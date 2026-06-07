namespace DocumentQA.Application.Options;

public sealed class RagOptions
{
    public int ChunkSize { get; init; } = 500;
    public int ChunkOverlap { get; init; } = 100;
    public int RetrievalTopK { get; init; } = 10;
    public int RerankTopN { get; init; } = 5;
    public double MinRelevanceScore { get; init; } = 0.0;
}
