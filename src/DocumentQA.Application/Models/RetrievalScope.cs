using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.Models;

public enum ScopeMode { Shared, Private, Chat }

public sealed record RetrievalScope(
    string     TenantId,
    string?    UserId              = null,
    ScopeMode  Mode                = ScopeMode.Shared,
    Guid?      ChatId              = null,
    bool       IncludeSharedDocs  = false
)
{
    public static RetrievalScope ForApiKey(string tenantId)
        => new(tenantId, null, ScopeMode.Shared);

    public static RetrievalScope SharedFor(string tenantId)
        => new(tenantId, null, ScopeMode.Shared);

    public static RetrievalScope PrivateFor(string tenantId, string userId)
        => new(tenantId, userId, ScopeMode.Private);

    public static RetrievalScope ForChat(string tenantId, string userId, Guid chatId, bool includeShared)
        => new(tenantId, userId, ScopeMode.Chat, chatId, includeShared);
}
