using System.Text.Json;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Api.Services;
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
            LlmGate gate,
            StreamMetrics metrics,
            ILoggerFactory loggerFactory,
            HttpContext ctx) =>
        {
            var logger = loggerFactory.CreateLogger("Api.Chat");
            // ── Input validation ────────────────────────────────────────
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

            // ── Concurrency gate ────────────────────────────────────────
            try
            {
                await gate.AcquireAsync(ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                ctx.Response.StatusCode = 503;
                return;
            }

            // ── SSE setup ───────────────────────────────────────────────
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            metrics.Increment();
            try
            {
                await foreach (var chunk in handler.HandleAsync(req.Question, ctx.RequestAborted))
                {
                    if (ctx.RequestAborted.IsCancellationRequested) break;
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
        });

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed record ChatRequest(string Question);
