namespace DocumentQA.Application.Abstractions.Usage;

public interface ITokenRateLimiter
{
    /// Returns current token count in this minute window. Does NOT increment.
    Task<int> GetCurrentUsageAsync(string apiKey, CancellationToken ct = default);

    /// Increments the bucket by tokens; sets 60-second expiry on the first call in a window.
    Task DeductAsync(string apiKey, int tokens, CancellationToken ct = default);
}
