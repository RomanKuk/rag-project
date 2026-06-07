using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Options;
using DocumentQA.Application.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Application.UseCases.AskQuestion;

public sealed class AskQuestionHandler
{
    // System prompt phrases that must never appear verbatim in the LLM output
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var rootActivity = RagActivitySource.Source.StartActivity("ask-question");
        rootActivity?.SetTag("question.length", question.Length);

        // ── Embed (one call — reused for both cache and RAG) ──────────────
        float[] queryVector;
        using (var embedActivity = RagActivitySource.Source.StartActivity("embed-query"))
        {
            var processed = await _queryProcessor.ProcessAsync(question, ct);
            queryVector = await _embedding.EmbedAsync(processed.SearchText, ct);
            embedActivity?.SetTag("embedding.dims", queryVector.Length);
            _logger.LogInformation("Embedding generated ({Dims} dims)", queryVector.Length);
        }

        // ── Semantic cache lookup ─────────────────────────────────────────
        string? cached;
        using (var cacheActivity = RagActivitySource.Source.StartActivity("cache-check"))
        {
            cached = await _cache.TryGetAsync(queryVector, ct);
            var hit = cached is not null;
            cacheActivity?.SetTag("cache.hit", hit);
            rootActivity?.SetTag("cache.hit", hit);
        }

        if (cached is not null)
        {
            foreach (var token in SplitIntoTokens(cached))
                yield return AskQuestionChunk.OfToken(token);
            yield break;
        }

        // ── Vector search ─────────────────────────────────────────────────
        var candidates = await SearchWithSpanAsync(queryVector, ct);

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

        // ── LLM streaming + accumulate for cache & output filter ──────────
        var accumulated = new StringBuilder();
        using (var llmActivity = RagActivitySource.Source.StartActivity("llm-completion"))
        {
            llmActivity?.SetTag("model", "gpt-4o");
            llmActivity?.SetTag("context.chunks", ranked.Count);

            await foreach (var token in _chat.StreamAsync(prompt, ct))
            {
                accumulated.Append(token);
                yield return AskQuestionChunk.OfToken(token);
            }

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

        // ── Cache store (fire-and-forget, don't fail the response) ────────
        _ = _cache.StoreAsync(queryVector, question, response, CancellationToken.None)
              .ContinueWith(t => _logger.LogWarning(t.Exception, "Cache store failed"),
                  TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task<IReadOnlyList<Domain.Retrieval.RetrievedChunk>> SearchWithSpanAsync(
        float[] queryVector, CancellationToken ct)
    {
        using var activity = RagActivitySource.Source.StartActivity("vector-search");
        var result = await _vectorStore.SearchAsync(
            queryVector, _options.RetrievalTopK, _options.MinRelevanceScore, ct);
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
