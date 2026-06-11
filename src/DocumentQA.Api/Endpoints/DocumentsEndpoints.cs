using DocumentQA.Api.Auth;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.UseCases.IngestDocument;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Api.Endpoints;

public static class DocumentsEndpoints
{
    public static void MapDocumentsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/documents/upload", async (
            IFormFile file,
            IngestDocumentHandler handler,
            ICurrentUser currentUser,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (file.Length == 0)
                return Results.BadRequest(new { error = "File is empty." });

            var scope = ResolveScope(currentUser, ctx, privateMode: false);

            // Members can only upload private docs — owner/admin can upload shared
            if (currentUser.IsAuthenticated && currentUser.Role == Role.Member)
                scope = RetrievalScope.PrivateFor(scope.TenantId, currentUser.UserId.ToString());

            await using var stream = file.OpenReadStream();
            var result = await handler.HandleAsync(stream, file.FileName, scope, ct);

            return result.IsSuccess
                ? Results.Ok(new { chunks = result.Value, file = file.FileName, tenant = scope.TenantId, visibility = scope.Mode.ToString() })
                : Results.BadRequest(new { error = result.Error });
        })
        .DisableAntiforgery()
        .AddEndpointFilter<ApiKeyFilter>();

        app.MapGet("/api/documents", async (
            IVectorStore vectorStore,
            ICurrentUser currentUser,
            HttpContext ctx,
            bool privateMode = false,
            CancellationToken ct = default) =>
        {
            var scope = ResolveScope(currentUser, ctx, privateMode);
            var names = await vectorStore.ListDocumentNamesAsync(scope, ct);
            return Results.Ok(new { documents = names, tenant = scope.TenantId, mode = scope.Mode.ToString() });
        })
        .AddEndpointFilter<ApiKeyFilter>();

        app.MapDelete("/api/documents/{name}", async (
            string name,
            IVectorStore vectorStore,
            ICurrentUser currentUser,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var scope = ResolveScope(currentUser, ctx, privateMode: false);
            await vectorStore.DeleteDocumentAsync(name, scope, ct);
            return Results.Ok(new { deleted = name, tenant = scope.TenantId });
        })
        .AddEndpointFilter<ApiKeyFilter>();
    }

    internal static RetrievalScope ResolveScope(ICurrentUser user, HttpContext ctx, bool privateMode)
    {
        // JWT-authenticated user
        if (user.IsAuthenticated)
        {
            var tenantId = user.TenantSlug;
            if (privateMode)
                return RetrievalScope.PrivateFor(tenantId, user.UserId.ToString());
            return RetrievalScope.SharedFor(tenantId);
        }

        // ApiKey fallback (eval harness / programmatic access — always shared scope)
        var tenantCtx = ctx.Items.TryGetValue(ApiKeyFilter.TenantContextItemKey, out var tc)
            ? (TenantContext)tc!
            : null;
        var slug = tenantCtx?.TenantId ?? "public";
        return RetrievalScope.ForApiKey(slug);
    }
}
