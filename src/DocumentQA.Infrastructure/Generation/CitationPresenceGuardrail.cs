using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Infrastructure.Generation;

public sealed class CitationPresenceGuardrail : IAnswerGuardrail
{
    public GuardrailVerdict Check(string answer, IReadOnlyList<Citation> availableSources)
        => availableSources.Any(s => answer.Contains(s.DocumentName, StringComparison.OrdinalIgnoreCase))
            ? new GuardrailVerdict(true, null)
            : new GuardrailVerdict(false, "Answer contains no citation to a known source.");
}
