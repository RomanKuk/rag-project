using DocumentQA.Application.UseCases.IngestDocument;

namespace DocumentQA.Api.Endpoints;

public static class DocumentsEndpoints
{
    public static void MapDocumentsEndpoints(this WebApplication app) =>
        app.MapPost("/api/documents/upload", async (
            IFormFile file,
            IngestDocumentHandler handler,
            CancellationToken ct) =>
        {
            if (file.Length == 0)
                return Results.BadRequest(new { error = "File is empty." });

            await using var stream = file.OpenReadStream();
            var result = await handler.HandleAsync(stream, file.FileName, ct);

            return result.IsSuccess
                ? Results.Ok(new { chunks = result.Value, file = file.FileName })
                : Results.BadRequest(new { error = result.Error });
        }).DisableAntiforgery();
}
