using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;
using DocumentQA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentQA.Infrastructure.Identity;

public sealed class EfTenantRepository(AppDbContext db) : ITenantRepository
{
    public Task<Tenant?> FindByIdAsync(Guid id, CancellationToken ct = default)
        => db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<Tenant?> FindBySlugAsync(string slug, CancellationToken ct = default)
        => db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);

    public async Task<IReadOnlyList<Tenant>> ListAllAsync(CancellationToken ct = default)
        => await db.Tenants.OrderBy(t => t.Name).ToListAsync(ct);

    public async Task AddAsync(Tenant tenant, CancellationToken ct = default)
        => await db.Tenants.AddAsync(tenant, ct);

    public Task UpdateAsync(Tenant tenant, CancellationToken ct = default)
    {
        db.Tenants.Update(tenant);
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken ct = default)
        => db.SaveChangesAsync(ct);
}
