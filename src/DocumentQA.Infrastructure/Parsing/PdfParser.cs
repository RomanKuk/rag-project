using System.Runtime.CompilerServices;
using DocumentQA.Application.Abstractions;
using DocumentQA.Application.Abstractions.Ingestion;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace DocumentQA.Infrastructure.Parsing;

public sealed class PdfParser(
    TesseractOcr ocr,
    IOptions<RagOptions> options,
    ILogger<PdfParser> logger) : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(
        Stream stream,
        string fileName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // PdfPig requires a seekable stream; copy if needed
        Stream seekable = stream.CanSeek
            ? stream
            : await CopyToTempAsync(stream, ct);

        // For OCR we also need a file path — write to a temp file once
        string? tempPdfPath = null;
        if (options.Value.OcrEnabled)
        {
            tempPdfPath = Path.Combine(Path.GetTempPath(), $"{Path.GetRandomFileName()}.pdf");
            await WriteTempPdfAsync(seekable, tempPdfPath, ct);
            seekable.Seek(0, SeekOrigin.Begin);
        }

        try
        {
            using var doc = PdfDocument.Open(seekable);
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var text = string.Join(" ", page.GetWords().Select(w => w.Text));
                var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

                if (wordCount < options.Value.OcrMinWords && options.Value.OcrEnabled && tempPdfPath is not null)
                {
                    logger.LogInformation(
                        "Page {Page} has {Words} words — attempting OCR", page.Number, wordCount);
                    try
                    {
                        var ocrText = await ocr.OcrPageAsync(tempPdfPath, page.Number, ct);
                        if (!string.IsNullOrWhiteSpace(ocrText))
                        {
                            logger.LogInformation("OCR page {Page}: {Words} words extracted",
                                page.Number, ocrText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                            text = ocrText;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "OCR failed for page {Page}, keeping text-layer result", page.Number);
                    }
                }

                yield return new ParsedPage(page.Number, text);
            }
        }
        finally
        {
            if (tempPdfPath is not null)
                try { File.Delete(tempPdfPath); } catch { /* best effort */ }
            if (!stream.CanSeek)
                await seekable.DisposeAsync();
        }
    }

    private static async Task<Stream> CopyToTempAsync(Stream source, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct);
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    private static async Task WriteTempPdfAsync(Stream source, string path, CancellationToken ct)
    {
        await using var fs = File.Create(path);
        await source.CopyToAsync(fs, ct);
    }
}
