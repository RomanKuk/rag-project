using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml.Packaging;
using DocumentQA.Application.Abstractions;
using DocumentQA.Application.Abstractions.Ingestion;

namespace DocumentQA.Infrastructure.Parsing;

public sealed class DocxParser : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(
        Stream stream,
        string fileName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var text = doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
        yield return new ParsedPage(1, text);
        await Task.CompletedTask;
    }
}
