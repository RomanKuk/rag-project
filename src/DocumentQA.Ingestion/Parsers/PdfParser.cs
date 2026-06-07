using DocumentQA.Core.Interfaces;
using UglyToad.PdfPig;

namespace DocumentQA.Ingestion.Parsers;

public class PdfParser : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName)
    {
        using var doc = PdfDocument.Open(stream);
        foreach (var page in doc.GetPages())
        {
            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            yield return new ParsedPage(page.Number, text, fileName);
        }
        await Task.CompletedTask;
    }
}
