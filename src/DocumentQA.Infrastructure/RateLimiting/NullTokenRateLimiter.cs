using DocumentQA.Application.Abstractions.Usage;

namespace DocumentQA.Infrastructure.RateLimiting;

/// No-op rate limiter used when Redis is not configured (local dev).
public sealed class NullTokenRateLimiter : ITokenRateLimiter
{
    public Task<int> GetCurrentUsageAsync(string apiKey, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task DeductAsync(string apiKey, int tokens, CancellationToken ct = default) =>
        Task.CompletedTask;
}
