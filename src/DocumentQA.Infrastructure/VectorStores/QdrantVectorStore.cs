using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Retrieval;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace DocumentQA.Infrastructure.VectorStores;

public sealed class QdrantVectorStore : IVectorStore
{
    private const string CollectionName = "documents";
    private const ulong VectorSize = 1536;

    private readonly QdrantClient _client;

    public QdrantVectorStore(QdrantClient client) => _client = client;

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
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        double minScore,
        CancellationToken ct)
    {
        var results = await _client.SearchAsync(
            CollectionName,
            queryEmbedding,
            limit: (ulong)topK,
            scoreThreshold: (float)minScore,
            cancellationToken: ct);

        return results.Select(r => new RetrievedChunk(
            new DocumentChunk
            {
                Id = r.Id.Uuid,
                Content = r.Payload["content"].StringValue,
                Metadata = new ChunkMetadata
                {
                    DocumentName = r.Payload["documentName"].StringValue,
                    Page = (int)r.Payload["page"].IntegerValue,
                    ChunkIndex = (int)r.Payload["chunkIndex"].IntegerValue
                }
            },
            r.Score
        )).ToList();
    }

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var existing = await _client.ListCollectionsAsync(ct);
        if (!existing.Contains(CollectionName))
        {
            await _client.CreateCollectionAsync(
                CollectionName,
                new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }
}
