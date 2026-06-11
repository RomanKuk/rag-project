using DocumentQA.Domain.Chat;

namespace DocumentQA.Application.Abstractions.Chat;

public interface IChatSessionRepository
{
    Task<ChatSession> CreateAsync(ChatSession session, CancellationToken ct);
    Task<IReadOnlyList<ChatSession>> ListAsync(string tenantId, Guid userId, CancellationToken ct);
    Task<ChatSession?> GetAsync(Guid id, CancellationToken ct);
    Task<ChatSession?> GetWithMessagesAsync(Guid id, CancellationToken ct);
    Task UpdateAsync(ChatSession session, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task AddMessageAsync(ChatMessage message, CancellationToken ct);
}
