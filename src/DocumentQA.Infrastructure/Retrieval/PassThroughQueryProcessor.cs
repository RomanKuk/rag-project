using DocumentQA.Application.Abstractions.Retrieval;

namespace DocumentQA.Infrastructure.Retrieval;

public sealed class PassThroughQueryProcessor : IQueryProcessor
{
    public Task<ProcessedQuery> ProcessAsync(string rawQuery, CancellationToken ct)
        => Task.FromResult(new ProcessedQuery(rawQuery));
}
