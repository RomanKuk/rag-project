using DocumentQA.Core.Interfaces;

namespace DocumentQA.Ingestion.Parsers;

public class TxtParser : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName)
    {
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        yield return new ParsedPage(1, text, fileName);
    }
}
