using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.UseCases.Owner;

public sealed class CreateUserHandler(
    IUserRepository userRepo,
    IPasswordHasher hasher)
{
    public async Task<CreateUserResult> HandleAsync(
        CreateUserRequest req, Guid tenantId, CancellationToken ct = default)
    {
        var existing = await userRepo.FindByEmailAsync(req.Email, ct);
        if (existing is not null)
            throw new InvalidOperationException($"Email '{req.Email}' is already registered.");

        var user = new User
        {
            TenantId     = tenantId,
            Email        = req.Email,
            PasswordHash = hasher.Hash(req.InitialPassword),
            Role         = Role.Member,
            DisplayName  = req.DisplayName ?? req.Email,
            IsActive     = true,
        };
        await userRepo.AddAsync(user, ct);
        await userRepo.SaveAsync(ct);

        return new CreateUserResult(user.Id, user.Email, user.DisplayName);
    }
}

public sealed record CreateUserRequest(
    string  Email,
    string  InitialPassword,
    string? DisplayName
);

public sealed record CreateUserResult(Guid UserId, string Email, string DisplayName);
