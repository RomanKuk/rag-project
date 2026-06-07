namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IQueryProcessor
{
    Task<ProcessedQuery> ProcessAsync(string rawQuery, CancellationToken ct);
}

public sealed record ProcessedQuery(string SearchText, string Intent = "qa");
