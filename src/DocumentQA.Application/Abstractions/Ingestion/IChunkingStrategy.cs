namespace DocumentQA.Application.Abstractions.Ingestion;

public sealed record ChunkPiece(string Text, string? Heading = null);

public interface IChunkingStrategy
{
    IEnumerable<ChunkPiece> Chunk(string text);
}
