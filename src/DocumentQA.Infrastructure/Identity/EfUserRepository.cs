using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;
using DocumentQA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentQA.Infrastructure.Identity;

public sealed class EfUserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
        => db.Users.Include(u => u.Tenant)
               .FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant(), ct);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken ct = default)
        => db.Users.Include(u => u.Tenant)
               .FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<User>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default)
        => await db.Users
            .Where(u => u.TenantId == tenantId && u.IsActive)
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct);

    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        user.Email = user.Email.ToLowerInvariant();
        await db.Users.AddAsync(user, ct);
    }

    public Task SaveAsync(CancellationToken ct = default)
        => db.SaveChangesAsync(ct);
}
