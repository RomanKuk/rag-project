# Document Q&A Assistant — Implementation Plan

> Enterprise-grade RAG system, built MVP-first.
> ASP.NET Core (.NET 10) + Semantic Kernel + OpenAI API + Angular 22
> Clean Architecture · Ports & Adapters · No multitenancy in MVP · 20-lesson course project

---

## 1. Guiding Principles

This project is built as a **vertical slice through a clean architecture**, then grown one capability per lesson. Three principles drive every decision:

1. **Every pipeline stage is a port (interface).** Parsing, chunking, embedding, retrieval, reranking, generation, guardrails — each is an abstraction with a swappable adapter. You start with the simplest adapter and replace it without touching callers.
2. **The domain knows nothing about OpenAI, Qdrant, or Azure.** Infrastructure depends on the domain, never the reverse. You can swap OpenAI → Azure OpenAI, or Qdrant → Azure AI Search, by adding an adapter and one DI line.
3. **Eval and observability are first-class, not an afterthought.** A RAG system you can't measure is a RAG system you can't improve. The feedback loop is part of the architecture from day one.

This mirrors the enterprise RAG reference pipeline (offline ingestion → online retrieval → augmentation/generation → post-processing, with security and RAGOps as cross-cutting concerns). The MVP implements the **thinnest working path** through it; later lessons thicken each stage.

---

## 2. Project Overview

| Item | Value |
|---|---|
| **Type** | RAG System |
| **Stack** | .NET 10 / C#, Angular 22, Python (eval only) |
| **Architecture** | Clean Architecture (Domain / Application / Infrastructure / API) |
| **Cloud** | Azure |
| **LLM** | OpenAI API — GPT-4o (behind `IChatCompletionPort`) |
| **Embeddings** | OpenAI API — text-embedding-3-small (behind `IEmbeddingPort`) |
| **Vector DB** | Qdrant (local MVP) → Azure AI Search (later) |
| **Document store** | Local filesystem → Azure Blob Storage |
| **Orchestration** | Semantic Kernel |
| **Eval** | Ragas (Python) + custom judge-LLM |
| **Observability** | OpenTelemetry → Azure Monitor / Application Insights |
| **UI** | Angular 22 SPA (signal-first, zoneless) |

---

## 3. Business Problem

Users spend significant time manually searching through company documents (PDFs, DOCX, technical docs, regulations). The system lets a user ask a question in natural language and receive an accurate answer **grounded in the source documents**, with a citation to the exact document and page.

**Success metrics (three layers):**

| Layer | Metric | Target |
|---|---|---|
| Technical | Faithfulness on golden set (judge-LLM) | ≥ 0.8 |
| Technical | Context precision / recall | tracked, improving |
| Product | Questions answered without hallucination | ≥ 90% |
| Product | End-to-end answer latency (p95) | < 5 s |
| Business | Time-to-answer vs manual search | ~5 min → ~30 s |

---

## 4. Enterprise Pipeline → Architecture Mapping

The reference pipeline maps directly onto ports. This table is the project's backbone — each row is a port, an MVP adapter, and the lesson where it gets upgraded.

| Pipeline stage | Port (abstraction) | MVP adapter | Upgrade path |
|---|---|---|---|
| **Ingestion orchestration** | `IIngestionPipeline` | In-process, synchronous on upload | Background worker → Azure Functions / Hangfire |
| Multimodal parsing | `IDocumentParser` | PdfPig, OpenXML (text only) | + Azure Document Intelligence (tables, scans, images) |
| Chunk + metadata enrichment | `IChunkingStrategy` | Sliding window + page/source tags | Semantic chunking, role/department tags |
| Embedding | `IEmbeddingPort` | OpenAI text-embedding-3-small | Batch embedding, multilingual model |
| **Query pre-processing** | `IQueryProcessor` | Pass-through (no-op) | Rewriting, decomposition, intent detection |
| Vector / semantic search | `IVectorStore` | Qdrant cosine top-K | Azure AI Search |
| Full-text search | `IKeywordSearch` | (skipped in MVP) | Hybrid: vector + BM25 |
| Metadata filtering | `ISearchFilter` | (skipped in MVP) | Role/department filtering |
| **Re-ranking** | `IReranker` | Identity (pass-through) | Cross-encoder reranker |
| Prompt assembly + token budgeting | `IPromptBuilder` | Template + naive concat | Token budgeting, dynamic context window |
| LLM inference + routing | `IChatCompletionPort` | OpenAI GPT-4o | Model routing, fallback, prompt cache |
| **Guardrails / de-hallucination** | `IAnswerGuardrail` | Citation presence check | Safety filters, groundedness verification |
| Citation generation | `ICitationBuilder` | Map chunks → sources | Inline span-level citations |
| Response cache | `IResponseCache` | (skipped in MVP) | Semantic LLM cache |
| **Security & compliance** | cross-cutting | API key auth, HTTPS | IAM, audit logs, data residency, encryption at rest |
| **RAGOps & observability** | cross-cutting | OpenTelemetry traces + Ragas eval | Live monitoring, feedback loop, CI/CD eval gate |

> **The MVP path (bold stages, simplest adapters):** parse → sliding-window chunk → embed → Qdrant top-K → template prompt → GPT-4o → citation check → answer. Everything else starts as a no-op or identity adapter and is filled in lesson by lesson — *without changing the use-case orchestration.*

---

## 5. Solution Structure (Clean Architecture)

```
/src
  DocumentQA.Domain/            ← Entities, value objects, domain rules. ZERO dependencies.
    Documents/
      Document.cs
      DocumentChunk.cs
      ChunkMetadata.cs
    Retrieval/
      RetrievedChunk.cs
      Citation.cs
    Common/
      Result.cs                 ← Result<T> for explicit error handling

  DocumentQA.Application/       ← Use cases + ports. Depends only on Domain.
    Abstractions/               ← PORTS (interfaces)
      Ingestion/
        IDocumentParser.cs
        IChunkingStrategy.cs
        IIngestionPipeline.cs
      Retrieval/
        IEmbeddingPort.cs
        IVectorStore.cs
        IQueryProcessor.cs
        IReranker.cs
      Generation/
        IPromptBuilder.cs
        IChatCompletionPort.cs
        IAnswerGuardrail.cs
        ICitationBuilder.cs
    UseCases/
      IngestDocument/
        IngestDocumentCommand.cs
        IngestDocumentHandler.cs
      AskQuestion/
        AskQuestionQuery.cs
        AskQuestionHandler.cs    ← orchestrates the whole RAG flow
    Options/
      RagOptions.cs              ← Options pattern (chunk size, top-K, thresholds)

  DocumentQA.Infrastructure/    ← ADAPTERS. Depends on Application + Domain.
    Parsing/
      PdfParser.cs
      DocxParser.cs
      PlainTextParser.cs
    Chunking/
      SlidingWindowChunker.cs
    Embeddings/
      OpenAIEmbeddingAdapter.cs
    VectorStores/
      QdrantVectorStore.cs
      AzureAiSearchVectorStore.cs   ← added later, same port
    Generation/
      OpenAIChatAdapter.cs
      TemplatePromptBuilder.cs
      CitationPresenceGuardrail.cs
    Telemetry/
      RagActivitySource.cs       ← OpenTelemetry instrumentation

  DocumentQA.Api/               ← Composition root. Endpoints, DI wiring, middleware.
    Endpoints/
      DocumentsEndpoints.cs
      ChatEndpoints.cs
    Program.cs                   ← the ONLY place infrastructure is wired to ports
    appsettings.json

/ui
  document-qa-ui/               ← Angular 22 SPA (signal-first, zoneless)

/eval
  golden_set.json
  eval.py                       ← Ragas; calls the API over HTTP

/infra
  docker-compose.yml            ← Qdrant local
  Dockerfile.api
  Dockerfile.ui
  main.bicep                    ← Azure Container Apps (later)
```

**The dependency rule:** `Api → Infrastructure → Application → Domain`. Arrows point inward. Domain has no `using` of any framework. This is what lets every pipeline stage be swapped.

---

## 6. Domain Layer

The domain is pure C# — no Semantic Kernel, no Azure SDK. It models *what a RAG system is*, not *how it's implemented*.

```csharp
// Domain/Documents/DocumentChunk.cs
namespace DocumentQA.Domain.Documents;

public sealed record DocumentChunk
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required ChunkMetadata Metadata { get; init; }
}

public sealed record ChunkMetadata
{
    public required string DocumentName { get; init; }
    public required int Page { get; init; }
    public required int ChunkIndex { get; init; }
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;

    // Enrichment fields — populated by later lessons, ignored by MVP.
    public string? Source { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}
```

```csharp
// Domain/Retrieval/RetrievedChunk.cs
namespace DocumentQA.Domain.Retrieval;

public sealed record RetrievedChunk(DocumentChunk Chunk, double Score);

public sealed record Citation(string DocumentName, int Page, string Excerpt);
```

```csharp
// Domain/Common/Result.cs — explicit success/failure, no exceptions for control flow
namespace DocumentQA.Domain.Common;

public readonly record struct Result<T>(bool IsSuccess, T? Value, string? Error)
{
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(string error) => new(false, default, error);
}
```

---

## 7. Application Layer — Ports

Ports are the contract between the domain logic and the outside world. They live in `Application`, are implemented in `Infrastructure`.

```csharp
// Application/Abstractions/Ingestion/IDocumentParser.cs
public interface IDocumentParser
{
    bool CanHandle(string fileExtension);
    IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName, CancellationToken ct);
}
public sealed record ParsedPage(int PageNumber, string Text);
```

```csharp
// Application/Abstractions/Ingestion/IChunkingStrategy.cs
public interface IChunkingStrategy
{
    IEnumerable<string> Chunk(string text);
}
```

```csharp
// Application/Abstractions/Retrieval/IEmbeddingPort.cs
public interface IEmbeddingPort
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
```

```csharp
// Application/Abstractions/Retrieval/IVectorStore.cs
public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryEmbedding, int topK, double minScore, CancellationToken ct);
}
```

```csharp
// Application/Abstractions/Retrieval/IQueryProcessor.cs
// MVP: pass-through. Later: rewriting, decomposition, intent detection.
public interface IQueryProcessor
{
    Task<ProcessedQuery> ProcessAsync(string rawQuery, CancellationToken ct);
}
public sealed record ProcessedQuery(string SearchText, string Intent = "qa");
```

```csharp
// Application/Abstractions/Retrieval/IReranker.cs
// MVP: identity (returns input unchanged). Later: cross-encoder.
public interface IReranker
{
    Task<IReadOnlyList<RetrievedChunk>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int topN, CancellationToken ct);
}
```

```csharp
// Application/Abstractions/Generation/IPromptBuilder.cs
public interface IPromptBuilder
{
    PromptBundle Build(string question, IReadOnlyList<RetrievedChunk> context);
}
public sealed record PromptBundle(string SystemPrompt, string UserPrompt, IReadOnlyList<Citation> Sources);
```

```csharp
// Application/Abstractions/Generation/IChatCompletionPort.cs
public interface IChatCompletionPort
{
    IAsyncEnumerable<string> StreamAsync(PromptBundle prompt, CancellationToken ct);
}
```

```csharp
// Application/Abstractions/Generation/IAnswerGuardrail.cs
// MVP: verifies the answer cites at least one source. Later: groundedness + safety.
public interface IAnswerGuardrail
{
    GuardrailVerdict Check(string answer, IReadOnlyList<Citation> availableSources);
}
public sealed record GuardrailVerdict(bool Passed, string? Reason);
```

---

## 8. Application Layer — Use Cases

The use case is where the pipeline orchestration lives. **It depends only on ports** — it has no idea OpenAI or Qdrant exist. This is the heart of the design: the orchestration never changes as you swap adapters.

```csharp
// Application/UseCases/AskQuestion/AskQuestionHandler.cs
public sealed class AskQuestionHandler
{
    private readonly IQueryProcessor _queryProcessor;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;
    private readonly IReranker _reranker;
    private readonly IPromptBuilder _promptBuilder;
    private readonly IChatCompletionPort _chat;
    private readonly RagOptions _options;
    private static readonly ActivitySource Activity = new("DocumentQA.Rag");

    public AskQuestionHandler(/* ctor injection of all ports + IOptions<RagOptions> */) { /* ... */ }

    public async IAsyncEnumerable<AskQuestionChunk> HandleAsync(
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = Activity.StartActivity("AskQuestion");

        // 1. Query pre-processing (MVP: pass-through)
        var processed = await _queryProcessor.ProcessAsync(question, ct);

        // 2. Embed the query
        var queryVector = await _embedding.EmbedAsync(processed.SearchText, ct);

        // 3. Vector search (semantic retrieval)
        var candidates = await _vectorStore.SearchAsync(
            queryVector, _options.RetrievalTopK, _options.MinRelevanceScore, ct);

        if (candidates.Count == 0)
        {
            yield return AskQuestionChunk.NoContext();
            yield break;
        }

        // 4. Re-rank (MVP: identity)
        var ranked = await _reranker.RerankAsync(
            processed.SearchText, candidates, _options.RerankTopN, ct);

        // 5. Assemble prompt + collect citations
        var prompt = _promptBuilder.Build(question, ranked);

        // 6. Emit sources first, then stream tokens
        yield return AskQuestionChunk.Sources(prompt.Sources);

        await foreach (var token in _chat.StreamAsync(prompt, ct))
            yield return AskQuestionChunk.Token(token);
    }
}

public sealed record AskQuestionChunk(string Type, string? Token, IReadOnlyList<Citation>? Sources)
{
    public static AskQuestionChunk Token(string t) => new("token", t, null);
    public static AskQuestionChunk Sources(IReadOnlyList<Citation> s) => new("sources", null, s);
    public static AskQuestionChunk NoContext() => new("no_context", null, null);
}
```

```csharp
// Application/UseCases/IngestDocument/IngestDocumentHandler.cs
public sealed class IngestDocumentHandler
{
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IChunkingStrategy _chunker;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;

    public async Task<Result<int>> HandleAsync(Stream file, string fileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(ext));
        if (parser is null) return Result<int>.Failure($"Unsupported file type: {ext}");

        var chunks = new List<DocumentChunk>();
        await foreach (var page in parser.ParseAsync(file, fileName, ct))
        {
            var pieces = _chunker.Chunk(page.Text);
            chunks.AddRange(pieces.Select((text, idx) => new DocumentChunk
            {
                Id = $"{fileName}-p{page.PageNumber}-c{idx}",
                Content = text,
                Metadata = new ChunkMetadata
                {
                    DocumentName = fileName, Page = page.PageNumber, ChunkIndex = idx
                }
            }));
        }

        if (chunks.Count == 0) return Result<int>.Failure("No text extracted");

        var embeddings = await _embedding.EmbedBatchAsync(
            chunks.Select(c => c.Content).ToList(), ct);
        await _vectorStore.UpsertAsync(chunks, embeddings, ct);

        return Result<int>.Success(chunks.Count);
    }
}
```

```csharp
// Application/Options/RagOptions.cs — Options pattern, bound from appsettings
public sealed class RagOptions
{
    public int ChunkSize { get; init; } = 500;
    public int ChunkOverlap { get; init; } = 100;
    public int RetrievalTopK { get; init; } = 10;
    public int RerankTopN { get; init; } = 5;
    public double MinRelevanceScore { get; init; } = 0.7;
}
```

---

## 9. Infrastructure Layer — Adapters

Each adapter implements one port. Swapping an adapter = adding a class + changing one DI line. Callers never change.

### 9.1 Parsing

```csharp
// Infrastructure/Parsing/PdfParser.cs
public sealed class PdfParser : IDocumentParser
{
    public bool CanHandle(string ext) => ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(
        Stream stream, string fileName, [EnumeratorCancellation] CancellationToken ct)
    {
        using var doc = PdfDocument.Open(stream);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            yield return new ParsedPage(page.Number, text);
        }
    }
}
```

### 9.2 Chunking

```csharp
// Infrastructure/Chunking/SlidingWindowChunker.cs
public sealed class SlidingWindowChunker(IOptions<RagOptions> options) : IChunkingStrategy
{
    private readonly RagOptions _o = options.Value;

    public IEnumerable<string> Chunk(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var step = _o.ChunkSize - _o.ChunkOverlap;
        for (int i = 0; i < words.Length; i += step)
        {
            yield return string.Join(" ", words.Skip(i).Take(_o.ChunkSize));
            if (i + _o.ChunkSize >= words.Length) break;
        }
    }
}
```

### 9.3 Embeddings (OpenAI behind the port)

```csharp
// Infrastructure/Embeddings/OpenAIEmbeddingAdapter.cs
public sealed class OpenAIEmbeddingAdapter(
    [FromKeyedServices("embeddings")] ITextEmbeddingGenerationService sk) : IEmbeddingPort
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        => (await sk.GenerateEmbeddingAsync(text, cancellationToken: ct)).ToArray();

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
        => (await sk.GenerateEmbeddingsAsync(texts, cancellationToken: ct))
            .Select(e => e.ToArray()).ToList();
}
```

### 9.4 No-op / identity adapters for the MVP

These satisfy the ports so the use case runs end-to-end today, and get replaced later with zero changes to `AskQuestionHandler`.

```csharp
// Infrastructure/Retrieval/PassThroughQueryProcessor.cs
public sealed class PassThroughQueryProcessor : IQueryProcessor
{
    public Task<ProcessedQuery> ProcessAsync(string q, CancellationToken ct)
        => Task.FromResult(new ProcessedQuery(q));
}

// Infrastructure/Retrieval/IdentityReranker.cs
public sealed class IdentityReranker : IReranker
{
    public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query, IReadOnlyList<RetrievedChunk> candidates, int topN, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RetrievedChunk>>(candidates.Take(topN).ToList());
}
```

### 9.5 Prompt builder + guardrail

```csharp
// Infrastructure/Generation/TemplatePromptBuilder.cs
public sealed class TemplatePromptBuilder : IPromptBuilder
{
    private const string System = """
        You are a document assistant. Answer ONLY from the provided context.
        - If the answer is not in the context, say you cannot find it in the documents.
        - Cite every claim as [DocumentName, page X].
        - Be concise and factual. Never use outside knowledge.
        """;

    public PromptBundle Build(string question, IReadOnlyList<RetrievedChunk> context)
    {
        var sb = new StringBuilder();
        var citations = new List<Citation>();
        foreach (var rc in context)
        {
            var m = rc.Chunk.Metadata;
            sb.AppendLine($"[{m.DocumentName}, page {m.Page}]").AppendLine(rc.Chunk.Content).AppendLine();
            citations.Add(new Citation(m.DocumentName, m.Page,
                rc.Chunk.Content[..Math.Min(200, rc.Chunk.Content.Length)]));
        }
        var user = $"Context:\n{sb}\n\nQuestion: {question}";
        return new PromptBundle(System, user, citations);
    }
}

// Infrastructure/Generation/CitationPresenceGuardrail.cs
public sealed class CitationPresenceGuardrail : IAnswerGuardrail
{
    public GuardrailVerdict Check(string answer, IReadOnlyList<Citation> sources)
        => sources.Any(s => answer.Contains(s.DocumentName, StringComparison.OrdinalIgnoreCase))
            ? new(true, null)
            : new(false, "Answer contains no citation to a known source");
}
```

---

## 10. API Layer — Composition Root

`Program.cs` is the **only** place where ports are wired to adapters. Swapping Qdrant → Azure AI Search, or adding a real reranker, happens here and nowhere else.

```csharp
// Api/Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));

// --- LLM + embeddings (OpenAI via API key, behind ports) ---
builder.Services.AddKeyedOpenAIChatCompletion("chat", "gpt-4o", builder.Configuration["OpenAI:ApiKey"]!);
builder.Services.AddKeyedOpenAITextEmbeddingGeneration("embeddings", "text-embedding-3-small", builder.Configuration["OpenAI:ApiKey"]!);

// --- Ingestion adapters ---
builder.Services.AddScoped<IDocumentParser, PdfParser>();
builder.Services.AddScoped<IDocumentParser, DocxParser>();
builder.Services.AddScoped<IDocumentParser, PlainTextParser>();
builder.Services.AddScoped<IChunkingStrategy, SlidingWindowChunker>();

// --- Retrieval adapters (MVP choices) ---
builder.Services.AddScoped<IEmbeddingPort, OpenAIEmbeddingAdapter>();
builder.Services.AddScoped<IVectorStore, QdrantVectorStore>();        // ← swap to AzureAiSearchVectorStore later
builder.Services.AddScoped<IQueryProcessor, PassThroughQueryProcessor>(); // ← swap to RewritingQueryProcessor later
builder.Services.AddScoped<IReranker, IdentityReranker>();            // ← swap to CrossEncoderReranker later

// --- Generation adapters ---
builder.Services.AddScoped<IPromptBuilder, TemplatePromptBuilder>();
builder.Services.AddScoped<IChatCompletionPort, OpenAIChatAdapter>();
builder.Services.AddScoped<IAnswerGuardrail, CitationPresenceGuardrail>();

// --- Use cases ---
builder.Services.AddScoped<AskQuestionHandler>();
builder.Services.AddScoped<IngestDocumentHandler>();

// --- Cross-cutting: observability ---
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("DocumentQA.Rag").AddAspNetCoreInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());

// --- CORS for Angular ---
builder.Services.AddCors(o => o.AddPolicy("Angular", p =>
    p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("Angular");
app.MapDocumentsEndpoints();
app.MapChatEndpoints();
app.Run();
```

```csharp
// Api/Endpoints/ChatEndpoints.cs — thin: parse request, call handler, stream SSE
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app) =>
        app.MapPost("/api/chat", async (ChatRequest req, AskQuestionHandler handler, HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";

            await foreach (var chunk in handler.HandleAsync(req.Question, ctx.RequestAborted))
            {
                var json = JsonSerializer.Serialize(chunk);
                await ctx.Response.WriteAsync($"data: {json}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
        });
}

public sealed record ChatRequest(string Question);
```

```csharp
// Api/Endpoints/DocumentsEndpoints.cs
public static class DocumentsEndpoints
{
    public static void MapDocumentsEndpoints(this WebApplication app) =>
        app.MapPost("/api/documents/upload", async (IFormFile file, IngestDocumentHandler handler, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var result = await handler.HandleAsync(stream, file.FileName, ct);
            return result.IsSuccess
                ? Results.Ok(new { chunks = result.Value, file = file.FileName })
                : Results.BadRequest(new { error = result.Error });
        }).DisableAntiforgery();
}
```

---

## 11. Phase Plan (mapped to the architecture)

Each phase is a **working vertical slice**. You never have a half-built layer — you have a thinner or thicker version of the whole pipeline.

### Phase 1 — Walking skeleton (the MVP path)
**Lessons ~1–6.** Domain + Application ports + minimal Infrastructure. Parse PDF/DOCX → sliding-window chunk → OpenAI embed → Qdrant → template prompt → GPT-4o → citation-check guardrail. No-op query processor and identity reranker in place. One upload endpoint, one chat endpoint (SSE).
**Done when:** upload a PDF, ask a question via curl, get a streamed, cited answer.

### Phase 2 — Retrieval quality
**Lessons ~7–12.** Swap Qdrant → Azure AI Search (hybrid vector + keyword via `IKeywordSearch`). Add real `IQueryProcessor` (rewriting + intent). Add metadata enrichment at ingestion (source, tags) and `ISearchFilter`. Add cross-encoder `IReranker`. Move ingestion to a background worker / Azure Function triggered by Blob upload.
**Done when:** retrieval precision measurably improves on the golden set; ingestion is async.

### Phase 3 — Generation quality + RAGOps
**Lessons ~13–18.** Token-budgeting `IPromptBuilder`. Model routing + prompt cache behind `IChatCompletionPort`. Real guardrails (groundedness, safety) behind `IAnswerGuardrail`. Wire OpenTelemetry to Application Insights. Ragas eval in CI as a quality gate. Feedback loop endpoint (thumbs up/down → stored for eval set growth).
**Done when:** faithfulness ≥ 0.8 enforced in CI; traces and eval dashboards live.

### Phase 4 — Product polish + deploy
**Lessons ~19–20.** Angular 22 UI (chat + upload + citations). Deploy API and UI to Azure Container Apps. Demo recording.
**Done when:** deployed, presentable, measured.

---

## 12. Angular 22 Frontend

Signal-first, zoneless (Angular 22 defaults). The API contract is identical regardless of backend internals.

```bash
ng new document-qa-ui --routing --style=scss --zoneless
cd document-qa-ui
ng generate service services/chat
ng generate service services/document
ng generate component components/chat
ng generate component components/document-upload
```

### Chat models

```typescript
// models/chat.models.ts
export interface SourceReference {
  documentName: string;
  page: number;
  excerpt: string;
}
export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  sources: SourceReference[];
  isStreaming?: boolean;
}
```

### Chat service — SSE over POST via fetch + ReadableStream

`EventSource` only supports GET; the chat endpoint is POST, so use `fetch` with a stream reader.

```typescript
// services/chat.service.ts
import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';

interface ServerChunk { type: 'token' | 'sources' | 'no_context'; token?: string; sources?: any[]; }

@Injectable({ providedIn: 'root' })
export class ChatService {
  async *streamAnswer(question: string): AsyncGenerator<ServerChunk> {
    const res = await fetch(`${environment.apiUrl}/api/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question }),
    });
    if (!res.body) throw new Error('No response body');

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';
      for (const line of lines) {
        if (!line.startsWith('data: ')) continue;
        const data = line.slice(6).trim();
        if (data === '[DONE]') return;
        try { yield JSON.parse(data) as ServerChunk; } catch { /* skip */ }
      }
    }
  }
}
```

### Chat component (signals)

```typescript
// components/chat/chat.component.ts
import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatService } from '../../services/chat.service';
import { ChatMessage, SourceReference } from '../../models/chat.models';

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export class ChatComponent {
  messages = signal<ChatMessage[]>([]);
  question = signal('');
  isLoading = signal(false);

  constructor(private chat: ChatService) {}

  async send() {
    const q = this.question().trim();
    if (!q || this.isLoading()) return;

    this.messages.update(m => [...m, { role: 'user', content: q, sources: [] }]);
    const assistant: ChatMessage = { role: 'assistant', content: '', sources: [], isStreaming: true };
    this.messages.update(m => [...m, assistant]);
    this.question.set('');
    this.isLoading.set(true);

    try {
      for await (const chunk of this.chat.streamAnswer(q)) {
        if (chunk.type === 'sources') assistant.sources = chunk.sources as SourceReference[];
        else if (chunk.type === 'token') assistant.content += chunk.token ?? '';
        this.messages.update(m => [...m]); // trigger signal update
      }
    } finally {
      assistant.isStreaming = false;
      this.isLoading.set(false);
      this.messages.update(m => [...m]);
    }
  }
}
```

```html
<!-- components/chat/chat.component.html -->
<div class="chat-container">
  <div class="messages">
    @for (msg of messages(); track $index) {
      <div class="message" [class]="msg.role">
        <div class="content">
          {{ msg.content }}
          @if (msg.isStreaming) { <span class="cursor">▋</span> }
        </div>
        @if (msg.sources.length) {
          <div class="sources">
            <span class="sources-label">Sources:</span>
            @for (s of msg.sources; track s.documentName + s.page) {
              <span class="source-tag">{{ s.documentName }} p.{{ s.page }}</span>
            }
          </div>
        }
      </div>
    }
  </div>
  <div class="input-area">
    <textarea [(ngModel)]="question" (keydown.enter)="$event.preventDefault(); send()"
      placeholder="Ask about your documents..." rows="2"></textarea>
    <button (click)="send()" [disabled]="isLoading() || !question().trim()">
      {{ isLoading() ? '...' : 'Send' }}
    </button>
  </div>
</div>
```

---

## 13. Eval & RAGOps (cross-cutting, first-class)

### Golden test set

```json
// eval/golden_set.json
[
  { "question": "What is the maximum file size allowed for uploads?",
    "expected_answer": "50MB per document.", "source_document": "technical-spec.pdf", "source_page": 3 },
  { "question": "Who is responsible for data processing under GDPR?",
    "expected_answer": "The Data Controller.", "source_document": "privacy-policy.pdf", "source_page": 1 }
]
```

### Ragas evaluation (Python — the only Python in the project)

```python
# eval/eval.py
import json, httpx
from ragas import evaluate
from ragas.metrics import faithfulness, answer_relevancy, context_precision
from datasets import Dataset

API = "http://localhost:5000/api/chat"

def ask(q: str) -> dict:
    # consume the SSE stream into a single answer + sources
    answer, sources = "", []
    with httpx.stream("POST", API, json={"question": q}, timeout=60) as r:
        for line in r.iter_lines():
            if not line.startswith("data: "): continue
            data = line[6:].strip()
            if data == "[DONE]": break
            chunk = json.loads(data)
            if chunk["type"] == "token": answer += chunk.get("token", "")
            elif chunk["type"] == "sources": sources = chunk["sources"]
    return {"answer": answer, "sources": sources}

def build(golden):
    rows = []
    for item in golden:
        r = ask(item["question"])
        rows.append({
            "question": item["question"],
            "answer": r["answer"],
            "contexts": [s["excerpt"] for s in r["sources"]],
            "ground_truth": item["expected_answer"],
        })
    return Dataset.from_list(rows)

golden = json.load(open("golden_set.json"))
results = evaluate(build(golden), metrics=[faithfulness, answer_relevancy, context_precision])
print(results)  # → {'faithfulness': 0.87, 'answer_relevancy': 0.82, 'context_precision': 0.79}
```

### CI quality gate (Phase 3)

Run `eval.py` against a fixed document set in CI; fail the build if `faithfulness < 0.8`. This is the RAGOps feedback loop turned into an automated gate — the single most valuable thing that separates this project from a typical course submission.

### Observability

`ActivitySource("DocumentQA.Rag")` already instruments the use case (Section 8). In Phase 3, export to Application Insights and add custom metrics: retrieval latency, tokens per request, chunks retrieved, guardrail rejection rate.

---

## 14. Security & Compliance (cross-cutting)

| Concern | MVP | Production path |
|---|---|---|
| Authentication | API key / dev only | Azure AD (Entra) + MSAL in Angular, JWT to API |
| Transport | HTTPS local dev cert | Managed TLS on Container Apps |
| Secrets | User Secrets / appsettings | Azure Key Vault + Managed Identity |
| Audit logs | Console logging | Structured logs → Log Analytics |
| Data residency | Single region | Region-pinned Azure resources |
| Encryption at rest | (n/a local) | Azure Storage + AI Search default encryption |

These are deliberately out of MVP scope but named here so the architecture leaves room: auth is middleware, secrets are already behind `IConfiguration`, and the audit trail rides on the existing OpenTelemetry instrumentation.

---

## 15. Local Setup

```yaml
# infra/docker-compose.yml
services:
  qdrant:
    image: qdrant/qdrant
    ports: ["6333:6333"]
    volumes: ["qdrant_storage:/qdrant/storage"]
volumes:
  qdrant_storage:
```

```json
// Api/appsettings.Development.json
{
  "OpenAI": { "ApiKey": "sk-..." },
  "Qdrant": { "Host": "localhost", "Port": 6333 },
  "Rag": {
    "ChunkSize": 500, "ChunkOverlap": 100,
    "RetrievalTopK": 10, "RerankTopN": 5, "MinRelevanceScore": 0.7
  }
}
```

---

## 16. Deployment (Phase 4)

```dockerfile
# infra/Dockerfile.api
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/DocumentQA.Api/DocumentQA.Api.csproj -c Release -o /app/publish
FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocumentQA.Api.dll"]
```

```dockerfile
# infra/Dockerfile.ui
FROM node:22 AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build -- --configuration production
FROM nginx:alpine
COPY --from=build /app/dist/document-qa-ui/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
```

Target: Azure Container Apps (API + UI), Azure Container Registry for images, Azure AI Search + Blob Storage wired in Phase 2.

---

## 17. Key Design Decisions

**Why Clean Architecture for a course project?**
Because the course adds a capability per lesson. Without ports, every new capability means editing the orchestration and risking regressions. With ports, each lesson adds an adapter behind an existing interface — the use case never changes. The architecture *is* the syllabus.

**Why no-op / identity adapters in the MVP?**
They let the full pipeline run end-to-end on day one. `PassThroughQueryProcessor` and `IdentityReranker` satisfy the contract so `AskQuestionHandler` is final from Phase 1. Replacing them later is a one-line DI change — the highest-leverage pattern in the whole design.

**Why OpenAI API directly (not Azure OpenAI)?**
Zero infrastructure — just a key. It sits behind `IChatCompletionPort` / `IEmbeddingPort`, so switching to Azure OpenAI is one adapter + one DI line, no caller changes.

**Why Qdrant first, then Azure AI Search?**
Qdrant is free and local for development. Azure AI Search adds hybrid search and metadata filtering in Phase 2. Both implement `IVectorStore`; swapping is a DI line.

**Why Semantic Kernel under the adapters, not as the architecture?**
SK is a great client for OpenAI/Azure OpenAI and memory connectors — but it's an *implementation detail*. Keeping it inside Infrastructure adapters (not leaking `Kernel` into use cases) means SK can be replaced without rewriting the domain.

**Why eval and observability from day one?**
A RAG system's quality is invisible without measurement. The judge-LLM eval and OpenTelemetry traces are what let you prove (and improve) retrieval and generation quality — and a CI eval gate is the standout feature for the final presentation.

**Why Angular 22 signals + zoneless?**
Angular 22 (June 2026) makes signals and zoneless the default. Building signal-first from the start avoids a future migration and matches current best practice.

---

## 18. What to Add After the Course

- **Multitenancy** — `tenantId` on `ChunkMetadata`, filter in `IVectorStore`/`ISearchFilter`, `X-Tenant-Id` resolved from JWT. The metadata field already exists; only the filter and auth are new.
- **Agentic layer** — promote the use case to a tool-using agent: `SearchDocumentsTool`, `SummarizeTool`, with the LLM choosing. The ports become the tools.
- **Conversation memory** — persist history (Cosmos DB), inject last N turns via the prompt builder.
- **Multimodal ingestion** — Azure Document Intelligence adapter for `IDocumentParser` (tables, scanned pages, images).
- **Semantic cache** — `IResponseCache` adapter keyed on query embedding similarity.
