using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Domain.Chat;
using DocumentQA.Infrastructure.Persistence;

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
