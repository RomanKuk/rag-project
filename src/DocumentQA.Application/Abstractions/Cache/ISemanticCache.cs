using DocumentQA.Application.Models;

namespace DocumentQA.Application.Abstractions.Cache;

public interface ISemanticCache
{
    Task<string?> TryGetAsync(float[] queryEmbedding, RetrievalScope scope, CancellationToken ct);
    Task StoreAsync(float[] queryEmbedding, string query, string response, RetrievalScope scope, CancellationToken ct);
}
