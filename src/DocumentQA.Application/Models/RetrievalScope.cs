using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.Models;

public enum ScopeMode { Shared, Private }

public sealed record RetrievalScope(
    string     TenantId,
    string?    UserId = null,
    ScopeMode  Mode   = ScopeMode.Shared
)
{
    public static RetrievalScope ForApiKey(string tenantId)
        => new(tenantId, null, ScopeMode.Shared);

    public static RetrievalScope SharedFor(string tenantId)
        => new(tenantId, null, ScopeMode.Shared);

    public static RetrievalScope PrivateFor(string tenantId, string userId)
        => new(tenantId, userId, ScopeMode.Private);
}
