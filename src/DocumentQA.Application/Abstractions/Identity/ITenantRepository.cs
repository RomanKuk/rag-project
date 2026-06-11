using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.Abstractions.Identity;

public interface ITenantRepository
{
    Task<Tenant?>               FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<Tenant?>               FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<Tenant>> ListAllAsync(CancellationToken ct = default);
    Task                        AddAsync(Tenant tenant, CancellationToken ct = default);
    Task                        SaveAsync(CancellationToken ct = default);
}
