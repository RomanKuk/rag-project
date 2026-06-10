using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.UseCases.AskQuestion;

public sealed record AskQuestionChunk(
    string Type,
    string? Token,
    IReadOnlyList<Citation>? Sources,
    UsageSummary? Usage = null)
{
    public static AskQuestionChunk OfToken(string token) => new("token", token, null);
    public static AskQuestionChunk OfSources(IReadOnlyList<Citation> sources) => new("sources", null, sources);
    public static AskQuestionChunk NoContext() => new("no_context", null, null);
    public static AskQuestionChunk Done(UsageSummary usage) => new("done", null, null, usage);
}

public sealed record UsageSummary(
    int InputTokens,
    int OutputTokens,
    bool CacheHit,
    bool FallbackUsed,
    string Model
);
