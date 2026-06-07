namespace DocumentQA.Application.Abstractions.Ingestion;

public interface IChunkingStrategy
{
    IEnumerable<string> Chunk(string text);
}
