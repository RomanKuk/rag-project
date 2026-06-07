using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Generation;

public interface IPromptBuilder
{
    PromptBundle Build(string question, IReadOnlyList<RetrievedChunk> context);
}

public sealed record PromptBundle(
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<Citation> Sources);
