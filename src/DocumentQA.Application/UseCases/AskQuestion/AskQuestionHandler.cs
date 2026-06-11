using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Models;
using DocumentQA.Application.Options;
using DocumentQA.Application.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Application.UseCases.AskQuestion;

public sealed class AskQuestionHandler
{
    private static readonly string[] OutputForbiddenFragments =
    [
        "You are a document assistant",
        "Answer ONLY from the provided context",
        "Cite every claim inline",
        "Never use outside knowledge",
    ];

    private readonly IQueryProcessor _queryProcessor;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;
    private readonly IReranker _reranker;
    private readonly IPromptBuilder _promptBuilder;
    private readonly IChatCompletionPort _chat;
    private readonly ISemanticCache _cache;
    private readonly ISuspiciousActivityLog _suspiciousLog;
    private readonly RagOptions _options;
    private readonly ILogger<AskQuestionHandler> _logger;

    public AskQuestionHandler(
        IQueryProcessor queryProcessor,
        IEmbeddingPort embedding,
        IVectorStore vectorStore,
        IReranker reranker,
        IPromptBuilder promptBuilder,
        IChatCompletionPort chat,
        ISemanticCache cache,
        ISuspiciousActivityLog suspiciousLog,
        IOptions<RagOptions> options,
        ILogger<AskQuestionHandler> logger)
    {
        _queryProcessor = queryProcessor;
        _embedding = embedding;
        _vectorStore = vectorStore;
        _reranker = reranker;
        _promptBuilder = promptBuilder;
        _chat = chat;
        _cache = cache;
        _suspiciousLog = suspiciousLog;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<AskQuestionChunk> HandleAsync(
        string question,
        string[] modelFallbackChain,
        RetrievalScope scope,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var rootActivity = RagActivitySource.Source.StartActivity("ask-question");
        rootActivity?.SetTag("question.length", question.Length);
        rootActivity?.SetTag("tenant", scope.TenantId);
        rootActivity?.SetTag("scope.mode", scope.Mode.ToString());

        // ── Embed (one call — reused for both cache and RAG) ──────────────
        float[] queryVector;
        ProcessedQuery processed;
        using (var embedActivity = RagActivitySource.Source.StartActivity("embed-query"))
        {
            processed = await _queryProcessor.ProcessAsync(question, ct);
            queryVector = await _embedding.EmbedAsync(processed.SearchText, ct);
            embedActivity?.SetTag("embedding.dims", queryVector.Length);
            embedActivity?.SetTag("keywords.count", processed.Keywords.Count);
            _logger.LogInformation("Embedding generated ({Dims} dims), {KwCount} keywords extracted",
                queryVector.Length, processed.Keywords.Count);
        }

        // ── Semantic cache lookup ─────────────────────────────────────────
        string? cached;
        using (var cacheActivity = RagActivitySource.Source.StartActivity("cache-check"))
        {
            cached = await _cache.TryGetAsync(queryVector, scope, ct);
            var hit = cached is not null;
            cacheActivity?.SetTag("cache.hit", hit);
            rootActivity?.SetTag("cache.hit", hit);
        }

        if (cached is not null)
        {
            foreach (var token in SplitIntoTokens(cached))
                yield return AskQuestionChunk.OfToken(token);
            yield return AskQuestionChunk.Done(new UsageSummary(
                question.Length / 4, cached.Length / 4,
                CacheHit: true, FallbackUsed: false,
                Model: modelFallbackChain[0]));
            yield break;
        }

        // ── Vector search ─────────────────────────────────────────────────
        var candidates = await SearchWithSpanAsync(queryVector, processed.Keywords, scope, ct);

        _logger.LogInformation("Search returned {Count} candidates (minScore={MinScore})",
            candidates.Count, _options.MinRelevanceScore);
        rootActivity?.SetTag("candidates.count", candidates.Count);

        if (candidates.Count == 0)
        {
            yield return AskQuestionChunk.NoContext();
            yield break;
        }

        var ranked = await _reranker.RerankAsync(
            question, candidates, _options.RerankTopN, ct);

        var prompt = _promptBuilder.Build(question, ranked);
        yield return AskQuestionChunk.OfSources(prompt.Sources);

        // ── LLM streaming with model fallback ─────────────────────────────
        var accumulated = new StringBuilder();
        var usedModel = modelFallbackChain[0];
        var fallbackUsed = false;

        using (var llmActivity = RagActivitySource.Source.StartActivity("llm-completion"))
        {
            llmActivity?.SetTag("context.chunks", ranked.Count);

            // CS1626 workaround: yield return is not allowed inside try-catch.
            // Using GetAsyncEnumerator lets us catch MoveNextAsync failures while
            // yielding the token (the Current value) outside the catch block.
            foreach (var model in modelFallbackChain)
            {
                var failed = false;
                var enumerator = _chat.StreamAsync(prompt, model, ct).GetAsyncEnumerator(ct);
                await using (enumerator)
                {
                    while (true)
                    {
                        bool hasMore;
                        try
                        {
                            hasMore = await enumerator.MoveNextAsync();
                        }
                        catch (Exception ex) when (model != modelFallbackChain[^1])
                        {
                            _logger.LogWarning(ex, "Model {Model} failed, trying next in fallback chain", model);
                            failed = true;
                            break;
                        }

                        if (!hasMore) break;

                        var token = enumerator.Current;
                        accumulated.Append(token);
                        yield return AskQuestionChunk.OfToken(token);
                    }
                }

                if (!failed)
                {
                    usedModel = model;
                    fallbackUsed = model != modelFallbackChain[0];
                    break;
                }
            }

            llmActivity?.SetTag("model", usedModel);
            llmActivity?.SetTag("response.length", accumulated.Length);
        }

        var response = accumulated.ToString();

        // ── Output filtering ──────────────────────────────────────────────
        foreach (var fragment in OutputForbiddenFragments)
        {
            if (response.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Output filter triggered: fragment found in response");
                rootActivity?.SetTag("output_filtered", true);
                _ = _suspiciousLog.LogResponseAsync(question, fragment);
                break;
            }
        }

        // ── Yield usage summary ───────────────────────────────────────────
        var inputTokens  = (prompt.SystemPrompt.Length + prompt.UserPrompt.Length) / 4;
        var outputTokens = response.Length / 4;
        yield return AskQuestionChunk.Done(new UsageSummary(
            inputTokens, outputTokens,
            CacheHit: false, FallbackUsed: fallbackUsed,
            Model: usedModel));

        // ── Cache store (fire-and-forget, don't fail the response) ────────
        // Skip caching "I cannot find" answers — caching them would cause all
        // semantically similar follow-up queries to receive the same null answer.
        const string NoInfoPhrase = "I cannot find this information";
        if (!response.Contains(NoInfoPhrase, StringComparison.OrdinalIgnoreCase))
            _ = _cache.StoreAsync(queryVector, question, response, scope, CancellationToken.None)
                  .ContinueWith(t => _logger.LogWarning(t.Exception, "Cache store failed"),
                      TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<IReadOnlyList<Domain.Retrieval.RetrievedChunk>> SearchWithSpanAsync(
        float[] queryVector, IReadOnlyList<string> keywords, RetrievalScope scope, CancellationToken ct)
    {
        using var activity = RagActivitySource.Source.StartActivity("vector-search");
        activity?.SetTag("keywords.count", keywords.Count);
        var result = await _vectorStore.SearchHybridAsync(
            queryVector, keywords, _options.RetrievalTopK, _options.MinRelevanceScore, scope, ct);
        activity?.SetTag("results.count", result.Count);
        return result;
    }

    private static IEnumerable<string> SplitIntoTokens(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
            yield return i < words.Length - 1 ? words[i] + " " : words[i];
    }
}
