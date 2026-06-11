using DocumentQA.Application.Models;
using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Identity;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IVectorStore
{
    Task UpsertAsync(
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        RetrievalScope scope,
        CancellationToken ct);

    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding, int topK, double minScore,
        RetrievalScope scope, CancellationToken ct);

    Task<IReadOnlyList<RetrievedChunk>> SearchHybridAsync(
        float[] dense, IReadOnlyList<string> keywords, int topK, double minScore,
        RetrievalScope scope, CancellationToken ct);

    Task<IReadOnlyList<string>> ListDocumentNamesAsync(RetrievalScope scope, CancellationToken ct);
    Task DeleteDocumentAsync(string documentName, RetrievalScope scope, CancellationToken ct);
    Task DeleteByChatAsync(Guid chatId, CancellationToken ct);
    Task<long> CountDocumentsAsync(string tenantId, CancellationToken ct);
    Task<long> CountChunksAsync(string tenantId, CancellationToken ct);
}
