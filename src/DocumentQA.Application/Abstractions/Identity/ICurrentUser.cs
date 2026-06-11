using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.Abstractions.Identity;

public interface ICurrentUser
{
    bool    IsAuthenticated { get; }
    Guid    UserId          { get; }
    Guid?   TenantId        { get; }
    string  TenantSlug      { get; }
    Role    Role            { get; }
    string  Email           { get; }
    string  DisplayName     { get; }
}
