using DocumentFormat.OpenXml.Packaging;
using DocumentQA.Core.Interfaces;

namespace DocumentQA.Ingestion.Parsers;

public class DocxParser : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart!.Document.Body!;
        var text = body.InnerText;
        yield return new ParsedPage(1, text, fileName);
        await Task.CompletedTask;
    }
}
