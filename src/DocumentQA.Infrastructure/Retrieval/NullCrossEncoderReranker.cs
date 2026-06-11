using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Infrastructure.Retrieval;

public sealed class NullCrossEncoderReranker : ICrossEncoderReranker
{
    public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RetrievedChunk>>(candidates.Take(topN).ToList());
}
