namespace DocumentQA.Application.Abstractions.Retrieval;

public interface IEmbeddingPort
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
