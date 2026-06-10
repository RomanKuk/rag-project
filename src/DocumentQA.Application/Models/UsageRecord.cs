namespace DocumentQA.Application.Models;

public sealed record UsageRecord(
    string RequestId,
    string ApiKey,
    string TenantId,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    int LatencyMs,
    int? TtftMs,
    bool CacheHit,
    bool FallbackUsed
);
