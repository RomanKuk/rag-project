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
            point.Payload["documentName"] = chunk.Metadata.DocumentName;
            point.Payload["page"] = chunk.Metadata.Page;
            point.Payload["chunkIndex"] = chunk.Metadata.ChunkIndex;
            point.Payload["content"] = chunk.Content;
            return point;
        }).ToList();

        await _client.UpsertAsync(CollectionName, points, cancellationToken: ct);
        _logger.LogInformation("Upserted {Count} points into '{Collection}'", points.Count, CollectionName);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        double minScore,
        CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);

        // Search without score threshold so we can log actual scores for calibration
        var results = await _client.SearchAsync(
            CollectionName,
            queryEmbedding,
            limit: (ulong)topK,
            cancellationToken: ct);

        foreach (var r in results)
            _logger.LogInformation("  score={Score:F4} doc={Doc}", r.Score, GetString(r.Payload, "documentName"));

        var filtered = results
            .Where(r => r.Score >= (float)minScore && r.Payload.ContainsKey("content"))
            .Select(r => new RetrievedChunk(
                new DocumentChunk
                {
                    Id = r.Id.Uuid,
                    Content = GetString(r.Payload, "content"),
                    Metadata = new ChunkMetadata
                    {
                        DocumentName = GetString(r.Payload, "documentName"),
                        Page = (int)(r.Payload.TryGetValue("page", out var pg) ? pg.IntegerValue : 0),
                        ChunkIndex = (int)(r.Payload.TryGetValue("chunkIndex", out var ci) ? ci.IntegerValue : 0)
                    }
                },
                r.Score
            )).ToList();

        _logger.LogInformation("{Kept}/{Total} chunks passed minScore={MinScore}",
            filtered.Count, results.Count, minScore);

        return filtered;
    }

    private static string GetString(IDictionary<string, Qdrant.Client.Grpc.Value> payload, string key)
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
        }
    }
}
