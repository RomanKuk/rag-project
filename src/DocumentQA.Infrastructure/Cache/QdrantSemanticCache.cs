using DocumentQA.Application.Abstractions.Cache;
using DocumentQA.Application.Models;
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

    public async Task<string?> TryGetAsync(float[] queryEmbedding, RetrievalScope scope, CancellationToken ct)
    {
        if (!_opts.Enabled) return null;

        await EnsureCollectionAsync(ct);

        var cacheKey = CacheKey(scope);
        var filter = new Filter
        {
            Must =
            {
                new Condition { Field = new FieldCondition { Key = "cache_key", Match = new Match { Keyword = cacheKey } } }
            }
        };

        var hits = await _client.SearchAsync(
            Collection,
            queryEmbedding,
            filter: filter,
            limit: 1,
            scoreThreshold: (float)_opts.SimilarityThreshold,
            cancellationToken: ct);

        if (hits.Count == 0) return null;

        var hit = hits[0];

        if (hit.Payload.TryGetValue("expire_at", out var expireAt))
        {
            var expireUnix = expireAt.IntegerValue;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expireUnix)
            {
                _logger.LogInformation("Cache EXPIRED (score={Score:F4}, key={Key})", hit.Score, cacheKey);
                await _client.DeleteAsync(Collection, Guid.Parse(hit.Id.Uuid), cancellationToken: ct);
                return null;
            }
        }

        _logger.LogInformation("Cache HIT (score={Score:F4}, key={Key})", hit.Score, cacheKey);
        return hit.Payload.TryGetValue("response", out var resp) ? resp.StringValue : null;
    }

    public async Task StoreAsync(
        float[] queryEmbedding, string query, string response,
        RetrievalScope scope, CancellationToken ct)
    {
        if (!_opts.Enabled) return;

        await EnsureCollectionAsync(ct);

        var cacheKey = CacheKey(scope);
        var expireAt = DateTimeOffset.UtcNow.AddMinutes(_opts.TtlMinutes).ToUnixTimeSeconds();
        var point = new PointStruct
        {
            Id      = new PointId { Uuid = Guid.NewGuid().ToString() },
            Vectors = queryEmbedding
        };
        point.Payload["cache_key"] = cacheKey;
        point.Payload["query"]     = query;
        point.Payload["response"]  = response;
        point.Payload["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        point.Payload["expire_at"] = expireAt;

        await _client.UpsertAsync(Collection, [point], cancellationToken: ct);
        _logger.LogInformation("Cache STORED (ttl={Ttl}min, key={Key})",
            _opts.TtlMinutes, cacheKey);
    }

    // Chat scope gets its own key: answers derived from chat-private documents
    // must never be served to other users via the shared cache.
    private static string CacheKey(RetrievalScope scope) => scope.Mode switch
    {
        ScopeMode.Chat when scope.ChatId is not null
            => $"{scope.TenantId}:chat:{scope.ChatId}",
        ScopeMode.Private when scope.UserId is not null
            => $"{scope.TenantId}:private:{scope.UserId}",
        _   => $"{scope.TenantId}:shared",
    };

    // Process-wide guard — see QdrantVectorStore for rationale.
    private static volatile bool _collectionReady;
    private static readonly SemaphoreSlim InitLock = new(1, 1);

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        if (_collectionReady) return;

        await InitLock.WaitAsync(ct);
        try
        {
            if (_collectionReady) return;

            var existing = await _client.ListCollectionsAsync(ct);
            if (!existing.Contains(Collection))
            {
                await _client.CreateCollectionAsync(
                    Collection,
                    new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                    cancellationToken: ct);

                await _client.CreatePayloadIndexAsync(
                    Collection,
                    "cache_key",
                    PayloadSchemaType.Keyword,
                    cancellationToken: ct);
            }

            _collectionReady = true;
        }
        finally
        {
            InitLock.Release();
        }
    }
}
