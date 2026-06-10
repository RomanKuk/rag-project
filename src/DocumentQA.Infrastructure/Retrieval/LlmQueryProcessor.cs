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
        You are a query expansion assistant for a document retrieval system.
        Given the user's question, return a JSON object with:
        - "search_text": an expanded version of the query including synonyms, full form of acronyms,
          and related official terminology that might appear in formal documents.
        - "keywords": a JSON array of exact strings to match (resolution numbers, policy names,
          proper-noun titles, document section headings). Include the original phrasing if it's a specific ID or name.
        Return ONLY valid JSON, no explanation.
        Example: {"search_text": "Remote Employment Policy telecommuting work from home rules", "keywords": ["Remote Employment Policy", "Telecommuting"]}
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

            var searchText = root.TryGetProperty("search_text", out var st)
                ? st.GetString() ?? rawQuery
                : rawQuery;

            var llmKeywords = root.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array
                ? kw.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToList()
                : [];

            // Merge deterministic + LLM keywords, deduplicated
            var allKeywords = deterministicKeywords
                .Union(llmKeywords, StringComparer.OrdinalIgnoreCase)
                .ToList();

            logger.LogInformation("Query expansion: '{Raw}' → search='{Search}', keywords={KwCount}",
                rawQuery[..Math.Min(60, rawQuery.Length)], searchText[..Math.Min(60, searchText.Length)], allKeywords.Count);

            return new ProcessedQuery(searchText, Keywords: allKeywords);
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
