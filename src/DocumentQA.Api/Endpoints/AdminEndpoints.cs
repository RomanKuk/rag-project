using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.UseCases.Admin;
using DocumentQA.Domain.Identity;
using Microsoft.Extensions.Configuration;

namespace DocumentQA.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

        group.MapPost("/tenants", async (
            CreateTenantRequest req,
            CreateTenantHandler handler,
            IConfiguration config,
            CancellationToken ct) =>
        {
            try
            {
                var defaultLimit = config.GetValue<int>("Tenants:DefaultDailyTokenLimit", 100_000);
                var effective = req with { DailyTokenLimit = req.DailyTokenLimit > 0 ? req.DailyTokenLimit : defaultLimit };
                var result = await handler.HandleAsync(effective, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapGet("/tenants", async (
            ITenantRepository tenantRepo,
            CancellationToken ct) =>
        {
            var tenants = await tenantRepo.ListAllAsync(ct);
            return Results.Ok(tenants.Select(t => new
            {
                id              = t.Id,
                name            = t.Name,
                slug            = t.Slug,
                isActive        = t.IsActive,
                dailyTokenLimit = t.DailyTokenLimit,
                createdAt       = t.CreatedAt,
            }));
        });

        group.MapPatch("/tenants/{id:guid}", async (
            Guid id,
            UpdateTenantRequest req,
            ITenantRepository tenantRepo,
            CancellationToken ct) =>
        {
            var tenant = await tenantRepo.FindByIdAsync(id, ct);
            if (tenant is null) return Results.NotFound();

            if (req.DailyTokenLimit.HasValue)
                tenant.DailyTokenLimit = req.DailyTokenLimit.Value;
            if (req.IsActive.HasValue)
                tenant.IsActive = req.IsActive.Value;

            await tenantRepo.UpdateAsync(tenant, ct);
            await tenantRepo.SaveAsync(ct);
            return Results.Ok(new
            {
                id              = tenant.Id,
                name            = tenant.Name,
                slug            = tenant.Slug,
                isActive        = tenant.IsActive,
                dailyTokenLimit = tenant.DailyTokenLimit,
            });
        });

        group.MapGet("/metrics", async (
            IUsageAnalytics analytics,
            IVectorStore vectorStore,
            CancellationToken ct) =>
        {
            var overall    = await analytics.GetOverallMetricsAsync(ct);
            var perTenant  = overall.TopTenantsByCost;

            // Enrich with document + chunk counts from Qdrant (fire in parallel)
            var docCountTasks   = perTenant.Select(t => vectorStore.CountDocumentsAsync(t.TenantId, ct)).ToList();
            var chunkCountTasks = perTenant.Select(t => vectorStore.CountChunksAsync(t.TenantId, ct)).ToList();
            await Task.WhenAll(docCountTasks.Concat(chunkCountTasks));

            var enriched = perTenant
                .Select((t, i) => new
                {
                    tenantId      = t.TenantId,
                    tenantName    = t.TenantName,
                    requests      = t.Requests,
                    tokens        = t.Tokens,
                    costUsd       = t.CostUsd,
                    cacheHitRate  = t.CacheHitRate,
                    avgLatencyMs  = t.AvgLatencyMs,
                    p95LatencyMs  = t.P95LatencyMs,
                    userCount     = t.UserCount,
                    documentCount = (int)docCountTasks[i].Result,
                    chunkCount    = (int)chunkCountTasks[i].Result,
                })
                .ToList();

            return Results.Ok(new
            {
                totalTenants      = overall.TotalTenants,
                totalUsers        = overall.TotalUsers,
                totalRequests     = overall.TotalRequests,
                totalTokens       = overall.TotalTokens,
                totalCostUsd      = overall.TotalCostUsd,
                cacheHitRate      = overall.OverallCacheHitRate,
                topTenantsByCost  = enriched,
            });
        });

        group.MapGet("/metrics/tenants", async (
            IUsageAnalytics analytics,
            ITenantRepository tenantRepo,
            IVectorStore vectorStore,
            CancellationToken ct) =>
        {
            var perTenant = await analytics.GetTenantMetricsAsync(ct);
            var tenants   = await tenantRepo.ListAllAsync(ct);
            var tenantMap = tenants.ToDictionary(t => t.Slug);

            var docCountTasks   = perTenant.Select(t => vectorStore.CountDocumentsAsync(t.TenantId, ct)).ToList();
            var chunkCountTasks = perTenant.Select(t => vectorStore.CountChunksAsync(t.TenantId, ct)).ToList();
            await Task.WhenAll(docCountTasks.Concat(chunkCountTasks));

            var result = perTenant.Select((t, i) =>
            {
                var limit = tenantMap.TryGetValue(t.TenantId, out var ten) ? ten.DailyTokenLimit : 0;
                return new
                {
                    tenantId        = t.TenantId,
                    tenantName      = t.TenantName,
                    requests        = t.Requests,
                    tokens          = t.Tokens,
                    costUsd         = t.CostUsd,
                    cacheHitRate    = t.CacheHitRate,
                    avgLatencyMs    = t.AvgLatencyMs,
                    p95LatencyMs    = t.P95LatencyMs,
                    userCount       = t.UserCount,
                    documentCount   = (int)docCountTasks[i].Result,
                    chunkCount      = (int)chunkCountTasks[i].Result,
                    dailyTokenLimit = limit,
                };
            }).ToList();

            return Results.Ok(result);
        });

        group.MapGet("/metrics/timeseries", async (
            IUsageAnalytics analytics,
            int days = 30,
            string? tenantId = null,
            CancellationToken ct = default) =>
        {
            var buckets = await analytics.GetTimeSeriesAsync(days, tenantId, ct);
            return Results.Ok(buckets);
        });
    }
}

public sealed record UpdateTenantRequest(int? DailyTokenLimit, bool? IsActive);
