using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Retrieval;

public interface ICrossEncoderReranker
{
    Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken ct);
}
