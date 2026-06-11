using DocumentQA.Api.Auth;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Usage;

namespace DocumentQA.Api.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/usage/today", async (IUsageTracker tracker, ICurrentUser user, HttpContext ctx, CancellationToken ct) =>
            RequireCredential(user, ctx) ?? Results.Ok(await tracker.GetTodayAsync(ct)))
            .AddEndpointFilter<ApiKeyFilter>();

        app.MapGet("/usage/breakdown", async (IUsageTracker tracker, ICurrentUser user, HttpContext ctx, CancellationToken ct) =>
            RequireCredential(user, ctx) ?? Results.Ok(await tracker.GetBreakdownAsync(ct)))
            .AddEndpointFilter<ApiKeyFilter>();
    }

    // Usage data requires a JWT or a configured API key — anonymous passthrough is not enough.
    private static IResult? RequireCredential(ICurrentUser user, HttpContext ctx) =>
        user.IsAuthenticated || ctx.Items.ContainsKey(ApiKeyFilter.TenantContextItemKey)
            ? null
            : Results.Unauthorized();
}
