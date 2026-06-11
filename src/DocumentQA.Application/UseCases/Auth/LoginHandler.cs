using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.UseCases.Auth;

public sealed class LoginHandler(
    IUserRepository userRepo,
    ITenantRepository tenantRepo,
    IPasswordHasher hasher,
    IJwtTokenService jwt)
{
    public async Task<AuthResult?> HandleAsync(LoginRequest req, CancellationToken ct = default)
    {
        var user = await userRepo.FindByEmailAsync(req.Email, ct);
        if (user is null || !user.IsActive)
            return null;

        if (!hasher.Verify(req.Password, user.PasswordHash))
            return null;

        var tenantSlug = string.Empty;
        if (user.TenantId.HasValue)
        {
            var tenant = await tenantRepo.FindByIdAsync(user.TenantId.Value, ct);
            if (tenant is null || !tenant.IsActive)
                return null;
            tenantSlug = tenant.Slug;
        }

        var token = jwt.Issue(user, tenantSlug);
        return new AuthResult(token, user.Role.ToString(), tenantSlug, user.DisplayName, user.Id);
    }
}

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthResult(
    string Token,
    string Role,
    string TenantSlug,
    string DisplayName,
    Guid   UserId
);
