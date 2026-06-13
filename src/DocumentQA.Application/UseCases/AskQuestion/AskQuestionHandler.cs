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
using DocumentQA.Domain.Retrieval;
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
    private readonly IAnswerGuardrail _citationGuardrail;
    private readonly IGroundednessCheck _groundednessCheck;
    private readonly ISafetyFilter _safetyFilter;
    private readonly IPiiRedactor _piiRedactor;
    private readonly ITokenCounter _tokenCounter;
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
        IAnswerGuardrail citationGuardrail,
        IGroundednessCheck groundednessCheck,
        ISafetyFilter safetyFilter,
        IPiiRedactor piiRedactor,
        ITokenCounter tokenCounter,
        IOptions<RagOptions> options,
        ILogger<AskQuestionHandler> logger)
    {
        _queryProcessor     = queryProcessor;
        _embedding          = embedding;
        _vectorStore        = vectorStore;
        _reranker           = reranker;
        _promptBuilder      = promptBuilder;
        _chat               = chat;
        _cache              = cache;
        _suspiciousLog      = suspiciousLog;
        _citationGuardrail  = citationGuardrail;
        _groundednessCheck  = groundednessCheck;
        _safetyFilter       = safetyFilter;
        _piiRedactor        = piiRedactor;
        _tokenCounter       = tokenCounter;
        _options            = options.Value;
        _logger             = logger;
    }

    // Chars held back from the live stream so a PII pattern that straddles token
    // boundaries (e.g. "123" + "-45-" + "6789") is fully assembled before flush.
    // Must exceed the longest maskable pattern; emails set the practical ceiling.
    private const int PiiTailKeep = 80;

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

        // ── Embed the RAW question first — used for the cache key ─────────
        // Order matters for cost: a cache hit must not pay the query-processor
        // LLM call, so expansion runs only after a cache miss.
        float[] rawVector;
        using (var embedActivity = RagActivitySource.Source.StartActivity("embed-query"))
        {
            rawVector = await _embedding.EmbedAsync(question, ct);
            embedActivity?.SetTag("embedding.dims", rawVector.Length);
        }

        // ── Semantic cache lookup ─────────────────────────────────────────
        string? cached;
        using (var cacheActivity = RagActivitySource.Source.StartActivity("cache-check"))
        {
            cached = await _cache.TryGetAsync(rawVector, scope, ct);
            var hit = cached is not null;
            cacheActivity?.SetTag("cache.hit", hit);
            rootActivity?.SetTag("cache.hit", hit);
        }

        if (cached is not null)
        {
            // Redact on the way out too — protects any legacy cache entries that
            // were stored before redaction was enabled.
            if (_options.PiiRedactionEnabled)
                cached = _piiRedactor.Redact(cached);
            foreach (var token in SplitIntoTokens(cached))
                yield return AskQuestionChunk.OfToken(token);
            yield return AskQuestionChunk.Done(new UsageSummary(
                _tokenCounter.Count(question), _tokenCounter.Count(cached),
                CacheHit: true, FallbackUsed: false,
                Model: modelFallbackChain[0]));
            yield break;
        }

        // ── Query processing (cache miss only — this may cost an LLM call) ─
        ProcessedQuery processed;
        float[] queryVector;
        using (var processActivity = RagActivitySource.Source.StartActivity("query-processing"))
        {
            processed = await _queryProcessor.ProcessAsync(question, ct);
            queryVector = processed.SearchText == question
                ? rawVector
                : await _embedding.EmbedAsync(processed.SearchText, ct);
            processActivity?.SetTag("keywords.count", processed.Keywords.Count);
            processActivity?.SetTag("intent", processed.Intent);
            processActivity?.SetTag("sub_queries.count", processed.SubQueries.Count);
            _logger.LogInformation(
                "Query processed: {KwCount} keywords, intent={Intent}, subQueries={SqCount}",
                processed.Keywords.Count, processed.Intent, processed.SubQueries.Count);
        }

        // ── Vector search (multi-query if sub-queries present) ────────────
        var candidates = processed.SubQueries.Count > 0
            ? await SearchMultiQueryAsync(question, processed, queryVector, scope, ct)
            : await SearchWithSpanAsync(queryVector, processed.Keywords, scope, ct);

        _logger.LogInformation("Search returned {Count} candidates (minScore={MinScore})",
            candidates.Count, _options.MinRelevanceScore);
        rootActivity?.SetTag("candidates.count", candidates.Count);

        if (candidates.Count == 0)
        {
            yield return AskQuestionChunk.NoContext();
            yield break;
        }

        IReadOnlyList<Domain.Retrieval.RetrievedChunk> ranked;
        using (var rerankActivity = RagActivitySource.Source.StartActivity("rerank"))
        {
            rerankActivity?.SetTag("candidates.in", candidates.Count);
            ranked = await _reranker.RerankAsync(question, candidates, _options.RerankTopN, ct);
            rerankActivity?.SetTag("candidates.out", ranked.Count);
        }

        var prompt = _promptBuilder.Build(question, ranked);
        yield return AskQuestionChunk.OfSources(prompt.Sources);

        // ── LLM streaming with model fallback ─────────────────────────────
        var accumulated = new StringBuilder();
        var usedModel = modelFallbackChain[0];
        var fallbackUsed = false;
        var redact = _options.PiiRedactionEnabled;
        // Holds the trailing chars not yet safe to flush (PII straddle guard).
        var pending = new StringBuilder();

        using (var llmActivity = RagActivitySource.Source.StartActivity("llm-completion"))
        {
            llmActivity?.SetTag("context.chunks", ranked.Count);

            // CS1626 workaround: yield return is not allowed inside try-catch.
            // Using GetAsyncEnumerator lets us catch MoveNextAsync failures while
            // yielding the token (the Current value) outside the catch block.
            foreach (var model in modelFallbackChain)
            {
                var failed = false;
                pending.Clear();
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
                        if (!redact)
                        {
                            accumulated.Append(token);
                            yield return AskQuestionChunk.OfToken(token);
                            continue;
                        }

                        // Redact the whole buffer, then flush everything except the
                        // last PiiTailKeep chars so a still-forming pattern stays in.
                        pending.Append(token);
                        if (pending.Length > PiiTailKeep)
                        {
                            var masked = _piiRedactor.Redact(pending.ToString());
                            var flushLen = masked.Length - PiiTailKeep;
                            if (flushLen > 0)
                            {
                                var toFlush = masked[..flushLen];
                                pending.Clear();
                                pending.Append(masked[flushLen..]);
                                accumulated.Append(toFlush);
                                yield return AskQuestionChunk.OfToken(toFlush);
                            }
                        }
                    }
                }

                if (!failed)
                {
                    // Flush the retained tail through a final redaction pass.
                    if (redact && pending.Length > 0)
                    {
                        var masked = _piiRedactor.Redact(pending.ToString());
                        accumulated.Append(masked);
                        yield return AskQuestionChunk.OfToken(masked);
                    }
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

        // ── Citation guardrail ────────────────────────────────────────────
        var citationVerdict = _citationGuardrail.Check(response, prompt.Sources);
        rootActivity?.SetTag("citation.passed", citationVerdict.Passed);
        if (!citationVerdict.Passed)
        {
            _logger.LogWarning("Citation guardrail failed: {Reason}", citationVerdict.Reason);
            response += "\n\n⚠️ Note: The answer may not be fully supported by the retrieved documents.";
        }

        // ── Groundedness check (config-gated — costs one LLM call) ────────
        if (_options.GroundednessEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var groundActivity = RagActivitySource.Source.StartActivity("groundedness-check");
                    var groundedness = await _groundednessCheck.CheckAsync(response, ranked, CancellationToken.None);
                    groundActivity?.SetTag("grounded", groundedness.IsGrounded);
                    _logger.LogInformation("Groundedness: {Grounded}", groundedness.IsGrounded);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Groundedness check failed");
                }
            });
        }

        // ── Safety filter on output ───────────────────────────────────────
        SafetyResult outputSafety;
        using (var safetyActivity = RagActivitySource.Source.StartActivity("safety-filter"))
        {
            outputSafety = await _safetyFilter.CheckAsync(response, ct);
            safetyActivity?.SetTag("flagged", !outputSafety.IsSafe);
            safetyActivity?.SetTag("category", outputSafety.Category);
        }
        rootActivity?.SetTag("safety.output.passed", outputSafety.IsSafe);
        if (!outputSafety.IsSafe)
        {
            _logger.LogWarning("Output safety filter flagged response as: {Category}", outputSafety.Category);
            _ = _suspiciousLog.LogResponseAsync(question, $"safety:{outputSafety.Category}");
        }

        // ── Yield usage summary ───────────────────────────────────────────
        var inputTokens  = _tokenCounter.Count(prompt.SystemPrompt) + _tokenCounter.Count(prompt.UserPrompt);
        var outputTokens = _tokenCounter.Count(response);
        rootActivity?.SetTag("input_tokens", inputTokens);
        rootActivity?.SetTag("output_tokens", outputTokens);
        yield return AskQuestionChunk.Done(new UsageSummary(
            inputTokens, outputTokens,
            CacheHit: false, FallbackUsed: fallbackUsed,
            Model: usedModel));

        // ── Cache store (fire-and-forget, don't fail the response) ────────
        // Keyed by the RAW question vector — must match the lookup above.
        // Skip caching "I cannot find" answers — caching them would cause all
        // semantically similar follow-up queries to receive the same null answer.
        const string NoInfoPhrase = "I cannot find this information";
        if (!response.Contains(NoInfoPhrase, StringComparison.OrdinalIgnoreCase))
            _ = _cache.StoreAsync(rawVector, question, response, scope, CancellationToken.None)
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

    private async Task<IReadOnlyList<Domain.Retrieval.RetrievedChunk>> SearchMultiQueryAsync(
        string question,
        ProcessedQuery processed,
        float[] queryVector,
        RetrievalScope scope,
        CancellationToken ct)
    {
        using var activity = RagActivitySource.Source.StartActivity("vector-search");
        activity?.SetTag("sub_queries.count", processed.SubQueries.Count);

        // Primary retrieval
        var primary = await _vectorStore.SearchHybridAsync(
            queryVector, processed.Keywords, _options.RetrievalTopK, _options.MinRelevanceScore, scope, ct);

        var allById = primary.ToDictionary(c => c.Chunk.Id);

        // Sub-query retrieval (if any) — each costs one extra embedding call
        var subIndex = 0;
        foreach (var subQuery in processed.SubQueries.Take(3))
        {
            try
            {
                var subVector = await _embedding.EmbedAsync(subQuery, ct);
                var subResults = await _vectorStore.SearchHybridAsync(
                    subVector, [], _options.RetrievalTopK / 2, _options.MinRelevanceScore, scope, ct);

                foreach (var r in subResults)
                    allById.TryAdd(r.Chunk.Id, r);
                activity?.SetTag($"sub_query.{subIndex}.results", subResults.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sub-query retrieval failed for '{SubQuery}'", subQuery);
            }
            subIndex++;
        }
        activity?.SetTag("embedding.extra_calls", subIndex);

        activity?.SetTag("results.count", allById.Count);
        return allById.Values.ToList();
    }

    private static IEnumerable<string> SplitIntoTokens(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
            yield return i < words.Length - 1 ? words[i] + " " : words[i];
    }
}
