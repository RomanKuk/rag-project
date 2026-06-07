using DocumentQA.Domain.Documents;

namespace DocumentQA.Domain.Retrieval;

public sealed record RetrievedChunk(DocumentChunk Chunk, double Score);
