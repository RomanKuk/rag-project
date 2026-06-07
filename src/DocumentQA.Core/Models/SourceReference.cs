namespace DocumentQA.Core.Models;

public record SourceReference(
    string DocumentName,
    int Page,
    string Excerpt
);
