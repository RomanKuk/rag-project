using System.Text;
using DocumentQA.Api.Auth;
using DocumentQA.Api.Endpoints;
using DocumentQA.Api.Services;
using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Abstractions.Chat;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Identity;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Abstractions.Security;
using DocumentQA.Application.Abstractions.Usage;
using DocumentQA.Application.Options;
using DocumentQA.Application.UseCases.Admin;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Application.UseCases.Auth;
using DocumentQA.Application.UseCases.IngestDocument;
using DocumentQA.Application.UseCases.Owner;
using DocumentQA.Infrastructure.Agent;
using DocumentQA.Infrastructure.Cache;
using DocumentQA.Infrastructure.Chat;
using DocumentQA.Infrastructure.Chunking;
using DocumentQA.Infrastructure.Embeddings;
using DocumentQA.Infrastructure.Generation;
using DocumentQA.Infrastructure.Identity;
using DocumentQA.Infrastructure.Parsing;
using DocumentQA.Infrastructure.Persistence;
using DocumentQA.Infrastructure.RateLimiting;
using DocumentQA.Infrastructure.Retrieval;
using DocumentQA.Infrastructure.Security;
using DocumentQA.Infrastructure.Usage;
using DocumentQA.Infrastructure.VectorStores;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.SemanticKernel;
using OpenTelemetry.Trace;
using Qdrant.Client;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── Options ──────────────────────────────────────────────────────────────────
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection("Cache"));

// ── CORS ──────────────────────────────────────────────────────────────────────
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? ["http://localhost:4200"];
builder.Services.AddCors(o => o.AddPolicy("Angular", p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

// ── PostgreSQL + EF Core ──────────────────────────────────────────────────────
var pgConn = builder.Configuration["Postgres:ConnectionString"]!;
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(pgConn));
// Provide scoped AppDbContext via the factory (for EfUserRepository etc.)
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// ── Identity ports + adapters ─────────────────────────────────────────────────
builder.Services.AddScoped<IUserRepository,        EfUserRepository>();
builder.Services.AddScoped<ITenantRepository,      EfTenantRepository>();
builder.Services.AddScoped<IChatSessionRepository, EfChatSessionRepository>();
builder.Services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// ── JWT Authentication + Authorization ───────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep raw JWT claim names (e.g. "role", not the long URI)
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"],
            ValidAudience            = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType            = "email",
            RoleClaimType            = "role",
        };
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("AdminOnly",    p => p.RequireClaim("role", "Admin"));
    o.AddPolicy("OwnerOrAdmin", p => p.RequireClaim("role", "Owner", "Admin"));
});

// ── Semantic Kernel — embeddings ──────────────────────────────────────────────
builder.Services.AddOpenAIEmbeddingGenerator(
    modelId: builder.Configuration["Rag:EmbeddingModel"] ?? "text-embedding-3-small",
    apiKey:  builder.Configuration["OpenAI:ApiKey"]!,
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

// Safety moderation filter (OpenAI moderation API — opt-in when OpenAI key present)
var openAiKey = builder.Configuration["OpenAI:ApiKey"] ?? string.Empty;
builder.Services.AddHttpClient("OpenAIModeration", c =>
{
    c.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiKey}");
    c.Timeout = TimeSpan.FromSeconds(30);
}).AddStandardResilienceHandler();
if (!string.IsNullOrEmpty(openAiKey))
    builder.Services.AddScoped<ISafetyFilter, OpenAIModerationFilter>();
else
    builder.Services.AddSingleton<ISafetyFilter, NullSafetyFilter>();

// ── Auth endpoint filter ──────────────────────────────────────────────────────
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

// ── Usage tracking (Postgres) ─────────────────────────────────────────────────
builder.Services.AddSingleton<PostgresUsageTracker>();
builder.Services.AddSingleton<IUsageTracker>(sp  => sp.GetRequiredService<PostgresUsageTracker>());
builder.Services.AddSingleton<IUsageAnalytics>(sp => sp.GetRequiredService<PostgresUsageTracker>());

// ── Generation ────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ICompletionPort, OpenAICompletionAdapter>();

// ── Ingestion ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<TesseractOcr>();
builder.Services.AddScoped<IDocumentParser, PdfParser>();
builder.Services.AddScoped<IDocumentParser, DocxParser>();
builder.Services.AddScoped<IDocumentParser, TxtParser>();

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

builder.Services.AddScoped<IQueryProcessor>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    return opts.QueryExpansionEnabled
        ? (IQueryProcessor)sp.GetRequiredService<LlmQueryProcessor>()
        : sp.GetRequiredService<PassThroughQueryProcessor>();
});
builder.Services.AddScoped<LlmQueryProcessor>();
builder.Services.AddScoped<PassThroughQueryProcessor>();

builder.Services.AddScoped<IReranker>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    return opts.RerankerStrategy.ToLowerInvariant() switch
    {
        "llm"          => sp.GetRequiredService<LlmReranker>(),
        "crossencoder" => sp.GetRequiredService<CrossEncoderRerankerStrategy>(),
        _              => (IReranker)sp.GetRequiredService<IdentityReranker>(),
    };
});
builder.Services.AddScoped<LlmReranker>();
builder.Services.AddScoped<IdentityReranker>();
builder.Services.AddScoped<CrossEncoderRerankerStrategy>();

// Cross-encoder reranker (opt-in: set Reranker:Provider=cohere and Reranker:ApiKey)
var rerankerApiKey = builder.Configuration["Reranker:ApiKey"];
if (!string.IsNullOrEmpty(rerankerApiKey))
{
    builder.Services.AddHttpClient("Cohere", c =>
    {
        c.DefaultRequestHeaders.Add("Authorization", $"Bearer {rerankerApiKey}");
        c.DefaultRequestHeaders.Add("Accept", "application/json");
        c.Timeout = TimeSpan.FromSeconds(30);
    }).AddStandardResilienceHandler();
    builder.Services.Configure<RerankerOptions>(builder.Configuration.GetSection("Reranker"));
    builder.Services.AddScoped<ICrossEncoderReranker, CohereCrossEncoderReranker>();
}
else
{
    builder.Services.AddSingleton<ICrossEncoderReranker, NullCrossEncoderReranker>();
}

// ── Generation (chat, prompt, guardrail, model router, groundedness) ─────────
// Named client for OpenRouter streaming — pooled, with a generous streaming timeout.
builder.Services.AddHttpClient("OpenRouterChat", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1");
    c.Timeout     = TimeSpan.FromSeconds(120);
});
builder.Services.AddScoped<IPromptBuilder, TemplatePromptBuilder>();
builder.Services.AddScoped<IChatCompletionPort, OpenAIChatAdapter>();
builder.Services.AddScoped<IAnswerGuardrail, CitationPresenceGuardrail>();
builder.Services.AddScoped<IGroundednessCheck, LlmGroundednessCheck>();
builder.Services.AddSingleton<IModelRouter, ComplexityModelRouter>();
builder.Services.AddSingleton<ITokenCounter, SharpTokenCounter>();

// ── Cache ─────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ISemanticCache, QdrantSemanticCache>();

// ── Use cases ─────────────────────────────────────────────────────────────────
builder.Services.AddScoped<AskQuestionHandler>();
builder.Services.AddScoped<IngestDocumentHandler>();
builder.Services.AddScoped<LoginHandler>();
builder.Services.AddScoped<CreateTenantHandler>();
builder.Services.AddScoped<CreateUserHandler>();

// ── Agent orchestrator ────────────────────────────────────────────────────────
builder.Services.AddScoped<IAgentOrchestrator, SemanticKernelOrchestrator>();

// ── Observability ─────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithTracing(t =>
    {
        t.AddSource("DocumentQA.Rag")
         .AddAspNetCoreInstrumentation();

        var lf = builder.Configuration.GetSection("Langfuse");
        if (lf.GetValue<bool>("Enabled"))
        {
            var pub         = lf["PublicKey"]!;
            var sec         = lf["SecretKey"]!;
            var baseUrl     = lf["BaseUrl"] ?? "https://cloud.langfuse.com";
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{pub}:{sec}"));

            t.AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri($"{baseUrl}/api/public/otel");
                o.Headers  = $"Authorization=Basic {credentials}";
            });
        }
    });

var app = builder.Build();

// ── Startup configuration sanity checks ──────────────────────────────────────
{
    var startupLog    = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var adminPassword = builder.Configuration["Admin:Password"];
    var usingDefaultAdminPassword = string.IsNullOrEmpty(adminPassword) || adminPassword == "Admin1234!";

    if (usingDefaultAdminPassword)
    {
        if (app.Environment.IsProduction())
            throw new InvalidOperationException(
                "Refusing to start in Production with the default admin password. Set Admin:Password.");
        startupLog.LogWarning("SECURITY: admin account uses the DEFAULT password. Set Admin:Password before exposing this instance.");
    }

    if (!builder.Configuration.GetSection("ApiKeys").GetChildren().Any())
        startupLog.LogWarning("SECURITY: no API keys configured — API-key requests pass through as anonymous (dev mode).");

    if (!builder.Configuration.GetSection("Langfuse").GetValue<bool>("Enabled"))
        startupLog.LogInformation("Observability: Langfuse export disabled — spans are emitted locally only.");

    if (string.IsNullOrEmpty(rerankerApiKey))
        startupLog.LogInformation("Retrieval: no Reranker:ApiKey — cross-encoder reranking disabled (Null adapter).");
}

// ── Startup: run migrations + seed admin ─────────────────────────────────────
await AdminSeeder.SeedAsync(app.Services);

// ── Middleware pipeline ───────────────────────────────────────────────────────
app.UseCors("Angular");
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────────
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapUsersEndpoints();
app.MapDocumentsEndpoints();
app.MapChatEndpoints();
app.MapChatSessionEndpoints();
app.MapFeedbackEndpoints();
app.MapUsageEndpoints();

app.MapGet("/health", async (StreamMetrics m, LlmGate g, AppDbContext db, QdrantClient qdrant, CancellationToken ct) =>
{
    bool pgOk = false, qdrantOk = false;
    try { pgOk = await db.Database.CanConnectAsync(ct); } catch { /* unreachable -> false */ }
    try { await qdrant.ListCollectionsAsync(ct); qdrantOk = true; } catch { /* unreachable -> false */ }

    var healthy = pgOk && qdrantOk;
    var payload = new
    {
        status               = healthy ? "ok" : "degraded",
        postgres             = pgOk,
        qdrant               = qdrantOk,
        active_streams       = m.Active,
        aborted_streams      = m.Aborted,
        llm_slots_available  = g.Available,
        llm_slots_max        = g.MaxConcurrent
    };
    return healthy ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
});

app.Run();
