# Document Q&A Assistant

RAG system that lets users upload company documents (PDF, DOCX, TXT) and ask questions about them in natural language. Answers are grounded in the source documents with page-level citations.

**Stack:** .NET 10 / ASP.NET Core · Semantic Kernel · OpenAI API · Qdrant · Angular 22 · Python/Ragas

---

## Prerequisites

| Tool | Version | Purpose |
|---|---|---|
| Docker Desktop | Latest | Run the full stack |
| .NET 10 SDK | 10.0+ | Local backend development |
| Node.js + Angular CLI | Node 22 / ng 22 | Local frontend development |
| Python | 3.11+ | Evaluation script |
| OpenAI API key | — | GPT-4o + embeddings |

> **Right now you only need Docker Desktop** to run the full stack.  
> .NET SDK and Node.js are only needed when developing locally without Docker.

---

## Quick Start (Docker — all you need)

1. **Copy the API key template:**

```bash
cp src/DocumentQA.Api/appsettings.Development.json.template src/DocumentQA.Api/appsettings.Development.json
# Edit the file and replace sk-YOUR_OPENAI_API_KEY_HERE with your real key
```

2. **Start the full stack:**

```bash
OPENAI_API_KEY=sk-your-key docker compose up --build
```

| Service | URL |
|---|---|
| Angular UI | http://localhost:4200 |
| .NET API | http://localhost:5000 |
| Qdrant dashboard | http://localhost:6333/dashboard |

3. **Upload a document** via the UI header, then ask a question in the chat.

---

## Local Development

### Backend

```bash
# Start Qdrant only
docker compose -f docker-compose.dev.yml up -d

# Copy and configure secrets
cp src/DocumentQA.Api/appsettings.Development.json.template src/DocumentQA.Api/appsettings.Development.json
# Add your OpenAI API key to appsettings.Development.json

dotnet restore DocumentQA.sln
dotnet run --project src/DocumentQA.Api
# API available at http://localhost:5000
```

### Frontend

```bash
cd ui/document-qa-ui
npm install
ng serve
# UI available at http://localhost:4200
```

### Test the API directly

```bash
# Upload a document
curl -X POST http://localhost:5000/api/documents/upload \
  -F "file=@docs/test-documents/your-document.pdf"

# Ask a question (SSE stream)
curl -N -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"question": "What is this document about?"}'
```

---

## Evaluation

```bash
cd eval
pip install -r requirements.txt
# Make sure the API is running and documents are ingested
python eval.py
```

Target: `faithfulness >= 0.80` on the golden test set.

---

## Project Structure

```
rag-project/
├── src/
│   ├── DocumentQA.Core/        Domain models and interfaces
│   ├── DocumentQA.Ingestion/   PDF/DOCX/TXT parsers, chunker, ingestion service
│   └── DocumentQA.Api/         ASP.NET Core API, RAG service, endpoints
├── ui/
│   └── document-qa-ui/         Angular 22 SPA (standalone, zoneless, signals)
├── eval/
│   ├── eval.py                 Ragas evaluation script
│   └── golden_set.json         Sample Q&A pairs — replace with real test cases
├── docs/
│   └── test-documents/         Drop sample PDFs/DOCX here for testing
├── docker-compose.yml          Full stack (Qdrant + API + UI)
└── docker-compose.dev.yml      Qdrant only (for local dotnet run + ng serve)
```

---

## Architecture

```
Angular SPA  ──POST /api/chat──►  ASP.NET Core API
             ◄── SSE stream ──     │
                                   ├─ Semantic Kernel
             ──POST /api/documents/upload──►  │
                                   ├─ Embed question  → OpenAI text-embedding-3-small
                                   ├─ Vector search   → Qdrant
                                   ├─ Build prompt    → system prompt + top-5 chunks
                                   └─ LLM stream      → OpenAI GPT-4o
```

---

## Phase Roadmap

| Phase | What | Status |
|---|---|---|
| 1 | Ingestion pipeline: upload → parse → chunk → embed → Qdrant | Scaffolded |
| 2 | RAG chat endpoint with SSE streaming | Scaffolded |
| 3 | Angular UI + Ragas eval + Azure deployment | Scaffolded |

See [rag-implementation-plan.md](rag-implementation-plan.md) for the full design document.

---

## Azure Migration (Phase 2+)

Replace Qdrant with Azure AI Search — one line in `Program.cs`:

```csharp
builder.Services.AddAzureAISearchAsVectorStore(
    new Uri(config["AzureSearch:Endpoint"]!),
    new AzureKeyCredential(config["AzureSearch:ApiKey"]!));
```

Switch OpenAI → Azure OpenAI — one line in `Program.cs`:

```csharp
// Replace AddOpenAIChatCompletion with:
builder.Services.AddAzureOpenAIChatCompletion(
    deploymentName: "gpt-4o",
    endpoint: config["AzureOpenAI:Endpoint"]!,
    apiKey: config["AzureOpenAI:ApiKey"]!);
```
