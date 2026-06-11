using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.Abstractions.Identity;

public interface IUserRepository
{
    Task<User?>               FindByEmailAsync(string email, CancellationToken ct = default);
    Task<User?>               FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task                      AddAsync(User user, CancellationToken ct = default);
    Task                      SaveAsync(CancellationToken ct = default);
}
