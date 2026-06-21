using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentQA.Api.Auth;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.UseCases.Admin;
using DocumentQA.Domain.Identity;
using DocumentQA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DocumentQA.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        // ── Eval results (POST accepts API key; GET requires admin JWT) ───────
        app.MapPost("/api/admin/eval-results", async (
            EvalResultRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            IConfiguration config,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            // Accept any configured API key (so eval.py can post without a JWT)
            var apiKeys = config.GetSection("ApiKeys").GetChildren()
                .ToDictionary(c => c.Key, c => c.Value ?? "");
            var headerKey = ctx.Request.Headers["X-API-Key"].FirstOrDefault() ?? "";
            var hasAdminJwt = ctx.User.HasClaim("role", "Admin");
            if (!hasAdminJwt && !apiKeys.ContainsKey(headerKey))
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.EvalResults.Add(new EvalResult
            {
                RunAt       = DateTime.UtcNow,
                Passed      = req.Passed,
                Mode        = req.Mode ?? "full",
                ResultsJson = JsonSerializer.Serialize(req),
            });
            await db.SaveChangesAsync(ct);
            ctx.Response.StatusCode = 200;
        }).AddEndpointFilter<ApiKeyFilter>();

        app.MapGet("/api/admin/eval-results", async (
            IDbContextFactory<AppDbContext> dbFactory,
            int limit = 10,
            CancellationToken ct = default) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.EvalResults
                .OrderByDescending(r => r.RunAt)
                .Take(limit)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new
            {
                id          = r.Id,
                runAt       = r.RunAt,
                passed      = r.Passed,
                mode        = r.Mode,
                results     = JsonSerializer.Deserialize<JsonElement>(r.ResultsJson),
            }));
        }).RequireAuthorization("AdminOnly");

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

        group.MapGet("/metrics/models", async (
            IUsageTracker usageTracker,
            CancellationToken ct) =>
        {
            var breakdown = await usageTracker.GetBreakdownAsync(ct);
            return Results.Ok(breakdown.Select(m => new
            {
                model        = m.Model,
                requests     = m.Requests,
                tokens       = m.Tokens,
                costUsd      = m.CostUsd,
                avgLatencyMs = m.AvgLatencyMs,
                p95LatencyMs = m.P95LatencyMs,
                cacheHitRate = m.CacheHitRate,
                fallbackRate = m.FallbackRate,
            }));
        });

        group.MapGet("/metrics/system", async (
            IConfiguration config,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            var prometheusBase = config["Prometheus:Url"] ?? "http://prometheus:9090";
            var qdrantHost     = config["Qdrant__Host"] ?? config["Qdrant:Host"] ?? "qdrant";
            var qdrantRestPort = config.GetValue<int>("Qdrant__RestPort", 6333);
            var http           = httpFactory.CreateClient();

            async Task<double?> PromQueryAsync(string promql)
            {
                try
                {
                    var url  = $"{prometheusBase}/api/v1/query?query={Uri.EscapeDataString(promql)}";
                    var json = await http.GetFromJsonAsync<PromResponse>(url, ct);
                    var val  = json?.Data?.Result?.FirstOrDefault()?.Value?[1];
                    return val is JsonElement e && double.TryParse(e.GetString(), out var d) ? d : null;
                }
                catch { return null; }
            }

            async Task<long?> QdrantTotalAsync()
            {
                try
                {
                    var url  = $"http://{qdrantHost}:{qdrantRestPort}/collections/documents";
                    var json = await http.GetFromJsonAsync<QdrantCollectionResponse>(url, ct);
                    return json?.Result?.PointsCount ?? json?.Result?.VectorsCount;
                }
                catch { return null; }
            }

            var promTask   = Task.WhenAll(
                PromQueryAsync("rag_active_streams"),
                PromQueryAsync("sum(increase(rag_guard_blocks_total[24h]))"),
                PromQueryAsync("histogram_quantile(0.95, sum(rate(rag_request_duration_seconds_bucket[5m])) by (le)) * 1000"),
                PromQueryAsync("histogram_quantile(0.95, sum(rate(rag_ttft_seconds_bucket[5m])) by (le)) * 1000"),
                PromQueryAsync("sum(rate(rag_requests_total[5m])) * 60"),
                PromQueryAsync("sum(increase(rag_cost_usd_total[24h]))")
            );
            var qdrantTask = QdrantTotalAsync();

            var r = await promTask;
            var totalVectors = await qdrantTask;

            return Results.Ok(new
            {
                activeStreams        = r[0].HasValue ? (int?)Math.Round(r[0]!.Value) : null,
                guardBlocks24h      = r[1].HasValue ? (int?)Math.Round(r[1]!.Value) : null,
                p95LatencyMs        = r[2].HasValue ? (double?)Math.Round(r[2]!.Value, 1) : null,
                p95TtftMs           = r[3].HasValue ? (double?)Math.Round(r[3]!.Value, 1) : null,
                requestsPerMinute   = r[4].HasValue ? (double?)Math.Round(r[4]!.Value, 2) : null,
                cost24h             = r[5].HasValue ? (double?)Math.Round(r[5]!.Value, 6) : null,
                totalChunks         = totalVectors,
                prometheusAvailable = r.Any(x => x.HasValue),
                qdrantAvailable     = totalVectors.HasValue,
            });
        });
    }
}

// Prometheus instant-query response shape
file sealed record PromResponse(string Status, PromData? Data);
file sealed record PromData([property: JsonPropertyName("resultType")] string ResultType,
                             PromResult[]? Result);
file sealed record PromResult(Dictionary<string, string> Metric, JsonElement[]? Value);

// Qdrant collection info response shape
file sealed record QdrantCollectionResponse(QdrantResult? Result);
file sealed record QdrantResult(
    [property: JsonPropertyName("points_count")]  long? PointsCount,
    [property: JsonPropertyName("vectors_count")] long? VectorsCount  // older Qdrant versions
);

public sealed record UpdateTenantRequest(int? DailyTokenLimit, bool? IsActive);

public sealed record EvalResultRequest(
    bool    Passed,
    string? Mode,
    object? Scores,
    double? RetrievalCoverage,
    string? Toxicity,
    bool?   ToolSelection,
    bool?   TenantIsolation,
    object? Safety,
    double? RefusalRecall,
    double? RefusalPrecision
);
