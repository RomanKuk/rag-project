using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Models;
using DocumentQA.Domain.Identity;
using DocumentQA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentQA.Infrastructure.Usage;

public sealed class PostgresUsageTracker(IDbContextFactory<AppDbContext> dbFactory) : IUsageTracker, IUsageAnalytics
{
    // ── IUsageTracker ──────────────────────────────────────────────────────────

    public async Task LogAsync(UsageRecord r, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.UsageLogs.Add(new UsageLog
        {
            RequestId    = r.RequestId,
            ApiKey       = r.ApiKey,
            TenantId     = r.TenantId,
            UserId       = r.UserId,
            Model        = r.Model,
            InputTokens  = r.InputTokens,
            OutputTokens = r.OutputTokens,
            CostUsd      = r.CostUsd,
            LatencyMs    = r.LatencyMs,
            TtftMs       = r.TtftMs,
            CacheHit     = r.CacheHit,
            FallbackUsed = r.FallbackUsed,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<TodayUsage> GetTodayAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateTime.UtcNow.Date;
        var row = await db.UsageLogs
            .Where(u => u.CreatedAt >= today)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Requests = g.Count(),
                Tokens   = g.Sum(u => (long)u.InputTokens + u.OutputTokens),
                CostUsd  = g.Sum(u => u.CostUsd),
            })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? new TodayUsage(0, 0, 0)
            : new TodayUsage(row.Requests, row.Tokens, row.CostUsd);
    }

    public async Task<IReadOnlyList<ModelBreakdown>> GetBreakdownAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.UsageLogs
            .GroupBy(u => u.Model)
            .Select(g => new
            {
                Model        = g.Key,
                Requests     = g.Count(),
                Tokens       = g.Sum(u => (long)u.InputTokens + u.OutputTokens),
                CostUsd      = g.Sum(u => u.CostUsd),
                AvgLatencyMs = g.Average(u => (double)u.LatencyMs),
                CacheHitRate = g.Average(u => u.CacheHit ? 1.0 : 0.0),
                FallbackRate = g.Average(u => u.FallbackUsed ? 1.0 : 0.0),
                Latencies    = g.Select(u => u.LatencyMs).ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var sorted = r.Latencies.Order().ToArray();
            var p95    = sorted.Length > 0
                ? (double?)sorted[Math.Max(0, (int)(sorted.Length * 0.95) - 1)]
                : null;
            return new ModelBreakdown(r.Model, r.Requests, r.Tokens, r.CostUsd,
                r.AvgLatencyMs, p95, r.CacheHitRate, r.FallbackRate);
        }).ToList();
    }

    // ── IUsageAnalytics ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TenantMetrics>> GetTenantMetricsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var usageByTenant = await db.UsageLogs
            .GroupBy(u => u.TenantId)
            .Select(g => new
            {
                TenantId     = g.Key,
                Requests     = g.Count(),
                Tokens       = g.Sum(u => (long)u.InputTokens + u.OutputTokens),
                CostUsd      = g.Sum(u => u.CostUsd),
                CacheHitRate = g.Average(u => u.CacheHit ? 1.0 : 0.0),
                AvgLatencyMs = g.Average(u => (double)u.LatencyMs),
                Latencies    = g.Select(u => u.LatencyMs).ToList(),
            })
            .ToListAsync(ct);

        var tenants = await db.Tenants.ToListAsync(ct);
        var userCounts = await db.Users
            .Where(u => u.TenantId != null)
            .GroupBy(u => u.TenantId!.Value)
            .Select(g => new { TenantId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var tenantMap      = tenants.ToDictionary(t => t.Slug, t => t);
        var userCountByGuid = userCounts.ToDictionary(u => u.TenantId, u => u.Count);

        return usageByTenant.Select(r =>
        {
            var sorted = r.Latencies.Order().ToArray();
            var p95    = sorted.Length > 0
                ? (double?)sorted[Math.Max(0, (int)(sorted.Length * 0.95) - 1)]
                : null;

            var tenantGuid   = tenantMap.TryGetValue(r.TenantId, out var t) ? t.Id : Guid.Empty;
            var userCount    = userCountByGuid.GetValueOrDefault(tenantGuid, 0);
            var tenantName   = tenantMap.TryGetValue(r.TenantId, out var tn) ? tn.Name : r.TenantId;

            return new TenantMetrics(
                r.TenantId, tenantName,
                r.Requests, r.Tokens, r.CostUsd,
                r.CacheHitRate, r.AvgLatencyMs, p95,
                userCount,
                0); // document count injected by AdminService from Qdrant
        }).ToList();
    }

    public async Task<OverallMetrics> GetOverallMetricsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var totals = await db.UsageLogs
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Requests     = g.Count(),
                Tokens       = g.Sum(u => (long)u.InputTokens + u.OutputTokens),
                CostUsd      = g.Sum(u => u.CostUsd),
                CacheHitRate = g.Average(u => u.CacheHit ? 1.0 : 0.0),
            })
            .FirstOrDefaultAsync(ct);

        var tenantCount = await db.Tenants.CountAsync(u => u.IsActive, ct);
        var userCount   = await db.Users.CountAsync(u => u.IsActive, ct);
        var perTenant   = await GetTenantMetricsAsync(ct);
        var topByCost   = perTenant.OrderByDescending(t => t.CostUsd).Take(5).ToList();

        return new OverallMetrics(
            tenantCount, userCount,
            totals?.Requests ?? 0,
            totals?.Tokens   ?? 0,
            totals?.CostUsd  ?? 0,
            totals?.CacheHitRate ?? 0,
            topByCost);
    }

    public async Task<long> GetTenantTokensTodayAsync(string tenantSlug, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = DateTime.UtcNow.Date;
        var total = await db.UsageLogs
            .Where(u => u.TenantId == tenantSlug && u.CreatedAt >= today)
            .SumAsync(u => (long)u.InputTokens + u.OutputTokens, ct);
        return total;
    }

    public async Task<IReadOnlyList<DailyBucket>> GetTimeSeriesAsync(
        int days, string? tenantId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var from = DateTime.UtcNow.Date.AddDays(-days + 1);

        var query = db.UsageLogs.Where(u => u.CreatedAt >= from);
        if (!string.IsNullOrEmpty(tenantId))
            query = query.Where(u => u.TenantId == tenantId);

        var rows = await query
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new
            {
                Date         = g.Key,
                Requests     = g.Count(),
                Tokens       = g.Sum(u => (long)u.InputTokens + u.OutputTokens),
                CostUsd      = g.Sum(u => u.CostUsd),
                CacheHitRate = g.Average(u => u.CacheHit ? 1.0 : 0.0),
            })
            .OrderBy(r => r.Date)
            .ToListAsync(ct);

        // fill gaps so the chart always has every day
        var result = new List<DailyBucket>();
        for (var d = from; d <= DateTime.UtcNow.Date; d = d.AddDays(1))
        {
            var row = rows.FirstOrDefault(r => r.Date == d);
            var only = DateOnly.FromDateTime(d);
            result.Add(row is null
                ? new DailyBucket(only, 0, 0, 0, 0)
                : new DailyBucket(only, row.Requests, row.Tokens, row.CostUsd, row.CacheHitRate));
        }
        return result;
    }
}
