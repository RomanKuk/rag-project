using DocumentQA.Domain.Identity;

namespace DocumentQA.Domain.Documents;

public sealed record ChunkMetadata
{
    public required string DocumentName { get; init; }
    public required int Page { get; init; }
    public required int ChunkIndex { get; init; }
    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Source { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Section { get; init; }
    public string? DocumentType { get; init; }
    public string? DocumentDate { get; init; }
    public string TenantId { get; init; } = "public";
    public DocumentVisibility Visibility { get; init; } = DocumentVisibility.Shared;
    public string? OwnerUserId  { get; init; }
    public Guid?   ChatId       { get; init; }

    // Enrichment fields (B3)
    public string?   Language        { get; init; }
    public DateTime? SourceCreatedAt { get; init; }
    public IReadOnlyList<string> RoleTags { get; init; } = [];
    public string?   ContextBlurb    { get; init; } // contextual retrieval prefix
}
