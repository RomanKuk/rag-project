namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IQueryProcessor
{
    Task<ProcessedQuery> ProcessAsync(string rawQuery, CancellationToken ct);
}

public sealed record ProcessedQuery(
    string SearchText,
    string Intent = "qa",
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? SubQueries = null)
{
    public IReadOnlyList<string> Keywords   { get; init; } = Keywords   ?? [];
    public IReadOnlyList<string> SubQueries { get; init; } = SubQueries ?? [];
}
