using DocumentQA.Api.Auth;
using DocumentQA.Application.Abstractions.Usage;

namespace DocumentQA.Api.Endpoints;

public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/usage/today", async (IUsageTracker tracker, CancellationToken ct) =>
            Results.Ok(await tracker.GetTodayAsync(ct)))
            .AddEndpointFilter<ApiKeyFilter>();

        app.MapGet("/usage/breakdown", async (IUsageTracker tracker, CancellationToken ct) =>
            Results.Ok(await tracker.GetBreakdownAsync(ct)))
            .AddEndpointFilter<ApiKeyFilter>();
    }
}
