using System.Text.Json;
using DocumentQA.Core.Interfaces;
using DocumentQA.Core.Models;
using Microsoft.SemanticKernel.Memory;

namespace DocumentQA.Ingestion;

public class IngestionService
{
    private const string Collection = "documents";

    private readonly ISemanticTextMemory _memory;
    private readonly SlidingWindowChunker _chunker;
    private readonly IEnumerable<IDocumentParser> _parsers;

    public IngestionService(
        ISemanticTextMemory memory,
        SlidingWindowChunker chunker,
        IEnumerable<IDocumentParser> parsers)
    {
        _memory = memory;
        _chunker = chunker;
        _parsers = parsers;
    }

    public async Task IngestAsync(Stream fileStream, string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(ext))
            ?? throw new NotSupportedException($"No parser registered for '{ext}'.");

        await foreach (var page in parser.ParseAsync(fileStream, fileName))
        {
            var chunks = _chunker.Chunk(page.Text).ToList();
            for (int idx = 0; idx < chunks.Count; idx++)
            {
                var id = $"{fileName}-p{page.PageNumber}-c{idx}";
                var meta = JsonSerializer.Serialize(new ChunkMetadata(fileName, page.PageNumber, idx));

                await _memory.SaveInformationAsync(
                    collection: Collection,
                    text: chunks[idx],
                    id: id,
                    additionalMetadata: meta);
            }
        }
    }
}
