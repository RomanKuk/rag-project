namespace DocumentQA.Application.Abstractions.Ingestion;

public interface IDocumentParser
{
    bool CanHandle(string fileExtension);
    IAsyncEnumerable<ParsedPage> ParseAsync(Stream stream, string fileName, CancellationToken ct);
}
