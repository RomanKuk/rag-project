using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Api.Auth;

public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private ClaimsPrincipal? Principal => http.HttpContext?.User;

    public bool   IsAuthenticated => Principal?.Identity?.IsAuthenticated is true;
    public Guid   UserId          => Guid.TryParse(Claim(JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;
    public Guid?  TenantId        => null; // not stored as Guid in token — use TenantSlug to look up if needed
    public string TenantSlug      => Claim("tenant_id") ?? string.Empty;
    public Role   Role            => Enum.TryParse<Role>(Claim("role"), out var r) ? r : Role.Member;
    public string Email           => Claim(JwtRegisteredClaimNames.Email) ?? string.Empty;
    public string DisplayName     => Claim("display_name") ?? string.Empty;

    private string? Claim(string type)
        => Principal?.FindFirstValue(type);
}
