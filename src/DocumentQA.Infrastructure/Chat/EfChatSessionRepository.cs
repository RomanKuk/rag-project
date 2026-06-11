using DocumentQA.Application.Abstractions.Chat;
using DocumentQA.Domain.Chat;
using DocumentQA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentQA.Infrastructure.Chat;

public sealed class EfChatSessionRepository(AppDbContext db) : IChatSessionRepository
{
    public async Task<ChatSession> CreateAsync(ChatSession session, CancellationToken ct)
    {
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    // Caps prevent unbounded loads — a session with thousands of messages would
    // otherwise be fully materialized on every open.
    private const int MaxSessions = 100;
    private const int MaxMessages = 200;

    public async Task<IReadOnlyList<ChatSession>> ListAsync(string tenantId, Guid userId, CancellationToken ct)
        => await db.ChatSessions
            .Where(s => s.TenantId == tenantId && s.UserId == userId)
            .OrderByDescending(s => s.UpdatedAt)
            .Take(MaxSessions)
            .ToListAsync(ct);

    public async Task<ChatSession?> GetAsync(Guid id, CancellationToken ct)
        => await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<ChatSession?> GetWithMessagesAsync(Guid id, CancellationToken ct)
    {
        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return null;

        // Last N messages in chronological order
        var recent = await db.ChatMessages
            .Where(m => m.SessionId == id)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxMessages)
            .ToListAsync(ct);
        recent.Reverse();
        session.Messages = recent;
        return session;
    }

    public async Task UpdateAsync(ChatSession session, CancellationToken ct)
    {
        db.ChatSessions.Update(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var session = await db.ChatSessions.FindAsync([id], ct);
        if (session is not null)
        {
            db.ChatSessions.Remove(session);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task AddMessageAsync(ChatMessage message, CancellationToken ct)
    {
        db.ChatMessages.Add(message);
        // also touch the session's UpdatedAt
        var session = await db.ChatSessions.FindAsync([message.SessionId], ct);
        if (session is not null)
            session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
