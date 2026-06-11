namespace DocumentQA.Application.Abstractions.Security;

public interface ISafetyFilter
{
    Task<SafetyResult> CheckAsync(string text, CancellationToken ct);
}

public sealed record SafetyResult(bool IsSafe, string? Category);
