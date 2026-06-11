using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Identity;
using DocumentQA.Domain.Retrieval;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocumentQA.Infrastructure.VectorStores;

public sealed class QdrantVectorStore : IVectorStore
{
    private const string CollectionName = "documents";
    private const ulong  VectorSize     = 1536;

    private readonly QdrantClient             _client;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(QdrantClient client, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task UpsertAsync(
        IReadOnlyList<DocumentChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        RetrievalScope scope,
        CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var points = chunks.Zip(embeddings).Select(pair =>
        {
            var (chunk, vector) = pair;
            var point = new PointStruct
            {
                Id      = new PointId { Uuid = chunk.Id },
                Vectors = vector
            };
            point.Payload["tenant_id"]     = scope.TenantId;
            point.Payload["visibility"]    = chunk.Metadata.Visibility.ToString().ToLowerInvariant();
            point.Payload["owner_user_id"] = chunk.Metadata.OwnerUserId ?? string.Empty;
            if (chunk.Metadata.ChatId.HasValue)
                point.Payload["chat_id"] = chunk.Metadata.ChatId.Value.ToString();
            if (chunk.Metadata.Language is { Length: > 0 } lang)
                point.Payload["language"] = lang;
            if (chunk.Metadata.ContextBlurb is { Length: > 0 } blurb)
                point.Payload["context_blurb"] = blurb;
            point.Payload["documentName"]  = chunk.Metadata.DocumentName;
            point.Payload["page"]          = chunk.Metadata.Page;
            point.Payload["chunkIndex"]    = chunk.Metadata.ChunkIndex;
            point.Payload["content"]       = chunk.Content;
            if (chunk.Metadata.Section is { Length: > 0 } sec)
                point.Payload["section"] = sec;
            if (chunk.Metadata.DocumentType is { Length: > 0 } dt)
                point.Payload["documentType"] = dt;
            if (chunk.Metadata.DocumentDate is { Length: > 0 } dd)
                point.Payload["documentDate"] = dd;
            return point;
        }).ToList();

        await _client.UpsertAsync(CollectionName, points, cancellationToken: ct);
        _logger.LogInformation("Upserted {Count} points into '{Collection}' for tenant '{Tenant}' visibility={Vis}",
            points.Count, CollectionName, scope.TenantId, scope.Mode);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding, int topK, double minScore,
        RetrievalScope scope, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var results = await _client.SearchAsync(
            CollectionName,
            queryEmbedding,
            filter: ScopeFilter(scope),
            limit: (ulong)topK,
            cancellationToken: ct);

        foreach (var r in results)
            _logger.LogInformation("  score={Score:F4} doc={Doc}", r.Score, GetString(r.Payload, "documentName"));

        var filtered = results
            .Where(r => r.Score >= (float)minScore && r.Payload.ContainsKey("content"))
            .Select(ToRetrievedChunk)
            .ToList();

        _logger.LogInformation("{Kept}/{Total} chunks passed minScore={MinScore} (tenant={Tenant} mode={Mode})",
            filtered.Count, results.Count, minScore, scope.TenantId, scope.Mode);

        return filtered;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchHybridAsync(
        float[] dense, IReadOnlyList<string> keywords, int topK, double minScore,
        RetrievalScope scope, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var denseResults = await _client.SearchAsync(
            CollectionName,
            dense,
            filter: ScopeFilter(scope),
            limit: (ulong)topK,
            cancellationToken: ct);

        var listA = denseResults
            .Where(r => r.Score >= (float)minScore && r.Payload.ContainsKey("content"))
            .ToList();

        var keywordHits = new List<ScoredPoint>();
        foreach (var kw in keywords.Where(k => k.Length > 2))
        {
            try
            {
                var baseConditions = ScopeConditions(scope);
                baseConditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key   = "content",
                        Match = new Match { Text = kw }
                    }
                });
                var filter = new Filter();
                foreach (var c in baseConditions) filter.Must.Add(c);

                var kwResults = await _client.SearchAsync(
                    CollectionName, dense, filter: filter,
                    limit: (ulong)topK, cancellationToken: ct);

                foreach (var r in kwResults.Where(r => r.Payload.ContainsKey("content")))
                    keywordHits.Add(r);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Keyword search for '{Kw}' failed, skipping", kw);
            }
        }

        const int rrfK     = 60;
        var scores          = new Dictionary<string, double>();
        var pointById       = new Dictionary<string, ScoredPoint>();

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
            "Hybrid search (dense={D} + kw={K}) → {F} after RRF (tenant={T} mode={M})",
            listA.Count, keywordHits.Count, fused.Count, scope.TenantId, scope.Mode);

        return fused;
    }

    public async Task<IReadOnlyList<string>> ListDocumentNamesAsync(RetrievalScope scope, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var names  = new HashSet<string>();
        PointId? offset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                CollectionName,
                filter: ScopeFilter(scope),
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

    public async Task DeleteDocumentAsync(string documentName, RetrievalScope scope, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var conditions = ScopeConditions(scope);
        conditions.Add(new Condition
        {
            Field = new FieldCondition
            {
                Key   = "documentName",
                Match = new Match { Keyword = documentName }
            }
        });
        var filter = new Filter();
        foreach (var c in conditions) filter.Must.Add(c);

        await _client.DeleteAsync(CollectionName, filter, cancellationToken: ct);
        _logger.LogInformation("Deleted document '{Name}' for tenant '{Tenant}'", documentName, scope.TenantId);
    }

    public async Task DeleteByChatAsync(Guid chatId, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        var filter = new Filter
        {
            Must =
            {
                new Condition { Field = new FieldCondition { Key = "chat_id", Match = new Match { Keyword = chatId.ToString() } } }
            }
        };
        await _client.DeleteAsync(CollectionName, filter, cancellationToken: ct);
        _logger.LogInformation("Deleted chat-scoped vectors for chatId='{ChatId}'", chatId);
    }

    public async Task<long> CountDocumentsAsync(string tenantId, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        var names  = new HashSet<string>();
        PointId? offset = null;

        while (true)
        {
            var response = await _client.ScrollAsync(
                CollectionName,
                filter: TenantOnlyFilter(tenantId),
                limit: 250,
                offset: offset,
                payloadSelector: true,
                cancellationToken: ct);

            foreach (var point in response.Result)
            {
                if (point.Payload.TryGetValue("documentName", out var dn))
                    names.Add(dn.StringValue);
            }

            if (response.NextPageOffset is null) break;
            offset = response.NextPageOffset;
        }

        return names.Count;
    }

    public async Task<long> CountChunksAsync(string tenantId, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        var result = await _client.CountAsync(
            CollectionName,
            filter: TenantOnlyFilter(tenantId),
            cancellationToken: ct);
        return (long)result;
    }

    // ── Scope helpers ────────────────────────────────────────────────────────

    private static List<Condition> ScopeConditions(RetrievalScope scope)
    {
        var conditions = new List<Condition>
        {
            new() { Field = new FieldCondition { Key = "tenant_id", Match = new Match { Keyword = scope.TenantId } } }
        };

        if (scope.Mode == ScopeMode.Shared)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition { Key = "visibility", Match = new Match { Keyword = "shared" } }
            });
        }
        else if (scope.Mode == ScopeMode.Private && scope.UserId is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition { Key = "visibility", Match = new Match { Keyword = "private" } }
            });
            conditions.Add(new Condition
            {
                Field = new FieldCondition { Key = "owner_user_id", Match = new Match { Keyword = scope.UserId } }
            });
        }

        return conditions;
    }

    private static Filter ScopeFilter(RetrievalScope scope)
    {
        if (scope.Mode == ScopeMode.Chat && scope.ChatId.HasValue)
        {
            // Chat scope: OR(chat-specific docs, optionally shared tenant docs)
            var chatFilter = new Filter();
            chatFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "tenant_id", Match = new Match { Keyword = scope.TenantId } } });
            chatFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "chat_id",   Match = new Match { Keyword = scope.ChatId.Value.ToString() } } });

            var outerFilter = new Filter();
            outerFilter.Should.Add(new Condition { Filter = chatFilter });

            if (scope.IncludeSharedDocs)
            {
                var sharedFilter = new Filter();
                sharedFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "tenant_id",  Match = new Match { Keyword = scope.TenantId } } });
                sharedFilter.Must.Add(new Condition { Field = new FieldCondition { Key = "visibility", Match = new Match { Keyword = "shared" } } });
                outerFilter.Should.Add(new Condition { Filter = sharedFilter });
            }

            return outerFilter;
        }

        var filter = new Filter();
        foreach (var c in ScopeConditions(scope))
            filter.Must.Add(c);
        return filter;
    }

    private static Filter TenantOnlyFilter(string tenantId) => new()
    {
        Must =
        {
            new Condition { Field = new FieldCondition { Key = "tenant_id", Match = new Match { Keyword = tenantId } } }
        }
    };

    // ── Mapping ──────────────────────────────────────────────────────────────

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
                    Visibility   = r.Payload.TryGetValue("visibility",  out var vis) && vis.StringValue == "private"
                        ? DocumentVisibility.Private : DocumentVisibility.Shared,
                    OwnerUserId  = r.Payload.TryGetValue("owner_user_id", out var uid)  ? uid.StringValue  : null,
                    ChatId       = r.Payload.TryGetValue("chat_id",       out var cid)  && Guid.TryParse(cid.StringValue, out var cidGuid) ? cidGuid : null,
                    Language     = r.Payload.TryGetValue("language",      out var lng)  ? lng.StringValue  : null,
                    ContextBlurb = r.Payload.TryGetValue("context_blurb", out var cb)   ? cb.StringValue   : null,
                }
            },
            r.Score
        );

    private static string GetString(IDictionary<string, Value> payload, string key)
        => payload.TryGetValue(key, out var v) ? v.StringValue : string.Empty;

    // ── Collection bootstrap ─────────────────────────────────────────────────

    // Process-wide guard: the store is registered Scoped, but collection existence
    // is process-global — check Qdrant once per process lifetime, not per request.
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
            if (!existing.Contains(CollectionName))
            {
                await _client.CreateCollectionAsync(
                    CollectionName,
                    new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                    cancellationToken: ct);
                _logger.LogInformation("Created collection '{Collection}'", CollectionName);

                await _client.CreatePayloadIndexAsync(CollectionName, "content",       PayloadSchemaType.Text,    cancellationToken: ct);
                await _client.CreatePayloadIndexAsync(CollectionName, "tenant_id",     PayloadSchemaType.Keyword, cancellationToken: ct);
                await _client.CreatePayloadIndexAsync(CollectionName, "visibility",    PayloadSchemaType.Keyword, cancellationToken: ct);
                await _client.CreatePayloadIndexAsync(CollectionName, "owner_user_id", PayloadSchemaType.Keyword, cancellationToken: ct);
                await _client.CreatePayloadIndexAsync(CollectionName, "chat_id",       PayloadSchemaType.Keyword, cancellationToken: ct);
                _logger.LogInformation("Created payload indexes on collection '{Collection}'", CollectionName);
            }

            _collectionReady = true;
        }
        finally
        {
            InitLock.Release();
        }
    }
}
