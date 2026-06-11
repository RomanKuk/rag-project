using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Infrastructure.Retrieval;

/// <summary>
/// Bridges the RerankerStrategy="crossencoder" path to the ICrossEncoderReranker
/// adapter (Cohere when configured, Null otherwise).
/// </summary>
public sealed class CrossEncoderRerankerStrategy(ICrossEncoderReranker crossEncoder) : IReranker
{
    public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query, IReadOnlyList<RetrievedChunk> candidates, int topN, CancellationToken ct)
        => crossEncoder.RerankAsync(query, candidates, topN, ct);
}
