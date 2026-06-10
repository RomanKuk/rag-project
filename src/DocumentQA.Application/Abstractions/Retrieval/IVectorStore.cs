using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<float[]> embeddings, string tenantId, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryEmbedding, int topK, double minScore, string tenantId, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> SearchHybridAsync(float[] dense, IReadOnlyList<string> keywords, int topK, double minScore, string tenantId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListDocumentNamesAsync(string tenantId, CancellationToken ct);
    Task DeleteDocumentAsync(string documentName, string tenantId, CancellationToken ct);
}
