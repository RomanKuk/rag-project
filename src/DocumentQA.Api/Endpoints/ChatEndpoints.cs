using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentQA.Api.Auth;
using DocumentQA.Api.Services;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Models;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.UseCases.AskQuestion;
using Microsoft.Extensions.Logging;

namespace DocumentQA.Api.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app) =>
        app.MapPost("/api/chat", async (
            ChatRequest req,
            AskQuestionHandler handler,
            IAgentOrchestrator orchestrator,
            IInputGuard guard,
            ISuspiciousActivityLog suspiciousLog,
            ITokenRateLimiter rateLimiter,
            IUsageTracker usageTracker,
            LlmGate gate,
            StreamMetrics metrics,
            ILoggerFactory loggerFactory,
            HttpContext ctx) =>
        {
            var logger = loggerFactory.CreateLogger("Api.Chat");

            // ── Auth: resolve tenant context from filter ────────────────────
            var tenantCtx = ctx.Items.TryGetValue(ApiKeyFilter.TenantContextItemKey, out var tc)
                ? (TenantContext)tc!
                : DefaultTenantContext;
            var tier    = tenantCtx.Tier;
            var apiKey  = tenantCtx.ApiKey;
            var tenantId = tenantCtx.TenantId;

            // ── Input validation ────────────────────────────────────────────
            var validation = guard.Validate(req.Question);
            if (!validation.IsAllowed)
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                logger.LogWarning("Blocked request from {IP}: {Reason}", ip, validation.Reason);
                await suspiciousLog.LogRequestAsync(req.Question, validation.Reason!, ip);
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid input.", reason = validation.Reason });
                return;
            }

            // ── Rate limit pre-check ────────────────────────────────────────
            var currentUsage = await rateLimiter.GetCurrentUsageAsync(apiKey, ctx.RequestAborted);
            if (currentUsage >= tier.TokensPerMinute)
            {
                var retryAfter = 60 - DateTime.UtcNow.Second;
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"] = retryAfter.ToString();
                await ctx.Response.WriteAsJsonAsync(new { detail = "Rate limit exceeded" });
                return;
            }

            // ── Concurrency gate ────────────────────────────────────────────
            try
            {
                await gate.AcquireAsync(ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                ctx.Response.StatusCode = 503;
                return;
            }

            // ── SSE setup ───────────────────────────────────────────────────
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var requestId = Guid.NewGuid().ToString();
            var start     = Stopwatch.GetTimestamp();
            int? ttftMs   = null;
            IReadOnlyList<Domain.Retrieval.Citation>? cachedSources = null;

            metrics.Increment();
            try
            {
                var history = req.History?.Select(h => new ConversationTurn(h.Role, h.Content)).ToList();
                var chunks = req.Agent
                    ? orchestrator.OrchestrateAsync(req.Question, tier.Models, tenantId, history, ctx.RequestAborted)
                    : handler.HandleAsync(req.Question, tier.Models, tenantId, ctx.RequestAborted);

                await foreach (var chunk in chunks)
                {
                    if (ctx.RequestAborted.IsCancellationRequested) break;

                    if (chunk.Type == "done")
                    {
                        var usage = chunk.Usage!;
                        var cost  = Pricing.Calculate(usage.Model, usage.InputTokens, usage.OutputTokens);
                        var doneEvent = new
                        {
                            type         = "done",
                            usage        = new { input_tokens = usage.InputTokens, output_tokens = usage.OutputTokens, model = usage.Model },
                            cost_usd     = cost,
                            cache_hit    = usage.CacheHit,
                            fallback_used = usage.FallbackUsed,
                            sources      = cachedSources ?? Array.Empty<Domain.Retrieval.Citation>(),
                        };
                        await ctx.Response.WriteAsync(
                            $"data: {JsonSerializer.Serialize(doneEvent, JsonOpts)}\n\n",
                            ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                        var latencyMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var record    = new UsageRecord(
                            requestId, apiKey, tenantId, usage.Model,
                            usage.InputTokens, usage.OutputTokens, cost,
                            latencyMs, ttftMs, usage.CacheHit, usage.FallbackUsed);
                        _ = Task.Run(async () =>
                        {
                            await usageTracker.LogAsync(record, CancellationToken.None);
                            await rateLimiter.DeductAsync(apiKey, usage.InputTokens + usage.OutputTokens, CancellationToken.None);
                        });
                        continue;
                    }

                    if (chunk.Type == "sources")
                        cachedSources = chunk.Sources;

                    if (chunk.Type == "token" && ttftMs is null)
                        ttftMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                    var json = JsonSerializer.Serialize(chunk, JsonOpts);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }

                await ctx.Response.WriteAsync("data: [DONE]\n\n");
                await ctx.Response.Body.FlushAsync();
            }
            catch (OperationCanceledException)
            {
                metrics.RecordAbort();
                logger.LogInformation("Client disconnected — stream aborted");
            }
            finally
            {
                metrics.Decrement();
                gate.Release();
            }
        })
        .AddEndpointFilter<ApiKeyFilter>();

    private static readonly TierInfo DefaultTier = new()
    {
        TokensPerMinute = int.MaxValue,
        Models = ["gpt-4o"],
    };

    private static readonly TenantContext DefaultTenantContext =
        new("public", DefaultTier, "anonymous");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy      = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ChatRequest(
    string Question,
    bool Agent = false,
    IReadOnlyList<HistoryEntry>? History = null
);

public sealed record HistoryEntry(string Role, string Content);
