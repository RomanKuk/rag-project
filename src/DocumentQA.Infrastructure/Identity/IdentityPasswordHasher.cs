using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace DocumentQA.Infrastructure.Identity;

public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<User> _inner = new();

    public string Hash(string password)
        => _inner.HashPassword(null!, password);

    public bool Verify(string password, string hash)
        => _inner.VerifyHashedPassword(null!, hash, password)
               != PasswordVerificationResult.Failed;
}
