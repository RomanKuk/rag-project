using DocumentQA.Application.Models;
using Microsoft.Extensions.Configuration;

namespace DocumentQA.Api.Auth;

public sealed class ApiKeyFilter(IConfiguration config) : IEndpointFilter
{
    public const string TenantContextItemKey = "tenantContext";
    // Keep legacy keys for callers not yet migrated
    public const string TierItemKey   = "tier";
    public const string ApiKeyItemKey = "apiKey";

    private static readonly TierInfo PassThroughTier = new()
    {
        TokensPerMinute = int.MaxValue,
        Models = ["gpt-4o"],
    };

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var apiKeyMap = config.GetSection("ApiKeys").Get<Dictionary<string, string>>() ?? [];

        // JWT-authenticated requests bypass the API key check entirely.
        if (ctx.HttpContext.User?.Identity?.IsAuthenticated is true)
        {
            var anon = new TenantContext("jwt", PassThroughTier, "jwt");
            ctx.HttpContext.Items[TenantContextItemKey] = anon;
            return await next(ctx);
        }

        // Auth is opt-in: if no keys are configured, pass through (dev mode / existing UI).
        if (apiKeyMap.Count == 0)
        {
            var anon = new TenantContext("public", PassThroughTier, "anonymous");
            ctx.HttpContext.Items[TenantContextItemKey] = anon;
            ctx.HttpContext.Items[TierItemKey]          = PassThroughTier;
            ctx.HttpContext.Items[ApiKeyItemKey]        = "anonymous";
            return await next(ctx);
        }

        if (!ctx.HttpContext.Request.Headers.TryGetValue("X-API-Key", out var keyValues)
            || keyValues.Count == 0)
        {
            return Results.Json(new { detail = "Missing X-API-Key header" }, statusCode: 401);
        }

        var key      = keyValues.ToString();
        var tiersMap = config.GetSection("Tiers").Get<Dictionary<string, TierInfo>>() ?? [];
        var tenantsMap = config.GetSection("Tenants").Get<Dictionary<string, string>>() ?? [];

        if (!apiKeyMap.TryGetValue(key, out var tierKey) ||
            !tiersMap.TryGetValue(tierKey, out var tier))
        {
            return Results.Json(new { detail = "Invalid API key" }, statusCode: 401);
        }

        // Derive tenant: explicit Tenants map → fall back to the tier key → "public"
        var tenantId = tenantsMap.TryGetValue(key, out var tid) ? tid : tierKey;

        var tenantCtx = new TenantContext(tenantId, tier, key);
        ctx.HttpContext.Items[TenantContextItemKey] = tenantCtx;
        ctx.HttpContext.Items[TierItemKey]          = tier;
        ctx.HttpContext.Items[ApiKeyItemKey]        = key;
        return await next(ctx);
    }
}
