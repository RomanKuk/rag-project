using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Domain.Common;
using DocumentQA.Domain.Documents;

namespace DocumentQA.Application.UseCases.IngestDocument;

public sealed class IngestDocumentHandler
{
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IChunkingStrategy _chunker;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;

    public IngestDocumentHandler(
        IEnumerable<IDocumentParser> parsers,
        IChunkingStrategy chunker,
        IEmbeddingPort embedding,
        IVectorStore vectorStore)
    {
        _parsers = parsers;
        _chunker = chunker;
        _embedding = embedding;
        _vectorStore = vectorStore;
    }

    public async Task<Result<int>> HandleAsync(Stream file, string fileName, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(ext));
        if (parser is null)
            return Result<int>.Failure($"Unsupported file type: {ext}");

        var chunks = new List<DocumentChunk>();
        await foreach (var page in parser.ParseAsync(file, fileName, ct))
        {
            // Skip near-empty pages (images, scanned attachments, signature blocks).
            // These pages produce meaningless tiny chunks that contaminate search results.
            var wordCount = page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount < 40) continue;

            var pieces = _chunker.Chunk(page.Text).ToList();
            chunks.AddRange(pieces.Select((text, idx) => new DocumentChunk
            {
                Id = Guid.NewGuid().ToString(),
                Content = text,
                Metadata = new ChunkMetadata
                {
                    DocumentName = fileName,
                    Page = page.PageNumber,
                    ChunkIndex = idx
                }
            }));
        }

        if (chunks.Count == 0)
            return Result<int>.Failure("No text could be extracted from the document.");

        var embeddings = await _embedding.EmbedBatchAsync(
            chunks.Select(c => c.Content).ToList(), ct);

        await _vectorStore.UpsertAsync(chunks, embeddings, ct);

        return Result<int>.Success(chunks.Count);
    }
}
