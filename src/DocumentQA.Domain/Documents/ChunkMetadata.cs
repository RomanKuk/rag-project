namespace DocumentQA.Domain.Documents;

public sealed record ChunkMetadata
{
    public required string DocumentName { get; init; }
    public required int Page { get; init; }
    public required int ChunkIndex { get; init; }
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Source { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    // Structural metadata — populated by StructuralChunker / IngestDocumentHandler
    public string? Section { get; init; }
    public string? DocumentType { get; init; }
    public string? DocumentDate { get; init; }
    // Tenant isolation — scopes documents to the owning tenant
    public string TenantId { get; init; } = "public";
}
