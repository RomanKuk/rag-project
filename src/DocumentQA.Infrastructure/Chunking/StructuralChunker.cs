using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Chunking;

/// <summary>
/// Splits text on structural heading boundaries before applying sliding-window sub-splitting.
/// Keeps resolutions, policy sections, and agenda items intact as standalone chunks.
/// </summary>
public sealed class StructuralChunker(IOptions<RagOptions> options) : IChunkingStrategy
{
    // Matches common section headings in board packets and authority documents
    private static readonly Regex HeadingPattern = new(
        @"(?m)^(?:" +
        @"RESOLUTION\s+NO\.?\s*[\d/\-]+" +       // RESOLUTION NO. 11/25-26
        @"|ATTACHMENT\s+\d+" +                    // ATTACHMENT 7
        @"|[A-Z][A-Z\s]{4,}(?:\:|$)" +           // ALL CAPS HEADING:
        @"|\d+\.\s+[A-Z][A-Za-z]" +              // 1. Numbered agenda item
        @"|(?:ARTICLE|SECTION|PART)\s+[IVX\d]+" + // ARTICLE IV
        @")",
        RegexOptions.Compiled);

    private readonly SlidingWindowChunker _splitter = new(options);

    public IEnumerable<ChunkPiece> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        // Split text into sections on heading boundaries
        var sections = SplitOnHeadings(text);

        foreach (var (heading, body) in sections)
        {
            var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;

            if (words.Length <= options.Value.ChunkSize)
            {
                // Section fits in a single chunk
                yield return new ChunkPiece(
                    heading is not null ? $"{heading}\n{body}" : body,
                    heading);
            }
            else
            {
                // Sub-split large sections with sliding window
                foreach (var piece in _splitter.Chunk(body))
                    yield return piece with { Heading = heading };
            }
        }
    }

    private static IEnumerable<(string? Heading, string Body)> SplitOnHeadings(string text)
    {
        var matches = HeadingPattern.Matches(text);
        if (matches.Count == 0)
        {
            yield return (null, text);
            yield break;
        }

        // Text before the first heading
        if (matches[0].Index > 0)
        {
            var preamble = text[..matches[0].Index].Trim();
            if (preamble.Length > 0)
                yield return (null, preamble);
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var headingText = matches[i].Value.Trim();
            var bodyStart   = matches[i].Index + matches[i].Length;
            var bodyEnd     = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body        = text[bodyStart..bodyEnd].Trim();

            if (body.Length > 0)
                yield return (headingText, body);
        }
    }
}
