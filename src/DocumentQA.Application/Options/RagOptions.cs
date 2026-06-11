namespace DocumentQA.Application.Options;

public sealed class RagOptions
{
    public int ChunkSize { get; init; } = 1500;
    public int ChunkOverlap { get; init; } = 200;
    public int RetrievalTopK { get; init; } = 20;
    public int RerankTopN { get; init; } = 10;
    public double MinRelevanceScore { get; init; } = 0.3;

    // Utility LLM for query expansion and reranking (cheap model to bound cost)
    public string UtilityModel { get; init; } = "gpt-4o-mini";

    // Query expansion via LLM before retrieval
    public bool QueryExpansionEnabled { get; init; } = true;

    // "identity" | "llm"
    public string RerankerStrategy { get; init; } = "llm";

    // "sliding" | "structural"
    public string ChunkingStrategy { get; init; } = "structural";

    // OCR fallback for near-empty PDF pages (requires tesseract + poppler in Docker image)
    public bool OcrEnabled { get; init; } = true;
    public int OcrMinWords { get; init; } = 40;

    // Token-aware context assembly
    public int MaxContextTokens { get; init; } = 6000;

    // History trimming (in turns = user+assistant pairs)
    public int MaxHistoryTurns { get; init; } = 6;

    // Model routing: cheap model for simple queries, strong for multi-hop/analytical
    public string SimpleModel  { get; init; } = "gpt-4o-mini";
    public string ComplexModel { get; init; } = "gpt-4o";

    // Multimodal table/image extraction (requires vision model — costly)
    public bool MultimodalEnabled { get; init; } = false;

    // Contextual chunk enrichment (Anthropic "contextual retrieval" style)
    public bool EnrichmentEnabled { get; init; } = false;

    // Configurable embedding model
    public string EmbeddingModel      { get; init; } = "text-embedding-3-small";
    public int    EmbeddingDimensions { get; init; } = 1536;
}
