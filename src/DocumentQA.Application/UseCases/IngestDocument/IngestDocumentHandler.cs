using System.Text;
using System.Text.RegularExpressions;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.Options;
using DocumentQA.Domain.Common;
using DocumentQA.Domain.Documents;
using DocumentQA.Domain.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Application.UseCases.IngestDocument;

public sealed class IngestDocumentHandler
{
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly IChunkingStrategy _chunker;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;
    private readonly ICompletionPort _completion;
    private readonly RagOptions _options;
    private readonly ILogger<IngestDocumentHandler> _logger;

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
        IVectorStore vectorStore,
        ICompletionPort completion,
        IOptions<RagOptions> options,
        ILogger<IngestDocumentHandler> logger)
    {
        _parsers    = parsers;
        _chunker    = chunker;
        _embedding  = embedding;
        _vectorStore = vectorStore;
        _completion = completion;
        _options    = options.Value;
        _logger     = logger;
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

        // Collect all page text first (for language detection and doc-level context)
        var pageTexts = new List<(int PageNumber, string Text, string? Heading)>();
        await foreach (var page in parser.ParseAsync(file, fileName, ct))
        {
            var wordCount = page.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount < 40) continue;
            pageTexts.Add((page.PageNumber, page.Text, null));
        }

        if (pageTexts.Count == 0)
            return Result<int>.Failure("No text could be extracted from the document.");

        // Detect document language from first 500 chars of first page
        var sampleText = pageTexts[0].Text[..Math.Min(500, pageTexts[0].Text.Length)];
        var language   = DetectLanguage(sampleText);

        // Build doc summary for contextual enrichment (first 600 chars)
        var docSample = string.Join(" ", pageTexts.Take(3).Select(p => p.Text))
            [..Math.Min(600, pageTexts.Take(3).Sum(p => p.Text.Length))];

        var chunks = new List<DocumentChunk>();
        foreach (var (pageNumber, text, heading) in pageTexts)
        {
            var pieces = _chunker.Chunk(text).ToList();
            chunks.AddRange(pieces.Select((piece, idx) => new DocumentChunk
            {
                Id      = Guid.NewGuid().ToString(),
                Content = piece.Text,
                Metadata = new ChunkMetadata
                {
                    DocumentName = fileName,
                    Page         = pageNumber,
                    ChunkIndex   = idx,
                    Section      = piece.Heading ?? heading,
                    DocumentType = docType,
                    DocumentDate = docDate,
                    TenantId     = scope.TenantId,
                    Visibility   = scope.Mode is ScopeMode.Private or ScopeMode.Chat
                        ? DocumentVisibility.Private : DocumentVisibility.Shared,
                    OwnerUserId  = scope.UserId,
                    ChatId       = scope.ChatId,
                    Language     = language,
                }
            }));
        }

        // Contextual chunk enrichment: generate blurb per chunk (config-gated, expensive)
        if (_options.EnrichmentEnabled)
            await EnrichChunksAsync(chunks, fileName, docSample, ct);

        // Embed: use enriched content (blurb + chunk) when enrichment is on
        var contents   = chunks.Select(c =>
            c.Metadata.ContextBlurb is { Length: > 0 } blurb
                ? $"{blurb}\n\n{c.Content}"
                : c.Content).ToList();
        var embeddings = await _embedding.EmbedBatchAsync(contents, ct);

        await _vectorStore.UpsertAsync(chunks, embeddings, scope, ct);

        _logger.LogInformation(
            "Ingested {Count} chunks from '{File}' (lang={Lang}, enriched={Enriched})",
            chunks.Count, fileName, language, _options.EnrichmentEnabled);

        return Result<int>.Success(chunks.Count);
    }

    private async Task EnrichChunksAsync(
        List<DocumentChunk> chunks, string fileName, string docSample, CancellationToken ct)
    {
        const string EnrichSystemPrompt = """
            You are a retrieval enrichment assistant.
            Given a document excerpt and a specific chunk from that document,
            write a short (1-2 sentence) context blurb that situates the chunk within the document.
            The blurb will be prepended to the chunk before embedding to improve retrieval.
            Return ONLY the blurb text — no labels, no quotes.
            """;

        var tasks = chunks.Select((chunk, i) => (i, task: Task.Run(async () =>
        {
            try
            {
                var userMsg = $"Document: {fileName}\nDocument excerpt: {docSample}\n\nChunk:\n{chunk.Content[..Math.Min(400, chunk.Content.Length)]}";
                var blurb = await _completion.CompleteAsync(
                    EnrichSystemPrompt, userMsg, _options.UtilityModel, ct);
                return blurb.Trim();
            }
            catch
            {
                return (string?)null;
            }
        }, ct)));

        foreach (var (i, task) in tasks)
        {
            var blurb = await task;
            if (blurb is not null)
                chunks[i] = chunks[i] with { Metadata = chunks[i].Metadata with { ContextBlurb = blurb } };
        }
    }

    private static string? DetectLanguage(string sample)
    {
        if (string.IsNullOrWhiteSpace(sample)) return null;

        var cyrillicCount = sample.Count(c => c >= 'Ѐ' && c <= 'ӿ');
        var arabicCount   = sample.Count(c => c >= '؀' && c <= 'ۿ');
        var cjkCount      = sample.Count(c => c >= '一' && c <= '鿿');
        var total         = sample.Length;

        if (cyrillicCount / (double)total > 0.15) return "uk"; // Ukrainian/Russian
        if (arabicCount   / (double)total > 0.10) return "ar";
        if (cjkCount      / (double)total > 0.10) return "zh";

        return "en"; // default
    }

    private static string? InferDocumentType(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        if (lower.Contains("board_packet") || lower.Contains("board packet")) return "board_packet";
        if (lower.Contains("minutes")) return "meeting_minutes";
        if (lower.Contains("agenda"))  return "agenda";
        if (lower.Contains("policy") || lower.Contains("policies")) return "policy";
        if (lower.Contains("resolution")) return "resolution";
        if (lower.Contains("agreement") || lower.Contains("contract")) return "agreement";
        if (lower.Contains("report"))  return "report";
        return null;
    }

    private static string? InferDocumentDate(string fileName)
    {
        var m = DateInFilename.Match(Path.GetFileNameWithoutExtension(fileName));
        if (!m.Success) return null;

        if (m.Groups[2].Success && m.Groups[3].Success)
            return $"{m.Groups[2].Value}-{m.Groups[3].Value}";

        var namePart = m.Value.Split(['_', '-', ' '])[0];
        if (MonthMap.TryGetValue(namePart, out var month) && m.Groups[1].Success)
            return $"{m.Groups[1].Value}-{month:D2}";

        return null;
    }
}
