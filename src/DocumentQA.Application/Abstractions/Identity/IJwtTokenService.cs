using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.Abstractions.Identity;

public interface IJwtTokenService
{
    string Issue(User user, string tenantSlug);
}
