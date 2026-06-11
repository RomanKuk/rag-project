using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.UseCases.Owner;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Api.Endpoints;

public static class UsersEndpoints
{
    public static void MapUsersEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").RequireAuthorization("OwnerOrAdmin");

        group.MapPost("/", async (
            CreateUserRequest req,
            CreateUserHandler handler,
            ICurrentUser currentUser,
            ITenantRepository tenantRepo,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.Unauthorized();

            if (currentUser.Role != Role.Owner)
                return Results.Forbid();

            var tenant = await tenantRepo.FindBySlugAsync(currentUser.TenantSlug, ct);
            if (tenant is null)
                return Results.Problem("Tenant not found.", statusCode: 404);

            try
            {
                var result = await handler.HandleAsync(req, tenant.Id, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapGet("/", async (
            ICurrentUser currentUser,
            ITenantRepository tenantRepo,
            IUserRepository userRepo,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.Forbid();

            var tenant = await tenantRepo.FindBySlugAsync(currentUser.TenantSlug, ct);
            if (tenant is null)
                return Results.Problem("Tenant not found.", statusCode: 404);

            var users = await userRepo.ListByTenantAsync(tenant.Id, ct);
            return Results.Ok(users.Select(u => new
            {
                id          = u.Id,
                email       = u.Email,
                displayName = u.DisplayName,
                role        = u.Role.ToString(),
                isActive    = u.IsActive,
                createdAt   = u.CreatedAt,
            }));
        });
    }
}
