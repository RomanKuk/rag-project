using DocumentQA.Api.Auth;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.UseCases.IngestDocument;

namespace DocumentQA.Api.Endpoints;

public static class DocumentsEndpoints
{
    private static TenantContext AnonTenant => new(
        "public",
        new TierInfo { TokensPerMinute = int.MaxValue, Models = [] },
        "anonymous");

    private static TenantContext ResolveTenant(HttpContext ctx) =>
        ctx.Items.TryGetValue(ApiKeyFilter.TenantContextItemKey, out var tc)
            ? (TenantContext)tc!
            : AnonTenant;

    public static void MapDocumentsEndpoints(this WebApplication app)
    {
        app.MapPost("/api/documents/upload", async (
            IFormFile file,
            IngestDocumentHandler handler,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (file.Length == 0)
                return Results.BadRequest(new { error = "File is empty." });

            var tenant = ResolveTenant(ctx);
            await using var stream = file.OpenReadStream();
            var result = await handler.HandleAsync(stream, file.FileName, tenant.TenantId, ct);

            return result.IsSuccess
                ? Results.Ok(new { chunks = result.Value, file = file.FileName, tenant = tenant.TenantId })
                : Results.BadRequest(new { error = result.Error });
        })
        .DisableAntiforgery()
        .AddEndpointFilter<ApiKeyFilter>();

        app.MapGet("/api/documents", async (
            IVectorStore vectorStore,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var tenant = ResolveTenant(ctx);
            var names  = await vectorStore.ListDocumentNamesAsync(tenant.TenantId, ct);
            return Results.Ok(new { documents = names, tenant = tenant.TenantId });
        })
        .AddEndpointFilter<ApiKeyFilter>();

        app.MapDelete("/api/documents/{name}", async (
            string name,
            IVectorStore vectorStore,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var tenant = ResolveTenant(ctx);
            await vectorStore.DeleteDocumentAsync(name, tenant.TenantId, ct);
            return Results.Ok(new { deleted = name, tenant = tenant.TenantId });
        })
        .AddEndpointFilter<ApiKeyFilter>();
    }
}
