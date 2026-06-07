namespace DocumentQA.Application.Abstractions.Cache;

public interface ISemanticCache
{
    Task<string?> TryGetAsync(float[] queryEmbedding, CancellationToken ct);
    Task StoreAsync(float[] queryEmbedding, string query, string response, CancellationToken ct);
}
