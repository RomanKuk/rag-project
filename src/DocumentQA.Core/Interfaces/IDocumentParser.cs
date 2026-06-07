namespace DocumentQA.Core.Interfaces;

public interface IDocumentParser
{
    bool CanHandle(string fileExtension);
    IAsyncEnumerable<ParsedPage> ParseAsync(Stream fileStream, string fileName);
}
