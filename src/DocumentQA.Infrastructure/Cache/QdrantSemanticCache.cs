using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocumentQA.Infrastructure.Cache;

public sealed class QdrantSemanticCache : ISemanticCache
{
    private const string Collection = "cache_entries";
    private const ulong VectorSize = 1536;

    private readonly QdrantClient _client;
    private readonly CacheOptions _opts;
    private readonly ILogger<QdrantSemanticCache> _logger;

    public QdrantSemanticCache(
        QdrantClient client,
        IOptions<CacheOptions> opts,
        ILogger<QdrantSemanticCache> logger)
    {
        _client = client;
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<string?> TryGetAsync(float[] queryEmbedding, CancellationToken ct)
    {
        if (!_opts.Enabled) return null;

        await EnsureCollectionAsync(ct);

        var hits = await _client.SearchAsync(
            Collection,
            queryEmbedding,
            limit: 1,
            scoreThreshold: (float)_opts.SimilarityThreshold,
            cancellationToken: ct);

        if (hits.Count == 0) return null;

        var hit = hits[0];

        // Check TTL via expire_at payload field
        if (hit.Payload.TryGetValue("expire_at", out var expireAt))
        {
            var expireUnix = expireAt.IntegerValue;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expireUnix)
            {
                _logger.LogInformation("Cache EXPIRED (score={Score:F4})", hit.Score);
                await _client.DeleteAsync(Collection, Guid.Parse(hit.Id.Uuid), cancellationToken: ct);
                return null;
            }
        }

        _logger.LogInformation("Cache HIT (score={Score:F4})", hit.Score);
        return hit.Payload.TryGetValue("response", out var resp) ? resp.StringValue : null;
    }

    public async Task StoreAsync(
        float[] queryEmbedding,
        string query,
        string response,
        CancellationToken ct)
    {
        if (!_opts.Enabled) return;

        await EnsureCollectionAsync(ct);

        var expireAt = DateTimeOffset.UtcNow.AddMinutes(_opts.TtlMinutes).ToUnixTimeSeconds();
        var point = new PointStruct
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Vectors = queryEmbedding
        };
        point.Payload["query"] = query;
        point.Payload["response"] = response;
        point.Payload["model"] = "gpt-4o";
        point.Payload["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        point.Payload["expire_at"] = expireAt;

        await _client.UpsertAsync(Collection, [point], cancellationToken: ct);
        _logger.LogInformation("Cache STORED (ttl={Ttl}min, expires={Exp})",
            _opts.TtlMinutes, DateTimeOffset.FromUnixTimeSeconds(expireAt).ToString("HH:mm:ss"));
    }

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var existing = await _client.ListCollectionsAsync(ct);
        if (!existing.Contains(Collection))
        {
            await _client.CreateCollectionAsync(
                Collection,
                new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }
}
