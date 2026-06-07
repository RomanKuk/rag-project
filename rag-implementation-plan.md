# Document Q&A Assistant — Implementation Plan

> RAG-система на ASP.NET Core + Semantic Kernel + OpenAI API + Azure AI Search  
> MVP без мультитенантності · Курсовий проєкт · 20 занять

---

## Project Overview

| Item | Value |
|---|---|
| **Type** | RAG System |
| **Stack** | .NET 10 / C#, Angular 22, Python (eval only) |
| **Cloud** | Azure |
| **LLM** | OpenAI API — GPT-4o |
| **Embeddings** | OpenAI API — text-embedding-3-small |
| **Vector DB** | Qdrant (local MVP) → Azure AI Search (Phase 2+) |
| **Document store** | Local filesystem → Azure Blob Storage |
| **Orchestration** | Semantic Kernel |
| **Eval** | Ragas (Python) |
| **UI** | Angular 22 SPA |

---

## Business Problem

Users spend significant time manually searching through company documents (PDFs, DOCX, technical docs, regulations). The system allows users to ask questions in natural language and receive accurate answers grounded in the source documents — with a reference to the exact document and page.

**Success metrics:**
- Faithfulness ≥ 0.8 on a golden test set of 15–20 Q&A pairs (judge-LLM scored)
- 90%+ of test questions answered without hallucination
- Answer returned in < 5 seconds end-to-end

---

## Architecture

```
User
 │
 ▼
Angular SPA (Chat UI + Document Upload)
 │  POST /api/chat  { question, conversationId }
 │  POST /api/documents/upload
 ▼
ASP.NET Core API  (CORS enabled)
 │
 ├── Semantic Kernel RAG Pipeline
 │    ├── 1. Embed question  →  OpenAI API (text-embedding-3-small)
 │    ├── 2. Vector search   →  Qdrant / Azure AI Search
 │    ├── 3. Build prompt    →  system prompt + top-K chunks
 │    └── 4. LLM call        →  OpenAI API (GPT-4o), streaming SSE
 │
 └── Response: { answer, sources: [{ docName, page, excerpt }] }

Ingestion (triggered by upload)
 Document upload → Parser → Chunker → Embedder → Vector DB
```

---

## Phase 1 — Ingestion Pipeline

**Goal:** Upload documents, parse them, chunk, embed, and store in a vector DB.  
**Output:** A queryable collection of chunks with metadata.  
**Estimated time:** 1–2 weekends.

### 1.1 Project Setup

```
/src
  DocumentQA.Api/          ← ASP.NET Core Web API
  DocumentQA.Core/         ← Domain: interfaces, models
  DocumentQA.Ingestion/    ← Ingestion pipeline (called from API)
/ui
  document-qa-ui/          ← Angular 22 SPA
/eval
  eval.py                  ← Ragas evaluation script
  golden_set.json          ← Test Q&A pairs
/docs
  test-documents/          ← Sample PDFs and DOCX for development
docker-compose.yml         ← Qdrant local
```

**NuGet packages:**
```xml
<!-- Core -->
<PackageReference Include="Microsoft.SemanticKernel" Version="1.*" />
<PackageReference Include="Microsoft.SemanticKernel.Connectors.OpenAI" Version="1.*" />
<PackageReference Include="Microsoft.SemanticKernel.Connectors.Qdrant" Version="1.*" />

<!-- Document parsing -->
<PackageReference Include="PdfPig" Version="0.1.*" />
<PackageReference Include="DocumentFormat.OpenXml" Version="3.*" />

<!-- Azure (Phase 2+) -->
<PackageReference Include="Azure.Storage.Blobs" Version="12.*" />
<PackageReference Include="Azure.Search.Documents" Version="11.*" />
```

### 1.2 Document Model

```csharp
public record DocumentChunk
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string DocumentName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int PageNumber { get; init; }
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = "";
    public float[] Embedding { get; init; } = [];
    public DateTime IngestedAt { get; init; } = DateTime.UtcNow;
}
```

### 1.3 Document Parser

Support PDF, DOCX, and TXT. Each parser implements `IDocumentParser`:

```csharp
public interface IDocumentParser
{
    bool CanHandle(string fileExtension);
    IAsyncEnumerable<ParsedPage> ParseAsync(Stream fileStream, string fileName);
}

public record ParsedPage(int PageNumber, string Text, string FileName);
```

**PDF parser** using PdfPig:
```csharp
public class PdfParser : IDocumentParser
{
    public bool CanHandle(string ext) => ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName)
    {
        using var doc = PdfDocument.Open(stream);
        foreach (var page in doc.GetPages())
        {
            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            yield return new ParsedPage(page.Number, text, fileName);
        }
    }
}
```

**DOCX parser** using OpenXml:
```csharp
public class DocxParser : IDocumentParser
{
    public bool CanHandle(string ext) => ext.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var text = body.InnerText;
        yield return new ParsedPage(1, text, fileName);
    }
}
```

### 1.4 Chunker

Sliding window chunking with overlap to preserve context across chunk boundaries:

```csharp
public class SlidingWindowChunker
{
    private readonly int _chunkSize;    // default: 500 words
    private readonly int _overlapSize;  // default: 100 words

    public IEnumerable<string> Chunk(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var step = _chunkSize - _overlapSize;

        for (int i = 0; i < words.Length; i += step)
        {
            var chunk = words.Skip(i).Take(_chunkSize);
            yield return string.Join(" ", chunk);
            if (i + _chunkSize >= words.Length) break;
        }
    }
}
```

**Chunking decisions:**
- Chunk size: 500 words — enough context, small enough for precision
- Overlap: 100 words — prevents answers from being cut at chunk boundaries
- Sentence boundary detection — add in v2

### 1.5 Embedding + Vector Storage

```csharp
public class IngestionService
{
    private readonly IKernelMemory _memory;
    private readonly SlidingWindowChunker _chunker;
    private readonly IEnumerable<IDocumentParser> _parsers;

    public async Task IngestAsync(Stream fileStream, string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.First(p => p.CanHandle(ext));

        await foreach (var page in parser.ParseAsync(fileStream, fileName))
        {
            var chunks = _chunker.Chunk(page.Text);
            foreach (var (chunk, idx) in chunks.Select((c, i) => (c, i)))
            {
                await _memory.SaveInformationAsync(
                    collection: "documents",
                    text: chunk,
                    id: $"{fileName}-p{page.PageNumber}-c{idx}",
                    additionalMetadata: JsonSerializer.Serialize(new
                    {
                        docName = fileName,
                        page = page.PageNumber,
                        chunkIndex = idx
                    })
                );
            }
        }
    }
}
```

### 1.6 Upload Endpoint + CORS

```csharp
// Program.cs
builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy => policy
        .WithOrigins("http://localhost:4200")  // Angular dev server
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// OpenAI via API key — no Azure resource needed
builder.Services.AddOpenAIChatCompletion(
    modelId: "gpt-4o",
    apiKey: builder.Configuration["OpenAI:ApiKey"]!
);
builder.Services.AddOpenAITextEmbeddingGeneration(
    modelId: "text-embedding-3-small",
    apiKey: builder.Configuration["OpenAI:ApiKey"]!
);

app.UseCors("Angular");

app.MapPost("/api/documents/upload", async (
    IFormFile file,
    IngestionService ingestionService) =>
{
    using var stream = file.OpenReadStream();
    await ingestionService.IngestAsync(stream, file.FileName);
    return Results.Ok(new { message = "Document ingested", fileName = file.FileName });
}).DisableAntiforgery();
```

### 1.7 Local Setup (Phase 1)

```yaml
# docker-compose.yml
services:
  qdrant:
    image: qdrant/qdrant
    ports:
      - "6333:6333"
    volumes:
      - qdrant_storage:/qdrant/storage

volumes:
  qdrant_storage:
```

```json
// appsettings.Development.json
{
  "OpenAI": {
    "ApiKey": "sk-..."
  },
  "Qdrant": {
    "Host": "localhost",
    "Port": 6333
  }
}
```

**Phase 1 done when:** Upload a PDF via `/api/documents/upload` and verify chunks appear in Qdrant dashboard (`http://localhost:6333/dashboard`).

---

## Phase 2 — RAG Chat Endpoint

**Goal:** Take a user question, retrieve relevant chunks, build a prompt, call the LLM, stream back a grounded answer with source citations.  
**Output:** `POST /api/chat` with Server-Sent Events streaming.  
**Estimated time:** 1 weekend on top of Phase 1.

### 2.1 Request / Response Models

```csharp
public record ChatRequest(
    string Question,
    string? ConversationId = null
);

public record ChatResponse(
    string Answer,
    List<SourceReference> Sources
);

public record SourceReference(
    string DocumentName,
    int Page,
    string Excerpt
);
```

### 2.2 System Prompt

```csharp
const string SystemPrompt = """
    You are a document assistant. Answer the user's question ONLY based on the provided context.

    Rules:
    - If the answer is not in the context, say "I cannot find this information in the available documents."
    - Always cite the source document and page number for each claim.
    - Be concise and factual. Do not add information from your general knowledge.
    - Format citations inline as: [DocumentName, page X]

    Context:
    {context}
    """;
```

### 2.3 RAG Pipeline

```csharp
public class RagService
{
    private readonly Kernel _kernel;
    private readonly IKernelMemory _memory;

    public async IAsyncEnumerable<string> AskAsync(string question)
    {
        // Step 1: Retrieve top-K relevant chunks
        var searchResults = await _memory.SearchAsync(
            collection: "documents",
            query: question,
            limit: 5,
            minRelevanceScore: 0.7
        );

        // Step 2: Build context + sources
        var contextBuilder = new StringBuilder();
        var sources = new List<SourceReference>();

        foreach (var result in searchResults)
        {
            var meta = JsonSerializer.Deserialize<ChunkMetadata>(result.Metadata.AdditionalMetadata);
            contextBuilder.AppendLine($"[{meta!.DocName}, page {meta.Page}]");
            contextBuilder.AppendLine(result.Metadata.Text);
            contextBuilder.AppendLine();

            sources.Add(new SourceReference(
                meta.DocName,
                meta.Page,
                result.Metadata.Text[..Math.Min(200, result.Metadata.Text.Length)]
            ));
        }

        // Step 3: Build prompt
        var prompt = SystemPrompt.Replace("{context}", contextBuilder.ToString());

        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(prompt);
        chatHistory.AddUserMessage(question);

        // Step 4: Stream response
        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(chatHistory))
        {
            yield return chunk.Content ?? "";
        }
    }
}
```

### 2.4 Streaming Chat Endpoint (SSE)

```csharp
app.MapPost("/api/chat", async (
    ChatRequest request,
    RagService ragService,
    HttpContext httpContext) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    await foreach (var chunk in ragService.AskAsync(request.Question))
    {
        var json = JsonSerializer.Serialize(new { token = chunk });
        await httpContext.Response.WriteAsync($"data: {json}\n\n");
        await httpContext.Response.Body.FlushAsync();
    }

    // Signal stream end
    await httpContext.Response.WriteAsync("data: [DONE]\n\n");
    await httpContext.Response.Body.FlushAsync();
});
```

### 2.5 Switch to Azure AI Search (end of Phase 2)

Replace Qdrant with Azure AI Search for hybrid search (vector + keyword):

```csharp
// Program.cs — one-line swap
builder.Services.AddAzureAISearchAsVectorStore(
    new Uri(builder.Configuration["AzureSearch:Endpoint"]!),
    new AzureKeyCredential(builder.Configuration["AzureSearch:ApiKey"]!));
```

Azure AI Search index schema:
```json
{
  "name": "documents",
  "fields": [
    { "name": "id", "type": "Edm.String", "key": true },
    { "name": "content", "type": "Edm.String", "searchable": true },
    { "name": "docName", "type": "Edm.String", "filterable": true },
    { "name": "page", "type": "Edm.Int32", "filterable": true },
    {
      "name": "embedding",
      "type": "Collection(Edm.Single)",
      "dimensions": 1536,
      "vectorSearchProfile": "default"
    }
  ]
}
```

### 2.6 Azure Blob Storage + Functions Trigger

Upload flow in Phase 2:
```
Angular upload → Blob Storage (/raw-documents) → Azure Function (BlobTrigger) → Ingestion → Azure AI Search
```

```csharp
// Azure Function — blob trigger
public class IngestionFunction
{
    [Function("IngestDocument")]
    public async Task Run(
        [BlobTrigger("raw-documents/{name}")] Stream blobStream,
        string name,
        FunctionContext context)
    {
        var ingestionService = context.InstanceServices.GetRequiredService<IngestionService>();
        await ingestionService.IngestAsync(blobStream, name);
    }
}
```

**Phase 2 done when:** `curl -X POST http://localhost:5000/api/chat -d '{"question":"..."}'` streams back a grounded answer with source citations.

---

## Phase 3 — Eval + Angular UI

**Goal:** Measure quality, build the Angular chat interface, deploy.  
**Output:** Scored system + demo-ready SPA.  
**Estimated time:** 1 weekend.

### 3.1 Golden Test Set

```json
// eval/golden_set.json
[
  {
    "question": "What is the maximum file size allowed for uploads?",
    "expected_answer": "The maximum file size is 50MB per document.",
    "source_document": "technical-spec.pdf",
    "source_page": 3
  },
  {
    "question": "Who is responsible for data processing under GDPR?",
    "expected_answer": "The Data Controller is responsible...",
    "source_document": "privacy-policy.pdf",
    "source_page": 1
  }
]
```

### 3.2 Ragas Evaluation (Python)

```python
# eval/eval.py
import json, httpx
from ragas import evaluate
from ragas.metrics import faithfulness, answer_relevancy, context_precision
from datasets import Dataset

API_URL = "http://localhost:5000/api/chat"

def ask(question: str) -> dict:
    resp = httpx.post(API_URL, json={"question": question}, timeout=30)
    return resp.json()

def build_dataset(golden_set: list) -> Dataset:
    rows = []
    for item in golden_set:
        result = ask(item["question"])
        rows.append({
            "question": item["question"],
            "answer": result["answer"],
            "contexts": [s["excerpt"] for s in result["sources"]],
            "ground_truth": item["expected_answer"]
        })
    return Dataset.from_list(rows)

with open("golden_set.json") as f:
    golden = json.load(f)

results = evaluate(build_dataset(golden),
                   metrics=[faithfulness, answer_relevancy, context_precision])
print(results)
# → {'faithfulness': 0.87, 'answer_relevancy': 0.82, 'context_precision': 0.79}
```

```bash
pip install ragas datasets httpx openai
python eval/eval.py
```

### 3.3 Angular App Structure

```bash
ng new document-qa-ui --routing --style=scss --zoneless
cd document-qa-ui
ng generate service services/chat
ng generate service services/document
ng generate component components/chat
ng generate component components/document-upload
```

```
/ui/document-qa-ui/src/app/
  components/
    chat/
      chat.component.ts        ← main chat interface
      chat.component.html
      chat.component.scss
    document-upload/
      document-upload.component.ts
  services/
    chat.service.ts            ← SSE streaming + HTTP
    document.service.ts        ← file upload
  models/
    chat.models.ts             ← interfaces
```

### 3.4 Chat Models

```typescript
// models/chat.models.ts
export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  sources: SourceReference[];
  isStreaming?: boolean;
}

export interface SourceReference {
  documentName: string;
  page: number;
  excerpt: string;
}
```

### 3.5 Chat Service (SSE Streaming)

Use `fetch` + `ReadableStream` for POST-based SSE — the native `EventSource` API only supports GET:

```typescript
// services/chat.service.ts
import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class ChatService {
  private apiUrl = environment.apiUrl;

  async *streamAnswer(question: string): AsyncGenerator<string> {
    const response = await fetch(`${this.apiUrl}/api/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question }),
    });

    if (!response.body) throw new Error('No response body');

    const reader = response.body.getReader();
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

        try {
          const parsed = JSON.parse(data);
          if (parsed.token) yield parsed.token;
        } catch {
          // skip malformed chunk
        }
      }
    }
  }
}
```

### 3.6 Document Upload Service

```typescript
// services/document.service.ts
import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class DocumentService {
  constructor(private http: HttpClient) {}

  upload(file: File): Observable<{ message: string; fileName: string }> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<any>(`${environment.apiUrl}/api/documents/upload`, formData);
  }
}
```

### 3.7 Chat Component

```typescript
// components/chat/chat.component.ts
import { Component } from '@angular/core';
import { ChatService } from '../../services/chat.service';
import { ChatMessage } from '../../models/chat.models';

@Component({
  selector: 'app-chat',
  templateUrl: './chat.component.html',
  styleUrls: ['./chat.component.scss']
})
export class ChatComponent {
  messages: ChatMessage[] = [];
  question = '';
  isLoading = false;

  constructor(private chatService: ChatService) {}

  async sendMessage() {
    if (!this.question.trim() || this.isLoading) return;

    const userMessage: ChatMessage = {
      role: 'user',
      content: this.question,
      sources: []
    };
    this.messages.push(userMessage);

    const assistantMessage: ChatMessage = {
      role: 'assistant',
      content: '',
      sources: [],
      isStreaming: true
    };
    this.messages.push(assistantMessage);

    const question = this.question;
    this.question = '';
    this.isLoading = true;

    try {
      for await (const token of this.chatService.streamAnswer(question)) {
        assistantMessage.content += token;
      }
    } finally {
      assistantMessage.isStreaming = false;
      this.isLoading = false;
    }
  }

  onKeyDown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.sendMessage();
    }
  }
}
```

```html
<!-- components/chat/chat.component.html -->
<div class="chat-container">
  <div class="messages" #scrollContainer>
    @for (msg of messages; track $index) {
      <div class="message" [class]="msg.role">
        <div class="content">
          {{ msg.content }}
          @if (msg.isStreaming) { <span class="cursor">▋</span> }
        </div>
        @if (msg.sources.length > 0) {
          <div class="sources">
            <span class="sources-label">Sources:</span>
            @for (src of msg.sources; track src.documentName) {
              <span class="source-tag">{{ src.documentName }} p.{{ src.page }}</span>
            }
          </div>
        }
      </div>
    }
  </div>

  <div class="input-area">
    <textarea
      [(ngModel)]="question"
      (keydown)="onKeyDown($event)"
      placeholder="Ask a question about your documents..."
      rows="2">
    </textarea>
    <button (click)="sendMessage()" [disabled]="isLoading || !question.trim()">
      {{ isLoading ? '...' : 'Send' }}
    </button>
  </div>
</div>
```

### 3.8 Environment Config

```typescript
// environments/environment.ts
export const environment = {
  production: false,
  apiUrl: 'http://localhost:5000'
};

// environments/environment.prod.ts
export const environment = {
  production: true,
  apiUrl: 'https://YOUR_CONTAINER_APP.azurecontainerapps.io'
};
```

### 3.9 Azure Deployment

```dockerfile
# API Dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish DocumentQA.Api/DocumentQA.Api.csproj -c Release -o /app/publish

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocumentQA.Api.dll"]
```

```dockerfile
# Angular Dockerfile
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

```nginx
# nginx.conf — handle Angular routing
server {
  listen 80;
  root /usr/share/nginx/html;
  index index.html;

  location / {
    try_files $uri $uri/ /index.html;
  }
}
```

**Phase 3 done when:** Ragas scores ≥ 0.8 faithfulness, Angular UI streams answers with source tags, both containers deployed to Azure Container Apps.

---

## Azure Services Summary

| Service | Purpose | When |
|---|---|---|
| **OpenAI API** | GPT-4o (chat) + text-embedding-3-small | Phase 1 |
| **Blob Storage** | Raw document storage, upload trigger | Phase 2 |
| **Azure Functions** | Blob trigger → ingestion pipeline | Phase 2 |
| **Azure AI Search** | Vector DB with hybrid search | Phase 2 |
| **Container Apps** | API + Angular hosting, autoscale | Phase 3 |
| **Azure Container Registry** | Docker image storage | Phase 3 |
| **Static Web Apps** | Alternative Angular hosting (simpler) | Phase 3 |

> **OpenAI vs Azure OpenAI:** For MVP use OpenAI API directly (`sk-...` key, zero Azure setup).  
> Switch to Azure OpenAI later via one config line in `Program.cs` — no other changes needed.

---

## Development Roadmap

```
Week 1–2   Phase 1: Parser + Chunker + Qdrant local
Week 3     Phase 1: Embed + Upload endpoint + verify in Qdrant dashboard
Week 4–5   Phase 2: RAG pipeline + streaming SSE endpoint
Week 6     Phase 2: Switch to Azure AI Search + Blob + Functions
Week 7     Phase 3: Golden test set + Ragas eval script
Week 8     Phase 3: Angular UI + SSE streaming + deploy both containers
```

---

## Key Design Decisions

**Why OpenAI API directly vs Azure OpenAI?**  
Zero infrastructure to set up — just an API key. Semantic Kernel abstracts the difference completely. Switch to Azure OpenAI for production with one line in `Program.cs`.

**Why Angular over Blazor?**  
Broader recognition outside the .NET ecosystem, stronger portfolio signal, and a cleaner separation between frontend and backend. The API contract stays identical.

**Why Angular 22 specifically?**  
Angular 22 (released June 3, 2026) is the "signal-first era" release — Signal Forms, Resource API, and zoneless change detection are all stable. `OnPush` is now the default for new components, and the project scaffolds without Zone.js by default (`--zoneless` flag). This is the right foundation for a new project in 2026.

**Why `fetch` + `ReadableStream` for streaming, not `EventSource`?**  
`EventSource` only supports GET requests. Since the chat endpoint is POST (to send the question in the body), `fetch` with a `ReadableStream` reader is the correct approach for SSE over POST.

**Why Semantic Kernel over calling OpenAI SDK directly?**  
Semantic Kernel handles the memory/retrieval abstraction, lets you swap OpenAI → Azure OpenAI → any other provider with zero pipeline changes, and has first-class .NET support.

**Why Qdrant first, then Azure AI Search?**  
Qdrant runs locally in Docker — zero cost, zero cloud dependency during development. AI Search adds hybrid search and Azure-native integration in Phase 2. Switching is one config change.

**Why Python only for eval?**  
Ragas has no .NET equivalent. A single Python script that calls the .NET API over HTTP keeps Python entirely out of production.

---

## What to Add After MVP (Post-course)

- **Multi-tenancy** — namespace/filter per tenant in Azure AI Search, `X-Tenant-Id` header. No architecture changes.
- **Conversation memory** — store chat history in Cosmos DB, inject last N turns into the prompt.
- **Agent layer** — add Semantic Kernel plugins (`SearchDocumentsTool`, `SummarizeDocumentTool`) so the LLM decides which tool to call.
- **Reranking** — cross-encoder reranker between retrieval and prompt building to improve top-K quality.
- **Azure Document Intelligence** — replace PdfPig for complex PDFs with tables, forms, or scanned pages.
- **Auth** — add Azure AD / MSAL to Angular, propagate JWT to the API.
