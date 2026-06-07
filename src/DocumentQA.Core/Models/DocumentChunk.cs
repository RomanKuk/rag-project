namespace DocumentQA.Core.Models;

public record DocumentChunk
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string DocumentName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int PageNumber { get; init; }
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = "";
    public float[] Embedding { get; init; } = [];
    public DateTime IngestedAt { get; init; } = DateTime.UtcNow;
}
