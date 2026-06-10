using System.Text;
using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Options;
using DocumentQA.Domain.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Retrieval;

public sealed class LlmReranker(
    ICompletionPort completion,
    IOptions<RagOptions> options,
    ILogger<LlmReranker> logger) : IReranker
{
    private static readonly Regex NumberListPattern =
        new(@"\b(\d+)\b", RegexOptions.Compiled);

    public async Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken ct)
    {
        if (candidates.Count <= 1)
            return candidates.Take(topN).ToList();

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Query: {query}");
            sb.AppendLine();
            sb.AppendLine("Rank these document chunks by relevance (most relevant first).");
            sb.AppendLine($"Return ONLY a comma-separated list of the {topN} most relevant chunk numbers (1-indexed), e.g. \"3,1,5,2,4\"");
            sb.AppendLine();

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                // Truncate each candidate to ~300 chars to keep prompt small
                var preview = c.Chunk.Content.Length > 300
                    ? c.Chunk.Content[..300] + "…"
                    : c.Chunk.Content;
                sb.AppendLine($"[{i + 1}] [{c.Chunk.Metadata.DocumentName}, p{c.Chunk.Metadata.Page}] {preview}");
            }

            var response = await completion.CompleteAsync(
                "You are a relevance ranking assistant. Return only numbers separated by commas.",
                sb.ToString(),
                options.Value.UtilityModel,
                ct);

            var indices = NumberListPattern.Matches(response)
                .Select(m => int.Parse(m.Value) - 1)
                .Where(i => i >= 0 && i < candidates.Count)
                .Distinct()
                .Take(topN)
                .Select(i => candidates[i])
                .ToList();

            // If the LLM returned fewer than topN, append remaining candidates in original order
            if (indices.Count < topN)
            {
                var seen = indices.Select(c => c.Chunk.Id).ToHashSet();
                foreach (var c in candidates)
                {
                    if (indices.Count >= topN) break;
                    if (seen.Add(c.Chunk.Id))
                        indices.Add(c);
                }
            }

            logger.LogInformation("LLM reranker selected {Count}/{Total} candidates", indices.Count, candidates.Count);
            return indices;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LLM reranker failed, falling back to identity ordering");
            return candidates.Take(topN).ToList();
        }
    }
}
