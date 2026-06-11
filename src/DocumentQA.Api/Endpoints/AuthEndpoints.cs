using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.UseCases.Auth;
using Microsoft.AspNetCore.Mvc;

namespace DocumentQA.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (
            [FromBody] LoginRequest req,
            LoginHandler handler,
            CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(req, ct);
            return result is null
                ? Results.Unauthorized()
                : Results.Ok(result);
        });

        app.MapGet("/api/auth/me", (ICurrentUser user) =>
        {
            if (!user.IsAuthenticated)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                userId      = user.UserId,
                email       = user.Email,
                displayName = user.DisplayName,
                role        = user.Role.ToString(),
                tenantSlug  = user.TenantSlug,
            });
        })
        .RequireAuthorization();
    }
}
