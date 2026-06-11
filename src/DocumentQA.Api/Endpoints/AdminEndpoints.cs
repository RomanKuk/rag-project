using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.UseCases.Admin;

namespace DocumentQA.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

        group.MapPost("/tenants", async (
            CreateTenantRequest req,
            CreateTenantHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var result = await handler.HandleAsync(req, ct);
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
                id        = t.Id,
                name      = t.Name,
                slug      = t.Slug,
                isActive  = t.IsActive,
                createdAt = t.CreatedAt,
            }));
        });

        group.MapGet("/metrics", async (
            IUsageAnalytics analytics,
            IVectorStore vectorStore,
            CancellationToken ct) =>
        {
            var overall    = await analytics.GetOverallMetricsAsync(ct);
            var perTenant  = overall.TopTenantsByCost;

            // Enrich with document counts from Qdrant (fire in parallel)
            var docCountTasks = perTenant
                .Select(t => vectorStore.CountDocumentsAsync(t.TenantId, ct))
                .ToList();
            var docCounts = await Task.WhenAll(docCountTasks);

            var enriched = perTenant
                .Zip(docCounts, (t, d) => t with { DocumentCount = (int)d })
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
