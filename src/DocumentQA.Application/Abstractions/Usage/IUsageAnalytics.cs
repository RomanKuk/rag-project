namespace DocumentQA.Application.Abstractions.Usage;

public interface IUsageAnalytics
{
    Task<IReadOnlyList<TenantMetrics>> GetTenantMetricsAsync(CancellationToken ct = default);
    Task<OverallMetrics>               GetOverallMetricsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DailyBucket>>   GetTimeSeriesAsync(int days, string? tenantId = null, CancellationToken ct = default);
}

public sealed record TenantMetrics(
    string  TenantId,
    string  TenantName,
    int     Requests,
    long    Tokens,
    decimal CostUsd,
    double  CacheHitRate,
    double  AvgLatencyMs,
    double? P95LatencyMs,
    int     UserCount,
    int     DocumentCount   // from Qdrant
);

public sealed record OverallMetrics(
    int     TotalTenants,
    int     TotalUsers,
    int     TotalRequests,
    long    TotalTokens,
    decimal TotalCostUsd,
    double  OverallCacheHitRate,
    IReadOnlyList<TenantMetrics> TopTenantsByCost
);

public sealed record DailyBucket(
    DateOnly Date,
    int      Requests,
    long     Tokens,
    decimal  CostUsd,
    double   CacheHitRate
);
