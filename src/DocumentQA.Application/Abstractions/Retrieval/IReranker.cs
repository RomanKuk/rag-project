using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IReranker
{
    Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken ct);
}
