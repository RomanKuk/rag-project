using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Retrieval;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocumentQA.Infrastructure.VectorStores;

public sealed class QdrantVectorStore : IVectorStore
{
    private const string CollectionName = "documents";
    private const ulong VectorSize = 1536;

    private readonly QdrantClient _client;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task UpsertAsync(
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        string tenantId,
        CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var points = chunks.Zip(embeddings).Select(pair =>
        {
            var (chunk, vector) = pair;
            var point = new PointStruct
            {
                Id = new PointId { Uuid = chunk.Id },
                Vectors = vector
            };
            point.Payload["tenant_id"]    = tenantId;
            point.Payload["documentName"] = chunk.Metadata.DocumentName;
            point.Payload["page"]         = chunk.Metadata.Page;
            point.Payload["chunkIndex"]   = chunk.Metadata.ChunkIndex;
            point.Payload["content"]      = chunk.Content;
            if (chunk.Metadata.Section is { Length: > 0 } sec)
                point.Payload["section"] = sec;
            if (chunk.Metadata.DocumentType is { Length: > 0 } dt)
                point.Payload["documentType"] = dt;
            if (chunk.Metadata.DocumentDate is { Length: > 0 } dd)
                point.Payload["documentDate"] = dd;
            return point;
        }).ToList();

        await _client.UpsertAsync(CollectionName, points, cancellationToken: ct);
        _logger.LogInformation("Upserted {Count} points into '{Collection}' for tenant '{Tenant}'",
            points.Count, CollectionName, tenantId);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        double minScore,
        string tenantId,
        CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var results = await _client.SearchAsync(
            CollectionName,
            queryEmbedding,
            filter: TenantFilter(tenantId),
            limit: (ulong)topK,
            cancellationToken: ct);

        foreach (var r in results)
            _logger.LogInformation("  score={Score:F4} doc={Doc}", r.Score, GetString(r.Payload, "documentName"));

        var filtered = results
            .Where(r => r.Score >= (float)minScore && r.Payload.ContainsKey("content"))
            .Select(ToRetrievedChunk)
            .ToList();

        _logger.LogInformation("{Kept}/{Total} chunks passed minScore={MinScore} (tenant={Tenant})",
            filtered.Count, results.Count, minScore, tenantId);

        return filtered;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchHybridAsync(
        float[] dense,
        IReadOnlyList<string> keywords,
        int topK,
        double minScore,
        string tenantId,
        CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        // List A: dense semantic search with tenant + minScore filter
        var denseResults = await _client.SearchAsync(
            CollectionName,
            dense,
            filter: TenantFilter(tenantId),
            limit: (ulong)topK,
            cancellationToken: ct);

        var listA = denseResults
            .Where(r => r.Score >= (float)minScore && r.Payload.ContainsKey("content"))
            .ToList();

        _logger.LogInformation("Dense search: {Count} results above minScore={MinScore} (tenant={Tenant})",
            listA.Count, minScore, tenantId);

        // List B: per-keyword filtered dense search (tenant-scoped)
        var keywordHits = new List<ScoredPoint>();
        foreach (var kw in keywords.Where(k => k.Length > 2))
        {
            try
            {
                var filter = new Filter
                {
                    Must =
                    {
                        TenantCondition(tenantId),
                        new Condition
                        {
                            Field = new FieldCondition
                            {
                                Key   = "content",
                                Match = new Match { Text = kw }
                            }
                        }
                    }
                };

                var kwResults = await _client.SearchAsync(
                    CollectionName,
                    dense,
                    filter: filter,
                    limit: (ulong)topK,
                    cancellationToken: ct);

                foreach (var r in kwResults.Where(r => r.Payload.ContainsKey("content")))
                    keywordHits.Add(r);

                _logger.LogInformation("Keyword '{Kw}': {Count} hits", kw, kwResults.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Keyword search for '{Kw}' failed, skipping", kw);
            }
        }

        // Fuse lists A + B with Reciprocal Rank Fusion (k=60)
        const int rrfK = 60;
        var scores    = new Dictionary<string, double>();
        var pointById = new Dictionary<string, ScoredPoint>();

        void AddRankScores(IList<ScoredPoint> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var id = list[i].Id.Uuid;
                scores[id] = scores.GetValueOrDefault(id, 0.0) + 1.0 / (rrfK + i + 1);
                pointById.TryAdd(id, list[i]);
            }
        }

        AddRankScores(listA);
        AddRankScores(keywordHits);

        var fused = scores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => ToRetrievedChunk(pointById[kv.Key]))
            .ToList();

        _logger.LogInformation(
            "Hybrid search (dense={DenseCount} + keyword hits={KwCount}) → {FusedCount} after RRF (tenant={Tenant})",
            listA.Count, keywordHits.Count, fused.Count, tenantId);

        return fused;
    }

    public async Task<IReadOnlyList<string>> ListDocumentNamesAsync(string tenantId, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var names = new HashSet<string>();
        PointId? offset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                CollectionName,
                filter: TenantFilter(tenantId),
                limit: 250,
                offset: offset,
                payloadSelector: true,
                cancellationToken: ct);

            foreach (var point in response.Result)
            {
                if (point.Payload.TryGetValue("documentName", out var dn))
                    names.Add(dn.StringValue);
            }

            if (response.NextPageOffset is null)
                break;

            offset = response.NextPageOffset;
        }

        return names.Order().ToList();
    }

    public async Task DeleteDocumentAsync(string documentName, string tenantId, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var filter = new Filter
        {
            Must =
            {
                TenantCondition(tenantId),
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key   = "documentName",
                        Match = new Match { Keyword = documentName }
                    }
                }
            }
        };

        await _client.DeleteAsync(CollectionName, filter, cancellationToken: ct);
        _logger.LogInformation("Deleted document '{Name}' for tenant '{Tenant}'", documentName, tenantId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Condition TenantCondition(string tenantId) => new()
    {
        Field = new FieldCondition
        {
            Key   = "tenant_id",
            Match = new Match { Keyword = tenantId }
        }
    };

    private static Filter TenantFilter(string tenantId) => new()
    {
        Must = { TenantCondition(tenantId) }
    };

    private static RetrievedChunk ToRetrievedChunk(ScoredPoint r) =>
        new(
            new DocumentChunk
            {
                Id      = r.Id.Uuid,
                Content = GetString(r.Payload, "content"),
                Metadata = new ChunkMetadata
                {
                    DocumentName = GetString(r.Payload, "documentName"),
                    Page         = (int)(r.Payload.TryGetValue("page",       out var pg) ? pg.IntegerValue : 0),
                    ChunkIndex   = (int)(r.Payload.TryGetValue("chunkIndex", out var ci) ? ci.IntegerValue : 0),
                    Section      = r.Payload.TryGetValue("section",      out var sec) ? sec.StringValue : null,
                    DocumentType = r.Payload.TryGetValue("documentType", out var dt)  ? dt.StringValue  : null,
                    DocumentDate = r.Payload.TryGetValue("documentDate", out var dd)  ? dd.StringValue  : null,
                    TenantId     = r.Payload.TryGetValue("tenant_id",   out var tid) ? tid.StringValue  : "public",
                }
            },
            r.Score
        );

    private static string GetString(IDictionary<string, Value> payload, string key)
        => payload.TryGetValue(key, out var v) ? v.StringValue : string.Empty;

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var existing = await _client.ListCollectionsAsync(ct);
        if (!existing.Contains(CollectionName))
        {
            await _client.CreateCollectionAsync(
                CollectionName,
                new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
            _logger.LogInformation("Created collection '{Collection}'", CollectionName);

            // Full-text index for keyword filtering
            await _client.CreatePayloadIndexAsync(
                CollectionName,
                "content",
                PayloadSchemaType.Text,
                cancellationToken: ct);
            _logger.LogInformation("Created full-text index on 'content'");

            // Keyword index for fast tenant isolation
            await _client.CreatePayloadIndexAsync(
                CollectionName,
                "tenant_id",
                PayloadSchemaType.Keyword,
                cancellationToken: ct);
            _logger.LogInformation("Created keyword index on 'tenant_id'");
        }
    }
}
