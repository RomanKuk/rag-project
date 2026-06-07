using DocumentQA.Api.Endpoints;
using Microsoft.SemanticKernel;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Options;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Application.UseCases.IngestDocument;
using DocumentQA.Infrastructure.Chunking;
using DocumentQA.Infrastructure.Embeddings;
using DocumentQA.Infrastructure.Generation;
using DocumentQA.Infrastructure.Parsing;
using DocumentQA.Infrastructure.Retrieval;
using DocumentQA.Infrastructure.VectorStores;
using OpenTelemetry.Trace;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);

// Options
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));

// CORS for Angular
builder.Services.AddCors(o => o.AddPolicy("Angular", p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

// Semantic Kernel — registered with serviceId so adapters resolve via [FromKeyedServices]
builder.Services.AddOpenAIChatCompletion(
    modelId: "gpt-4o",
    apiKey: builder.Configuration["OpenAI:ApiKey"]!,
    serviceId: "chat");

builder.Services.AddOpenAIEmbeddingGenerator(
    modelId: "text-embedding-3-small",
    apiKey: builder.Configuration["OpenAI:ApiKey"]!,
    serviceId: "embeddings");

// Qdrant client
builder.Services.AddSingleton(_ =>
{
    var host = builder.Configuration["Qdrant:Host"] ?? "localhost";
    var port = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
    return new QdrantClient(host, port);
});

// Ingestion adapters
builder.Services.AddScoped<IDocumentParser, PdfParser>();
builder.Services.AddScoped<IDocumentParser, DocxParser>();
builder.Services.AddScoped<IDocumentParser, TxtParser>();
builder.Services.AddScoped<IChunkingStrategy, SlidingWindowChunker>();

// Retrieval adapters
builder.Services.AddScoped<IEmbeddingPort, OpenAIEmbeddingAdapter>();
builder.Services.AddScoped<IVectorStore, QdrantVectorStore>();
builder.Services.AddScoped<IQueryProcessor, PassThroughQueryProcessor>();
builder.Services.AddScoped<IReranker, IdentityReranker>();

// Generation adapters
builder.Services.AddScoped<IPromptBuilder, TemplatePromptBuilder>();
builder.Services.AddScoped<IChatCompletionPort, OpenAIChatAdapter>();
builder.Services.AddScoped<IAnswerGuardrail, CitationPresenceGuardrail>();

// Use cases
builder.Services.AddScoped<AskQuestionHandler>();
builder.Services.AddScoped<IngestDocumentHandler>();

// Observability
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("DocumentQA.Rag")
        .AddAspNetCoreInstrumentation());

var app = builder.Build();

app.UseCors("Angular");
app.MapDocumentsEndpoints();
app.MapChatEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
