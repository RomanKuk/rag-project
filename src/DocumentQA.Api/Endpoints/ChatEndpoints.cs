using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentQA.Api.Auth;
using DocumentQA.Api.Services;
using DocumentQA.Application.Abstractions.Chat;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Models;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Options;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Domain.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Api.Endpoints;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app) =>
        app.MapPost("/api/chat", async (
            [FromBody] ChatRequest req,
            AskQuestionHandler handler,
            IAgentOrchestrator orchestrator,
            IModeRouter modeRouter,
            IInputGuard guard,
            ISuspiciousActivityLog suspiciousLog,
            ITokenRateLimiter rateLimiter,
            IUsageTracker usageTracker,
            IUsageAnalytics usageAnalytics,
            ICurrentUser currentUser,
            IChatSessionRepository sessionRepo,
            ITenantRepository tenantRepo,
            IModelRouter modelRouter,
            IOptions<RagOptions> ragOpts,
            IServiceScopeFactory scopeFactory,
            LlmGate gate,
            StreamMetrics metrics,
            RagMetrics ragMetrics,
            ILoggerFactory loggerFactory,
            HttpContext ctx) =>
        {
            var logger = loggerFactory.CreateLogger("Api.Chat");

            // ── Auth gate: JWT required for chat ────────────────────────────
            // API key is still accepted for eval harness (anonymous session = shared scope)
            var hasJwt    = currentUser.IsAuthenticated;
            var hasApiKey = ctx.Items.ContainsKey(ApiKeyFilter.TenantContextItemKey);

            if (!hasJwt && !hasApiKey)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Authentication required." });
                return;
            }

            // ── Resolve scope ────────────────────────────────────────────────
            RetrievalScope scope;
            Domain.Chat.ChatSession? session = null;

            if (hasJwt && req.SessionId.HasValue)
            {
                session = await sessionRepo.GetAsync(req.SessionId.Value, ctx.RequestAborted);
                if (session is null || session.UserId != currentUser.UserId || session.TenantId != currentUser.TenantSlug)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Chat session not found." });
                    return;
                }
                scope = RetrievalScope.ForChat(
                    currentUser.TenantSlug, currentUser.UserId.ToString(),
                    session.Id, session.IncludeSharedDocs);
            }
            else if (hasJwt)
            {
                scope = RetrievalScope.SharedFor(currentUser.TenantSlug);
            }
            else
            {
                // API key path — legacy shared scope
                var tenantCtx = ctx.Items.TryGetValue(ApiKeyFilter.TenantContextItemKey, out var tc)
                    ? (TenantContext)tc! : null;
                scope = RetrievalScope.ForApiKey(tenantCtx?.TenantId ?? "public");
            }

            var apiKey = hasJwt ? currentUser.Email : "anonymous";
            var userId = hasJwt ? currentUser.UserId : (Guid?)null;

            // ── Resolve tier ─────────────────────────────────────────────────
            TierInfo tier;
            if (ctx.Items.TryGetValue(ApiKeyFilter.TenantContextItemKey, out var tcv) && tcv is TenantContext tenantCtxVal)
                tier = tenantCtxVal.Tier;
            else
                tier = new TierInfo
                {
                    TokensPerMinute = ragOpts.Value.JwtTokensPerMinute,
                    Models          = [ragOpts.Value.ComplexModel, ragOpts.Value.SimpleModel],
                };

            // ── Daily quota gate (JWT path) ──────────────────────────────────
            if (hasJwt)
            {
                var tenant = await tenantRepo.FindBySlugAsync(currentUser.TenantSlug, ctx.RequestAborted);
                if (tenant is { DailyTokenLimit: > 0 })
                {
                    var usedToday = await usageAnalytics.GetTenantTokensTodayAsync(currentUser.TenantSlug, ctx.RequestAborted);
                    if (usedToday >= tenant.DailyTokenLimit)
                    {
                        ctx.Response.StatusCode = 429;
                        await ctx.Response.WriteAsJsonAsync(new
                        {
                            error         = "Daily token quota exceeded.",
                            limit         = tenant.DailyTokenLimit,
                            usedToday     = usedToday,
                            retryAfterUtc = DateTime.UtcNow.Date.AddDays(1).ToString("O"),
                        });
                        return;
                    }
                }
            }

            // ── Input validation ─────────────────────────────────────────────
            var validation = guard.Validate(req.Question);
            if (!validation.IsAllowed)
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                logger.LogWarning("Blocked request from {IP}: {Reason}", ip, validation.Reason);
                await suspiciousLog.LogRequestAsync(req.Question, validation.Reason!, ip);
                ragMetrics.RecordGuardBlock();
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid input.", reason = validation.Reason });
                return;
            }

            // ── Rate limit pre-check ─────────────────────────────────────────
            var currentUsage = await rateLimiter.GetCurrentUsageAsync(apiKey, ctx.RequestAborted);
            if (currentUsage >= tier.TokensPerMinute)
            {
                var retryAfter = 60 - DateTime.UtcNow.Second;
                ctx.Response.StatusCode = 429;
                ctx.Response.Headers["Retry-After"] = retryAfter.ToString();
                await ctx.Response.WriteAsJsonAsync(new { error = "Rate limit exceeded" });
                return;
            }

            // ── Persist user message (fire before stream) ────────────────────
            if (session is not null)
            {
                await sessionRepo.AddMessageAsync(new ChatMessage
                {
                    SessionId = session.Id,
                    Role      = "user",
                    Content   = req.Question,
                }, ctx.RequestAborted);
            }

            // ── Concurrency gate ─────────────────────────────────────────────
            try { await gate.AcquireAsync(ctx.RequestAborted); }
            catch (OperationCanceledException) { ctx.Response.StatusCode = 503; return; }

            // ── SSE setup ────────────────────────────────────────────────────
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection    = "keep-alive";

            var requestId          = Guid.NewGuid().ToString();
            var assistantMessageId = Guid.NewGuid();
            var start     = Stopwatch.GetTimestamp();
            int? ttftMs   = null;
            IReadOnlyList<Domain.Retrieval.Citation>? cachedSources = null;
            var responseBuffer = new System.Text.StringBuilder();
            int inputTokCount = 0, outputTokCount = 0;
            decimal costAccum = 0;

            metrics.Increment();
            try
            {
                // History trimming: cap at MaxHistoryTurns pairs (user+assistant = 2 messages each)
                var maxHistory = ragOpts.Value.MaxHistoryTurns * 2;
                var history = req.History?
                    .TakeLast(maxHistory)
                    .Select(h => new ConversationTurn(h.Role, h.Content))
                    .ToList();

                // Model routing: pick cheap vs strong model based on query complexity
                var routedModels = modelRouter.Route(req.Question, tier.Models);

                var useAgent = req.Agent ?? modeRouter.ShouldUseAgent(req.Question);
                var chunks = useAgent
                    ? orchestrator.OrchestrateAsync(req.Question, routedModels, scope, history, ctx.RequestAborted)
                    : handler.HandleAsync(req.Question, routedModels, scope, ctx.RequestAborted);

                await foreach (var chunk in chunks)
                {
                    if (ctx.RequestAborted.IsCancellationRequested) break;

                    if (chunk.Type == "done")
                    {
                        var usage = chunk.Usage!;
                        var cost  = Pricing.Calculate(usage.Model, usage.InputTokens, usage.OutputTokens);
                        inputTokCount  = usage.InputTokens;
                        outputTokCount = usage.OutputTokens;
                        costAccum      = cost;

                        var doneEvent = new
                        {
                            type          = "done",
                            message_id    = session is not null ? assistantMessageId : (Guid?)null,
                            usage         = new { input_tokens = usage.InputTokens, output_tokens = usage.OutputTokens, model = usage.Model },
                            cost_usd      = cost,
                            cache_hit     = usage.CacheHit,
                            fallback_used = usage.FallbackUsed,
                            sources       = cachedSources ?? Array.Empty<Domain.Retrieval.Citation>(),
                            mode          = useAgent ? "agent" : "rag",
                        };
                        await ctx.Response.WriteAsync(
                            $"data: {JsonSerializer.Serialize(doneEvent, JsonOpts)}\n\n",
                            ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                        var latencyMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        var record    = new UsageRecord(
                            requestId, apiKey, scope.TenantId, usage.Model,
                            usage.InputTokens, usage.OutputTokens, cost,
                            latencyMs, ttftMs, usage.CacheHit, usage.FallbackUsed,
                            UserId: userId);

                        // Prometheus: synchronous in-memory record (NOT in the
                        // background persistence scope, which lacks guard context).
                        ragMetrics.RecordRequest(
                            scope.TenantId, usage.Model, usage.CacheHit,
                            usage.InputTokens, usage.OutputTokens, cost,
                            latencyMs / 1000.0,
                            ttftMs.HasValue ? ttftMs.Value / 1000.0 : null);

                        var sourcesJson = cachedSources is { Count: > 0 }
                            ? JsonSerializer.Serialize(cachedSources, JsonOpts)
                            : null;

                        // Background persistence MUST use its own DI scope — the request
                        // scope (and its DbContext) is disposed once the response completes.
                        var sessionId       = session?.Id;
                        var responseContent = responseBuffer.ToString();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await using var bgScope = scopeFactory.CreateAsyncScope();
                                var bgTracker     = bgScope.ServiceProvider.GetRequiredService<IUsageTracker>();
                                var bgRateLimiter = bgScope.ServiceProvider.GetRequiredService<ITokenRateLimiter>();

                                await bgTracker.LogAsync(record, CancellationToken.None);
                                await bgRateLimiter.DeductAsync(apiKey, usage.InputTokens + usage.OutputTokens, CancellationToken.None);

                                if (sessionId is not null)
                                {
                                    var bgSessionRepo = bgScope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
                                    await bgSessionRepo.AddMessageAsync(new ChatMessage
                                    {
                                        Id           = assistantMessageId,
                                        SessionId    = sessionId.Value,
                                        Role         = "assistant",
                                        Content      = responseContent,
                                        SourcesJson  = sourcesJson,
                                        InputTokens  = usage.InputTokens,
                                        OutputTokens = usage.OutputTokens,
                                        CostUsd      = cost,
                                    }, CancellationToken.None);
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Background persistence failed for request {RequestId}", requestId);
                            }
                        });
                        continue;
                    }

                    if (chunk.Type == "sources")
                        cachedSources = chunk.Sources;

                    if (chunk.Type == "token" && chunk.Token is not null)
                    {
                        responseBuffer.Append(chunk.Token);
                        if (ttftMs is null)
                            ttftMs = (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    }

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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record ChatRequest(
    string Question,
    bool?  Agent     = null,
    Guid?  SessionId = null,
    IReadOnlyList<HistoryEntry>? History = null
);

public sealed record HistoryEntry(string Role, string Content);
