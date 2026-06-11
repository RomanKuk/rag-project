using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Chat;
using DocumentQA.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocumentQA.Api.Endpoints;

public static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat/feedback", async (
            FeedbackRequest req,
            ICurrentUser user,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (req.Rating != 1 && req.Rating != -1)
                return Results.BadRequest(new { error = "Rating must be +1 or -1." });

            var tenantId = user.IsAuthenticated ? user.TenantSlug : "public";
            var userId   = user.IsAuthenticated ? user.UserId : (Guid?)null;

            // Ownership check: the message must belong to a session owned by this user
            if (req.MessageId.HasValue)
            {
                var owns = await db.ChatMessages
                    .Where(m => m.Id == req.MessageId.Value)
                    .Join(db.ChatSessions, m => m.SessionId, s => s.Id, (m, s) => s)
                    .AnyAsync(s => s.UserId == userId && s.TenantId == tenantId, ct);
                if (!owns)
                    return Results.NotFound(new { error = "Message not found." });

                // Upsert: one feedback row per (message, user)
                var existing = await db.Feedbacks.FirstOrDefaultAsync(
                    f => f.MessageId == req.MessageId && f.UserId == userId, ct);
                if (existing is not null)
                {
                    existing.Rating  = req.Rating;
                    existing.Comment = req.Comment;
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { saved = true, updated = true });
                }
            }

            db.Feedbacks.Add(new Feedback
            {
                MessageId = req.MessageId,
                TenantId  = tenantId,
                UserId    = userId,
                Rating    = req.Rating,
                Comment   = req.Comment,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { saved = true });
        })
        .RequireAuthorization();
    }
}

public sealed record FeedbackRequest(
    Guid?   MessageId,
    int     Rating,
    string? Comment = null
);
