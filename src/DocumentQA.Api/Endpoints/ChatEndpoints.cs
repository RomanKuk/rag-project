using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentQA.Api.Auth;
using DocumentQA.Api.Services;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Models;
using DocumentQA.Application.UseCases.AskQuestion;
using Microsoft.Extensions.Logging;

namespace DocumentQA.Api.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app) =>
        app.MapPost("/api/chat", async (
            ChatRequest req,
            AskQuestionHandler handler,
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

            // ── Auth: resolve tier from filter (set by ApiKeyFilter) ────────
            var tier   = ctx.Items.TryGetValue(ApiKeyFilter.TierItemKey,  out var t) ? (TierInfo)t! : DefaultTier;
            var apiKey = ctx.Items.TryGetValue(ApiKeyFilter.ApiKeyItemKey, out var k) ? (string)k!  : "anonymous";

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
                await foreach (var chunk in handler.HandleAsync(req.Question, tier.Models, ctx.RequestAborted))
                {
                    if (ctx.RequestAborted.IsCancellationRequested) break;

                    if (chunk.Type == "done")
                    {
                        // Emit the enriched done event (cost, sources) instead of the raw chunk
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

                        // Fire-and-forget: log usage + deduct rate-limit tokens
                        var latencyMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var record    = new UsageRecord(
                            requestId, apiKey, usage.Model,
                            usage.InputTokens, usage.OutputTokens, cost,
                            latencyMs, ttftMs, usage.CacheHit, usage.FallbackUsed);
                        _ = Task.Run(async () =>
                        {
                            await usageTracker.LogAsync(record, CancellationToken.None);
                            await rateLimiter.DeductAsync(apiKey, usage.InputTokens + usage.OutputTokens, CancellationToken.None);
                        });
                        continue;
                    }

                    // Cache the sources list so we can embed it in the done event
                    if (chunk.Type == "sources")
                        cachedSources = chunk.Sources;

                    // Track time-to-first-token
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

    // Fallback tier used when the endpoint is called without an API key in config-less dev mode.
    // In production, ApiKeyFilter always enforces key presence.
    private static readonly TierInfo DefaultTier = new()
    {
        TokensPerMinute = int.MaxValue,
        Models = ["gpt-4o"],
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy          = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition        = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ChatRequest(string Question);
