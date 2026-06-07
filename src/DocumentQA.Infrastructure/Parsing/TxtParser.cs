using System.Runtime.CompilerServices;
using DocumentQA.Application.Abstractions;
using DocumentQA.Application.Abstractions.Ingestion;

namespace DocumentQA.Infrastructure.Parsing;

public sealed class TxtParser : IDocumentParser
{
    public bool CanHandle(string fileExtension) =>
        fileExtension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
        fileExtension.Equals(".md", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedPage> ParseAsync(
        Stream stream,
        string fileName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(ct);
        yield return new ParsedPage(1, text);
    }
}
