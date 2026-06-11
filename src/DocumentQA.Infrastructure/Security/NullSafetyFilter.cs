using DocumentQA.Application.Abstractions.Security;

namespace DocumentQA.Infrastructure.Security;

public sealed class NullSafetyFilter : ISafetyFilter
{
    public Task<SafetyResult> CheckAsync(string text, CancellationToken ct)
        => Task.FromResult(new SafetyResult(true, null));
}
