namespace DocumentQA.Application.Abstractions.Security;

/// <summary>
/// Masks personally-identifiable information (SSNs, payment cards, phone
/// numbers, emails) in model output before it reaches the client. A defensive
/// complement to the safety-tuned model: a deterministic guarantee that holds
/// even if the model regresses.
/// </summary>
public interface IPiiRedactor
{
    /// <summary>Returns <paramref name="text"/> with any detected PII replaced by a tag.</summary>
    string Redact(string text);
}
