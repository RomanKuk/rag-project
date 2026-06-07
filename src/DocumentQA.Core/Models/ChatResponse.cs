namespace DocumentQA.Core.Models;

public record ChatResponse(
    string Answer,
    List<SourceReference> Sources
);
