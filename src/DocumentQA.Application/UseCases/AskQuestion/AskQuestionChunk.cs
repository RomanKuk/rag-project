using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.UseCases.AskQuestion;

public sealed record AskQuestionChunk(
    string Type,
    string? Token,
    IReadOnlyList<Citation>? Sources)
{
    public static AskQuestionChunk OfToken(string token) => new("token", token, null);
    public static AskQuestionChunk OfSources(IReadOnlyList<Citation> sources) => new("sources", null, sources);
    public static AskQuestionChunk NoContext() => new("no_context", null, null);
}
