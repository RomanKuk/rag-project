using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryEmbedding, int topK, double minScore, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> SearchHybridAsync(float[] dense, IReadOnlyList<string> keywords, int topK, double minScore, CancellationToken ct);
}
