namespace DocumentQA.Core.Models;

public record ChunkMetadata(
    string DocName,
    int Page,
    int ChunkIndex
);
