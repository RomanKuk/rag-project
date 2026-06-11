using System.Text.Json;
using DocumentQA.Application.Abstractions.Chat;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.UseCases.IngestDocument;
using DocumentQA.Domain.Chat;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Api.Endpoints;

public static class ChatSessionEndpoints
{
    public static void MapChatSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/chats").RequireAuthorization();

        // ── List sessions ─────────────────────────────────────────────────────
        group.MapGet("", async (
            IChatSessionRepository repo,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var sessions = await repo.ListAsync(user.TenantSlug, user.UserId, ct);
            return Results.Ok(sessions.Select(s => new
            {
                id               = s.Id,
                title            = s.Title,
                includeSharedDocs = s.IncludeSharedDocs,
                createdAt        = s.CreatedAt,
                updatedAt        = s.UpdatedAt,
            }));
        });

        // ── Create session ────────────────────────────────────────────────────
        group.MapPost("", async (
            CreateSessionRequest req,
            IChatSessionRepository repo,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = new ChatSession
            {
                TenantId          = user.TenantSlug,
                UserId            = user.UserId,
                Title             = req.Title ?? "New chat",
                IncludeSharedDocs = req.IncludeSharedDocs,
            };
            await repo.CreateAsync(session, ct);
            return Results.Created($"/api/chats/{session.Id}", new
            {
                id               = session.Id,
                title            = session.Title,
                includeSharedDocs = session.IncludeSharedDocs,
                createdAt        = session.CreatedAt,
                updatedAt        = session.UpdatedAt,
            });
        });

        // ── Get session + messages ────────────────────────────────────────────
        group.MapGet("{id:guid}", async (
            Guid id,
            IChatSessionRepository repo,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = await repo.GetWithMessagesAsync(id, ct);
            if (session is null || session.UserId != user.UserId)
                return Results.NotFound();

            return Results.Ok(new
            {
                id               = session.Id,
                title            = session.Title,
                includeSharedDocs = session.IncludeSharedDocs,
                createdAt        = session.CreatedAt,
                updatedAt        = session.UpdatedAt,
                messages         = session.Messages.Select(m => new
                {
                    id           = m.Id,
                    role         = m.Role,
                    content      = m.Content,
                    sourcesJson  = m.SourcesJson,
                    inputTokens  = m.InputTokens,
                    outputTokens = m.OutputTokens,
                    costUsd      = m.CostUsd,
                    createdAt    = m.CreatedAt,
                }),
            });
        });

        // ── Rename session ─────────────────────────────────────────────────────
        group.MapPatch("{id:guid}", async (
            Guid id,
            RenameSessionRequest req,
            IChatSessionRepository repo,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = await repo.GetAsync(id, ct);
            if (session is null || session.UserId != user.UserId)
                return Results.NotFound();

            session.Title     = req.Title;
            session.UpdatedAt = DateTime.UtcNow;
            await repo.UpdateAsync(session, ct);
            return Results.Ok(new { id = session.Id, title = session.Title });
        });

        // ── Delete session ─────────────────────────────────────────────────────
        group.MapDelete("{id:guid}", async (
            Guid id,
            IChatSessionRepository repo,
            IVectorStore vectorStore,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = await repo.GetAsync(id, ct);
            if (session is null || session.UserId != user.UserId)
                return Results.NotFound();

            await vectorStore.DeleteByChatAsync(id, ct);
            await repo.DeleteAsync(id, ct);
            return Results.Ok(new { deleted = id });
        });

        // ── Upload document into chat scope ────────────────────────────────────
        group.MapPost("{id:guid}/documents", async (
            Guid id,
            IFormFile file,
            IChatSessionRepository repo,
            IngestDocumentHandler handler,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = await repo.GetAsync(id, ct);
            if (session is null || session.UserId != user.UserId)
                return Results.NotFound();

            if (file.Length == 0)
                return Results.BadRequest(new { error = "File is empty." });

            var scope = RetrievalScope.ForChat(
                user.TenantSlug, user.UserId.ToString(), id,
                includeShared: session.IncludeSharedDocs);

            await using var stream = file.OpenReadStream();
            var result = await handler.HandleAsync(stream, file.FileName, scope, ct);

            return result.IsSuccess
                ? Results.Ok(new { chunks = result.Value, file = file.FileName, chatId = id })
                : Results.BadRequest(new { error = result.Error });
        })
        .DisableAntiforgery();

        // ── List documents in chat scope ───────────────────────────────────────
        group.MapGet("{id:guid}/documents", async (
            Guid id,
            IChatSessionRepository repo,
            IVectorStore vectorStore,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = await repo.GetAsync(id, ct);
            if (session is null || session.UserId != user.UserId)
                return Results.NotFound();

            var scope = RetrievalScope.ForChat(
                user.TenantSlug, user.UserId.ToString(), id,
                includeShared: false); // list only chat-specific docs

            var names = await vectorStore.ListDocumentNamesAsync(scope, ct);
            return Results.Ok(new { documents = names, chatId = id });
        });

        // ── Delete document from chat scope ───────────────────────────────────
        group.MapDelete("{id:guid}/documents/{name}", async (
            Guid id,
            string name,
            IChatSessionRepository repo,
            IVectorStore vectorStore,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var session = await repo.GetAsync(id, ct);
            if (session is null || session.UserId != user.UserId)
                return Results.NotFound();

            var scope = RetrievalScope.ForChat(
                user.TenantSlug, user.UserId.ToString(), id,
                includeShared: false);

            await vectorStore.DeleteDocumentAsync(name, scope, ct);
            return Results.Ok(new { deleted = name, chatId = id });
        });
    }
}

public sealed record CreateSessionRequest(string? Title, bool IncludeSharedDocs = true);
public sealed record RenameSessionRequest(string Title);
