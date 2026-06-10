using DocumentQA.Application.Abstractions.Usage;
using StackExchange.Redis;

namespace DocumentQA.Infrastructure.RateLimiting;

public sealed class RedisTokenRateLimiter(IConnectionMultiplexer redis) : ITokenRateLimiter
{
    private static string BucketKey(string apiKey) =>
        $"rate:{apiKey}:{DateTime.UtcNow:yyyyMMddHHmm}";

    public async Task<int> GetCurrentUsageAsync(string apiKey, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var val = await db.StringGetAsync(BucketKey(apiKey));
        return val.HasValue ? (int)val : 0;
    }

    public async Task DeductAsync(string apiKey, int tokens, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var key = BucketKey(apiKey);
        var newTotal = await db.StringIncrementAsync(key, tokens);
        // First increment in this window — set 60-second TTL so the key auto-expires.
        // Check and increment are separate (no Lua): Upstash Free Tier does not run scripts.
        // Minor overages under concurrent load are intentional and documented.
        if (newTotal == tokens)
            await db.KeyExpireAsync(key, TimeSpan.FromSeconds(60));
    }
}
