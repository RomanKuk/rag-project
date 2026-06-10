using System.Text;
using DocumentQA.Api.Auth;
using DocumentQA.Api.Endpoints;
using DocumentQA.Api.Services;
using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Options;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Application.UseCases.IngestDocument;
using DocumentQA.Infrastructure.Agent;
using DocumentQA.Infrastructure.Cache;
using DocumentQA.Infrastructure.Chunking;
using DocumentQA.Infrastructure.Embeddings;
using DocumentQA.Infrastructure.Generation;
using DocumentQA.Infrastructure.Parsing;
using DocumentQA.Infrastructure.RateLimiting;
using DocumentQA.Infrastructure.Retrieval;
using DocumentQA.Infrastructure.Security;
using DocumentQA.Infrastructure.Usage;
using DocumentQA.Infrastructure.VectorStores;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using OpenTelemetry.Trace;
using Qdrant.Client;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── Options ─────────────────────────────────────────────────────────────────
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddPolicy("Angular", p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

// ── Semantic Kernel — embeddings only (chat completion is handled per-call in OpenAIChatAdapter) ─
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

// ── Auth filter ───────────────────────────────────────────────────────────────
builder.Services.AddTransient<ApiKeyFilter>();

// ── Rate limiting ─────────────────────────────────────────────────────────────
var redisConn = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrEmpty(redisConn))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redisConn));
    builder.Services.AddSingleton<ITokenRateLimiter, RedisTokenRateLimiter>();
}
else
{
    builder.Services.AddSingleton<ITokenRateLimiter, NullTokenRateLimiter>();
}

// ── Usage tracking ────────────────────────────────────────────────────────────
var dbPath  = builder.Configuration["UsageDb:Path"] ?? "usage.db";
var tracker = new SqliteUsageTracker(dbPath);
await tracker.EnsureCreatedAsync();
builder.Services.AddSingleton<IUsageTracker>(tracker);

// ── Generation (non-streaming utility port for query expansion + reranking) ───
builder.Services.AddScoped<ICompletionPort, OpenAICompletionAdapter>();

// ── Ingestion ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<TesseractOcr>();
builder.Services.AddScoped<IDocumentParser, PdfParser>();
builder.Services.AddScoped<IDocumentParser, DocxParser>();
builder.Services.AddScoped<IDocumentParser, TxtParser>();

// Chunking strategy: structural (default) or sliding window
builder.Services.AddScoped<IChunkingStrategy>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    return opts.ChunkingStrategy.Equals("structural", StringComparison.OrdinalIgnoreCase)
        ? (IChunkingStrategy)sp.GetRequiredService<StructuralChunker>()
        : sp.GetRequiredService<SlidingWindowChunker>();
});
builder.Services.AddScoped<StructuralChunker>();
builder.Services.AddScoped<SlidingWindowChunker>();

// ── Retrieval ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IEmbeddingPort, OpenAIEmbeddingAdapter>();
builder.Services.AddScoped<IVectorStore, QdrantVectorStore>();

// Query processor: LLM expansion (default) or pass-through
builder.Services.AddScoped<IQueryProcessor>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    return opts.QueryExpansionEnabled
        ? (IQueryProcessor)sp.GetRequiredService<LlmQueryProcessor>()
        : sp.GetRequiredService<PassThroughQueryProcessor>();
});
builder.Services.AddScoped<LlmQueryProcessor>();
builder.Services.AddScoped<PassThroughQueryProcessor>();

// Reranker: LLM (default) or identity
builder.Services.AddScoped<IReranker>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    return opts.RerankerStrategy.Equals("llm", StringComparison.OrdinalIgnoreCase)
        ? (IReranker)sp.GetRequiredService<LlmReranker>()
        : sp.GetRequiredService<IdentityReranker>();
});
builder.Services.AddScoped<LlmReranker>();
builder.Services.AddScoped<IdentityReranker>();

// ── Generation ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IPromptBuilder, TemplatePromptBuilder>();
builder.Services.AddScoped<IChatCompletionPort, OpenAIChatAdapter>();
builder.Services.AddScoped<IAnswerGuardrail, CitationPresenceGuardrail>();

// ── Cache ─────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ISemanticCache, QdrantSemanticCache>();

// ── Use cases ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<AskQuestionHandler>();
builder.Services.AddScoped<IngestDocumentHandler>();

// ── Agent orchestrator (SK-based, opt-in via "agent":true in chat request) ────
builder.Services.AddScoped<IAgentOrchestrator, SemanticKernelOrchestrator>();

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
app.MapUsageEndpoints();
app.MapGet("/health", (StreamMetrics m, LlmGate g) => Results.Ok(new
{
    status = "ok",
    active_streams = m.Active,
    aborted_streams = m.Aborted,
    llm_slots_available = g.Available,
    llm_slots_max = g.MaxConcurrent
}));

app.Run();
