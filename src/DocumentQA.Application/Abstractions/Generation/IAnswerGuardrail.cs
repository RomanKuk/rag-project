using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Generation;

public interface IAnswerGuardrail
{
    GuardrailVerdict Check(string answer, IReadOnlyList<Citation> availableSources);
}

public sealed record GuardrailVerdict(bool Passed, string? Reason);
