using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Retrieval;

public sealed class LlmQueryProcessor(
    ICompletionPort completion,
    IOptions<RagOptions> options,
    ILogger<LlmQueryProcessor> logger) : IQueryProcessor
{
    // Deterministic regex extractions that complement the LLM pass
    private static readonly Regex ResolutionPattern =
        new(@"RESOLUTION\s+NO\.?\s*[\d/\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IdPattern =
        new(@"\b\d+/\d+-\d+\b", RegexOptions.Compiled);

    private const string SystemPrompt = """
        You are a query analysis assistant for a document retrieval system.
        Given the user's question, return a JSON object with:
        - "search_text": expanded query including synonyms, acronyms, related official terminology.
        - "keywords": array of exact strings to match (resolution numbers, policy names, section headings, IDs).
        - "intent": one of "qa" (factual question), "summary" (summarize a doc/topic), "comparison" (compare items), "lookup" (find specific item).
        - "sub_queries": if the question has multiple parts, list each as a separate question string. Empty array for single-part questions.
        Return ONLY valid JSON, no explanation.
        Example: {"search_text": "Remote Employment Policy telecommuting work from home", "keywords": ["Remote Employment Policy"], "intent": "qa", "sub_queries": []}
        """;

    public async Task<ProcessedQuery> ProcessAsync(string rawQuery, CancellationToken ct)
    {
        // Always extract deterministic keywords (free, no latency cost)
        var deterministicKeywords = ExtractDeterministicKeywords(rawQuery);

        if (!options.Value.QueryExpansionEnabled)
            return new ProcessedQuery(rawQuery, Keywords: deterministicKeywords);

        try
        {
            var raw = await completion.CompleteAsync(
                SystemPrompt,
                rawQuery,
                options.Value.UtilityModel,
                ct);

            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Fall back to the raw query when the LLM omits search_text OR returns
            // an empty/whitespace string — embedding "" makes the OpenAI API 400.
            var searchText = root.TryGetProperty("search_text", out var st)
                             && !string.IsNullOrWhiteSpace(st.GetString())
                ? st.GetString()!
                : rawQuery;

            var intent = root.TryGetProperty("intent", out var intentEl)
                ? intentEl.GetString() ?? "qa"
                : "qa";

            var llmKeywords = root.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array
                ? kw.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToList()
                : (IReadOnlyList<string>)[];

            var subQueries = root.TryGetProperty("sub_queries", out var sq) && sq.ValueKind == JsonValueKind.Array
                ? sq.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToList()
                : (IReadOnlyList<string>)[];

            // Merge deterministic + LLM keywords, deduplicated
            var allKeywords = deterministicKeywords
                .Union(llmKeywords, StringComparer.OrdinalIgnoreCase)
                .ToList();

            logger.LogInformation(
                "Query processed: intent={Intent}, search='{Search}', keywords={KwCount}, subQueries={SqCount}",
                intent, searchText[..Math.Min(60, searchText.Length)], allKeywords.Count, subQueries.Count);

            return new ProcessedQuery(searchText, intent, allKeywords, subQueries);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query expansion failed, using raw query with deterministic keywords");
            return new ProcessedQuery(rawQuery, Keywords: deterministicKeywords);
        }
    }

    private static IReadOnlyList<string> ExtractDeterministicKeywords(string query)
    {
        var results = new List<string>();
        foreach (Match m in ResolutionPattern.Matches(query))
            results.Add(m.Value.Trim());
        foreach (Match m in IdPattern.Matches(query))
            results.Add(m.Value.Trim());
        return results;
    }

    // Extracts the first JSON object from a string (handles markdown code fences)
    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }
}
