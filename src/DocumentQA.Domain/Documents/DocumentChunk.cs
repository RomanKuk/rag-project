namespace DocumentQA.Domain.Documents;

public sealed record DocumentChunk
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required ChunkMetadata Metadata { get; init; }
}
