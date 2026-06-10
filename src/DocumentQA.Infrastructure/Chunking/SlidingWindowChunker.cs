using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Chunking;

public sealed class SlidingWindowChunker : IChunkingStrategy
{
    private readonly RagOptions _options;

    public SlidingWindowChunker(IOptions<RagOptions> options)
        => _options = options.Value;

    public IEnumerable<ChunkPiece> Chunk(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        var step = _options.ChunkSize - _options.ChunkOverlap;
        for (int i = 0; i < words.Length; i += step)
        {
            yield return new ChunkPiece(string.Join(" ", words.Skip(i).Take(_options.ChunkSize)));
            if (i + _options.ChunkSize >= words.Length) break;
        }
    }
}
