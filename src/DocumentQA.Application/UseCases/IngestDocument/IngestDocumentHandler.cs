using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Domain.Common;
using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Identity;

namespace DocumentQA.Application.UseCases.IngestDocument;

public sealed class IngestDocumentHandler
{
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IChunkingStrategy _chunker;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;

    // Derives a document date like "2024-04" from filename patterns
    private static readonly Regex DateInFilename = new(
        @"(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|" +
        @"jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)_?" +
        @"(\d{4})|(\d{4})[-_]?(0[1-9]|1[0-2])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, int> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["feb"] = 2, ["february"] = 2,
        ["mar"] = 3, ["march"] = 3, ["apr"] = 4, ["april"] = 4,
        ["may"] = 5, ["jun"] = 6, ["june"] = 6, ["jul"] = 7, ["july"] = 7,
        ["aug"] = 8, ["august"] = 8, ["sep"] = 9, ["september"] = 9,
        ["oct"] = 10, ["october"] = 10, ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

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

    public async Task<Result<int>> HandleAsync(
        Stream file, string fileName, RetrievalScope scope, CancellationToken ct)
    {
        var ext = Path.GetExtension(fileName);
        var parser = _parsers.FirstOrDefault(p => p.CanHandle(ext));
        if (parser is null)
            return Result<int>.Failure($"Unsupported file type: {ext}");

        var docType = InferDocumentType(fileName);
        var docDate = InferDocumentDate(fileName);

        var chunks = new List<DocumentChunk>();
        await foreach (var page in parser.ParseAsync(file, fileName, ct))
        {
            var wordCount = page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount < 40) continue;

            var pieces = _chunker.Chunk(page.Text).ToList();
            chunks.AddRange(pieces.Select((piece, idx) => new DocumentChunk
            {
                Id = Guid.NewGuid().ToString(),
                Content = piece.Text,
                Metadata = new ChunkMetadata
                {
                    DocumentName = fileName,
                    Page         = page.PageNumber,
                    ChunkIndex   = idx,
                    Section      = piece.Heading,
                    DocumentType = docType,
                    DocumentDate = docDate,
                    TenantId     = scope.TenantId,
                    Visibility   = scope.Mode == ScopeMode.Private
                        ? DocumentVisibility.Private : DocumentVisibility.Shared,
                    OwnerUserId  = scope.UserId,
                }
            }));
        }

        if (chunks.Count == 0)
            return Result<int>.Failure("No text could be extracted from the document.");

        var embeddings = await _embedding.EmbedBatchAsync(
            chunks.Select(c => c.Content).ToList(), ct);

        await _vectorStore.UpsertAsync(chunks, embeddings, scope, ct);

        return Result<int>.Success(chunks.Count);
    }

    private static string? InferDocumentType(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.Contains("board_packet") || lower.Contains("board packet")) return "board_packet";
        if (lower.Contains("minutes")) return "meeting_minutes";
        if (lower.Contains("agenda")) return "agenda";
        if (lower.Contains("policy") || lower.Contains("policies")) return "policy";
        if (lower.Contains("resolution")) return "resolution";
        if (lower.Contains("agreement") || lower.Contains("contract")) return "agreement";
        if (lower.Contains("report")) return "report";
        return null;
    }

    private static string? InferDocumentDate(string fileName)
    {
        var m = DateInFilename.Match(Path.GetFileNameWithoutExtension(fileName));
        if (!m.Success) return null;

        if (m.Groups[2].Success && m.Groups[3].Success)
            return $"{m.Groups[2].Value}-{m.Groups[3].Value}";

        // Month-name form
        var namePart = m.Value.Split(new[] { '_', '-', ' ' })[0];
        if (MonthMap.TryGetValue(namePart, out var month) && m.Groups[1].Success)
            return $"{m.Groups[1].Value}-{month:D2}";

        return null;
    }
}
