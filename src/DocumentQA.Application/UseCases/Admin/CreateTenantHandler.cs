using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.UseCases.Admin;

public sealed class CreateTenantHandler(
    ITenantRepository tenantRepo,
    IUserRepository userRepo,
    IPasswordHasher hasher)
{
    public async Task<CreateTenantResult> HandleAsync(CreateTenantRequest req, CancellationToken ct = default)
    {
        var slug = req.Slug ?? req.Name.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        var existing = await tenantRepo.FindBySlugAsync(slug, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Tenant slug '{slug}' already exists.");

        var tenant = new Tenant
        {
            Name            = req.Name,
            Slug            = slug,
            IsActive        = true,
            DailyTokenLimit = req.DailyTokenLimit,
        };
        await tenantRepo.AddAsync(tenant, ct);
        await tenantRepo.SaveAsync(ct);

        var owner = new User
        {
            TenantId     = tenant.Id,
            Email        = req.OwnerEmail,
            PasswordHash = hasher.Hash(req.OwnerPassword),
            Role         = Role.Owner,
            DisplayName  = req.OwnerDisplayName ?? req.OwnerEmail,
            IsActive     = true,
        };
        await userRepo.AddAsync(owner, ct);
        await userRepo.SaveAsync(ct);

        return new CreateTenantResult(tenant.Id, tenant.Slug, owner.Id);
    }
}

public sealed record CreateTenantRequest(
    string  Name,
    string? Slug,
    string  OwnerEmail,
    string  OwnerPassword,
    string? OwnerDisplayName,
    int     DailyTokenLimit = 0
);

public sealed record CreateTenantResult(Guid TenantId, string Slug, Guid OwnerId);
