using DocumentQA.Application.Models;
using Microsoft.Extensions.Configuration;

namespace DocumentQA.Api.Auth;

public sealed class ApiKeyFilter(IConfiguration config) : IEndpointFilter
{
    public const string TierItemKey   = "tier";
    public const string ApiKeyItemKey = "apiKey";

    // Used when no ApiKeys section is configured (local dev / existing Angular UI).
    private static readonly TierInfo PassThroughTier = new()
    {
        TokensPerMinute = int.MaxValue,
        Models = ["gpt-4o"],
    };

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var apiKeyMap = config.GetSection("ApiKeys").Get<Dictionary<string, string>>() ?? [];

        // Auth is opt-in: if no keys are configured, pass through (dev mode / existing UI).
        if (apiKeyMap.Count == 0)
        {
            ctx.HttpContext.Items[TierItemKey]   = PassThroughTier;
            ctx.HttpContext.Items[ApiKeyItemKey] = "anonymous";
            return await next(ctx);
        }

        if (!ctx.HttpContext.Request.Headers.TryGetValue("X-API-Key", out var keyValues)
            || keyValues.Count == 0)
        {
            return Results.Json(new { detail = "Missing X-API-Key header" }, statusCode: 401);
        }

        var key      = keyValues.ToString();
        var tiersMap = config.GetSection("Tiers").Get<Dictionary<string, TierInfo>>() ?? [];

        if (!apiKeyMap.TryGetValue(key, out var tierKey) ||
            !tiersMap.TryGetValue(tierKey, out var tier))
        {
            return Results.Json(new { detail = "Invalid API key" }, statusCode: 401);
        }

        ctx.HttpContext.Items[TierItemKey]   = tier;
        ctx.HttpContext.Items[ApiKeyItemKey] = key;
        return await next(ctx);
    }
}
