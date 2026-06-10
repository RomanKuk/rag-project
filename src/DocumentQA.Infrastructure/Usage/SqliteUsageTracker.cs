using Dapper;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Models;
using Microsoft.Data.Sqlite;

namespace DocumentQA.Infrastructure.Usage;

public sealed class SqliteUsageTracker(string dbPath) : IUsageTracker
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS usage_logs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            request_id    TEXT    NOT NULL,
            api_key       TEXT    NOT NULL,
            tenant_id     TEXT    NOT NULL DEFAULT 'public',
            model         TEXT    NOT NULL,
            input_tokens  INTEGER NOT NULL,
            output_tokens INTEGER NOT NULL,
            cost_usd      REAL    NOT NULL,
            latency_ms    INTEGER NOT NULL,
            ttft_ms       INTEGER,
            cache_hit     INTEGER NOT NULL DEFAULT 0,
            fallback_used INTEGER NOT NULL DEFAULT 0,
            created_at    TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        )
        """;

    public async Task EnsureCreatedAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.ExecuteAsync(CreateSql);
        // Add tenant_id column to any existing DB that was created before this change
        try
        {
            await conn.ExecuteAsync("ALTER TABLE usage_logs ADD COLUMN tenant_id TEXT NOT NULL DEFAULT 'public'");
        }
        catch
        {
            // Column already exists — safe to ignore
        }
    }

    public async Task LogAsync(UsageRecord r, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.ExecuteAsync(
            "INSERT INTO usage_logs " +
            "(request_id,api_key,tenant_id,model,input_tokens,output_tokens,cost_usd,latency_ms,ttft_ms,cache_hit,fallback_used) " +
            "VALUES (@RequestId,@ApiKey,@TenantId,@Model,@InputTokens,@OutputTokens,@CostUsd,@LatencyMs,@TtftMs,@CacheHit,@FallbackUsed)",
            new
            {
                r.RequestId, r.ApiKey, r.TenantId, r.Model,
                r.InputTokens, r.OutputTokens,
                CostUsd = (double)r.CostUsd,
                r.LatencyMs, r.TtftMs,
                CacheHit     = r.CacheHit     ? 1 : 0,
                FallbackUsed = r.FallbackUsed ? 1 : 0,
            });
    }

    public async Task<TodayUsage> GetTodayAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        var row = await conn.QuerySingleAsync(
            "SELECT COUNT(*) requests, " +
            "COALESCE(SUM(input_tokens+output_tokens),0) tokens, " +
            "COALESCE(SUM(cost_usd),0) cost_usd " +
            "FROM usage_logs WHERE DATE(created_at)=DATE('now')");
        return new TodayUsage((int)row.requests, (long)row.tokens, (decimal)(double)row.cost_usd);
    }

    public async Task<IReadOnlyList<ModelBreakdown>> GetBreakdownAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        var rows = await conn.QueryAsync("""
            SELECT model,
                   COUNT(*)                                                      req,
                   COALESCE(SUM(input_tokens+output_tokens),0)                   tok,
                   COALESCE(SUM(cost_usd),0)                                     cost,
                   AVG(latency_ms)                                               avg_lat,
                   AVG(CASE WHEN cache_hit     THEN 1.0 ELSE 0.0 END)           chr,
                   AVG(CASE WHEN fallback_used THEN 1.0 ELSE 0.0 END)           fbr,
                   GROUP_CONCAT(latency_ms)                                      lats
            FROM usage_logs
            GROUP BY model
            """);

        return rows.Select(r =>
        {
            var lats = ((string?)r.lats ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).Order().ToArray();
            var p95 = lats.Length > 0
                ? (double?)lats[Math.Max(0, (int)(lats.Length * 0.95) - 1)]
                : null;
            return new ModelBreakdown(
                Model:         r.model,
                Requests:      (int)r.req,
                Tokens:        (long)r.tok,
                CostUsd:       (decimal)(double)r.cost,
                AvgLatencyMs:  (double)r.avg_lat,
                P95LatencyMs:  p95,
                CacheHitRate:  (double)r.chr,
                FallbackRate:  (double)r.fbr);
        }).ToList();
    }
}
