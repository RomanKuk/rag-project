using System.Text;
using DocumentQA.Api.Endpoints;
using DocumentQA.Api.Services;
using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Options;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Application.UseCases.IngestDocument;
using DocumentQA.Infrastructure.Cache;
using DocumentQA.Infrastructure.Chunking;
using DocumentQA.Infrastructure.Embeddings;
using DocumentQA.Infrastructure.Generation;
using DocumentQA.Infrastructure.Parsing;
using DocumentQA.Infrastructure.Retrieval;
using DocumentQA.Infrastructure.Security;
using DocumentQA.Infrastructure.VectorStores;
using Microsoft.SemanticKernel;
using OpenTelemetry.Trace;
using Qdrant.Client;

var builder = WebApplication.CreateBuilder(args);

// ── Options ─────────────────────────────────────────────────────────────────
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddPolicy("Angular", p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

// ── Semantic Kernel ───────────────────────────────────────────────────────────
builder.Services.AddOpenAIChatCompletion(
    modelId: "gpt-4o",
    apiKey: builder.Configuration["OpenAI:ApiKey"]!,
    serviceId: "chat");

builder.Services.AddOpenAIEmbeddingGenerator(
    modelId: "text-embedding-3-small",
    apiKey: builder.Configuration["OpenAI:ApiKey"]!,
    serviceId: "embeddings");

// ── Qdrant ────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton(_ =>
{
    var host = builder.Configuration["Qdrant:Host"] ?? "localhost";
    var port = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334");
    return new QdrantClient(host, port);
});

// ── Concurrency ───────────────────────────────────────────────────────────────
var maxConcurrent = builder.Configuration.GetValue("Concurrency:MaxLlmCalls", 20);
builder.Services.AddSingleton(new LlmGate(maxConcurrent));
builder.Services.AddSingleton<StreamMetrics>();

// ── Security ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IInputGuard, InputGuard>();
builder.Services.AddSingleton<ISuspiciousActivityLog, SuspiciousActivityLogger>();

// ── Ingestion ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IDocumentParser, PdfParser>();
builder.Services.AddScoped<IDocumentParser, DocxParser>();
builder.Services.AddScoped<IDocumentParser, TxtParser>();
builder.Services.AddScoped<IChunkingStrategy, SlidingWindowChunker>();

// ── Retrieval ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IEmbeddingPort, OpenAIEmbeddingAdapter>();
builder.Services.AddScoped<IVectorStore, QdrantVectorStore>();
builder.Services.AddScoped<IQueryProcessor, PassThroughQueryProcessor>();
builder.Services.AddScoped<IReranker, IdentityReranker>();

// ── Generation ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IPromptBuilder, TemplatePromptBuilder>();
builder.Services.AddScoped<IChatCompletionPort, OpenAIChatAdapter>();
builder.Services.AddScoped<IAnswerGuardrail, CitationPresenceGuardrail>();

// ── Cache ─────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ISemanticCache, QdrantSemanticCache>();

// ── Use cases ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<AskQuestionHandler>();
builder.Services.AddScoped<IngestDocumentHandler>();

// ── Observability (OpenTelemetry + optional Langfuse OTLP export) ─────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(t =>
    {
        t.AddSource("DocumentQA.Rag")
         .AddAspNetCoreInstrumentation();

        var lf = builder.Configuration.GetSection("Langfuse");
        if (lf.GetValue<bool>("Enabled"))
        {
            var pub = lf["PublicKey"]!;
            var sec = lf["SecretKey"]!;
            var baseUrl = lf["BaseUrl"] ?? "https://cloud.langfuse.com";
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{pub}:{sec}"));

            t.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"{baseUrl}/api/public/otel");
                o.Headers = $"Authorization=Basic {credentials}";
            });
        }
    });

var app = builder.Build();

app.UseCors("Angular");
app.MapDocumentsEndpoints();
app.MapChatEndpoints();
app.MapGet("/health", (StreamMetrics m, LlmGate g) => Results.Ok(new
{
    status = "ok",
    active_streams = m.Active,
    aborted_streams = m.Aborted,
    llm_slots_available = g.Available,
    llm_slots_max = g.MaxConcurrent
}));

app.Run();
