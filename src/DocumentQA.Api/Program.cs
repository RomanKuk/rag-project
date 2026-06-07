using System.Text.Json;
using DocumentQA.Core.Interfaces;
using DocumentQA.Core.Models;
using DocumentQA.Ingestion;
using DocumentQA.Ingestion.Parsers;
using DocumentQA.Api.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using Microsoft.SemanticKernel.Memory;

var builder = WebApplication.CreateBuilder(args);

// CORS — allow Angular dev server
builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy => policy
        .WithOrigins("http://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Semantic Kernel
builder.Services.AddSingleton(sp =>
{
    return Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(
            modelId: "gpt-4o",
            apiKey: builder.Configuration["OpenAI:ApiKey"]!)
        .Build();
});

// Vector memory (Qdrant-backed)
builder.Services.AddSingleton<ISemanticTextMemory>(sp =>
{
    var cfg = builder.Configuration;
    var host = cfg["Qdrant:Host"] ?? "localhost";
    var port = int.Parse(cfg["Qdrant:Port"] ?? "6333");

    var store = new QdrantMemoryStore(host, port, vectorSize: 1536);
    var embeddingGen = new OpenAITextEmbeddingGenerationService(
        "text-embedding-3-small",
        cfg["OpenAI:ApiKey"]!);

    return new SemanticTextMemory(store, embeddingGen);
});

// Ingestion
builder.Services.AddSingleton<SlidingWindowChunker>();
builder.Services.AddSingleton<IDocumentParser, PdfParser>();
builder.Services.AddSingleton<IDocumentParser, DocxParser>();
builder.Services.AddSingleton<IDocumentParser, TxtParser>();
builder.Services.AddSingleton<IngestionService>();

// RAG
builder.Services.AddSingleton<RagService>();

var app = builder.Build();

app.UseCors("Angular");

// Upload endpoint
app.MapPost("/api/documents/upload", async (
    IFormFile file,
    IngestionService ingestionService) =>
{
    if (file.Length == 0)
        return Results.BadRequest(new { error = "File is empty." });

    using var stream = file.OpenReadStream();
    await ingestionService.IngestAsync(stream, file.FileName);

    return Results.Ok(new { message = "Document ingested", fileName = file.FileName });
}).DisableAntiforgery();

// Chat endpoint — SSE streaming
app.MapPost("/api/chat", async (
    ChatRequest request,
    RagService ragService,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    await foreach (var token in ragService.AskAsync(request.Question, ct))
    {
        var json = JsonSerializer.Serialize(new { token });
        await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }

    await httpContext.Response.WriteAsync("data: [DONE]\n\n", ct);
    await httpContext.Response.Body.FlushAsync(ct);
});

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
