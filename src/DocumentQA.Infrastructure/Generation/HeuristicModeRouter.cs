using DocumentQA.Application.Abstractions.Generation;

namespace DocumentQA.Infrastructure.Generation;

public sealed class HeuristicModeRouter : IModeRouter
{
    private static readonly string[] AgentMarkers =
    [
        "summarize", "summary", "summarise",
        "compare", "contrast", "difference between",
        "list all", "all documents", "across all", "across documents",
        "step by step", "step-by-step",
        "and then", "first", // multi-part chaining
    ];

    public bool ShouldUseAgent(string question)
    {
        var lower = question.ToLowerInvariant();

        var markerCount = AgentMarkers.Count(m => lower.Contains(m));
        if (markerCount >= 2) return true;

        // A single strong marker is enough for document-wide operations
        if (lower.Contains("summarize") || lower.Contains("summarise") || lower.Contains("summary"))
            return true;
        if (lower.Contains("list all") || lower.Contains("all documents") || lower.Contains("across all"))
            return true;
        if (lower.Contains("compare") && lower.Contains("contrast"))
            return true;

        // Multi-sentence/multi-part requests
        var segments = question.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3) return true;

        return false;
    }
}
