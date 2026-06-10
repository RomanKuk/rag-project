using DocumentQA.Application.Models;

namespace DocumentQA.Application.Abstractions.Usage;

public interface IUsageTracker
{
    Task LogAsync(UsageRecord record, CancellationToken ct = default);
    Task<TodayUsage> GetTodayAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ModelBreakdown>> GetBreakdownAsync(CancellationToken ct = default);
}

public sealed record TodayUsage(int Requests, long Tokens, decimal CostUsd);

public sealed record ModelBreakdown(
    string Model,
    int Requests,
    long Tokens,
    decimal CostUsd,
    double AvgLatencyMs,
    double? P95LatencyMs,
    double CacheHitRate,
    double FallbackRate
);
