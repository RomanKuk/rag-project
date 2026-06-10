namespace DocumentQA.Application.Abstractions.Cache;

public interface ISemanticCache
{
    Task<string?> TryGetAsync(float[] queryEmbedding, string tenantId, CancellationToken ct);
    Task StoreAsync(float[] queryEmbedding, string query, string response, string tenantId, CancellationToken ct);
}
