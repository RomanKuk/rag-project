using System.Runtime.CompilerServices;
using DocumentQA.Application.Abstractions;
using DocumentQA.Application.Abstractions.Ingestion;
using UglyToad.PdfPig;

namespace DocumentQA.Infrastructure.Parsing;

public sealed class PdfParser : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(
        Stream stream,
        string fileName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var doc = PdfDocument.Open(stream);
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            yield return new ParsedPage(page.Number, text);
        }
        await Task.CompletedTask;
    }
}
