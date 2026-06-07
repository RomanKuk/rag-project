using DocumentQA.Application.Abstractions.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentQA.Infrastructure.Embeddings;

public sealed class OpenAIEmbeddingAdapter : IEmbeddingPort
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public OpenAIEmbeddingAdapter(
        [FromKeyedServices("embeddings")] IEmbeddingGenerator<string, Embedding<float>> generator)
        => _generator = generator;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var results = await _generator.GenerateAsync([text], cancellationToken: ct);
        return results[0].Vector.ToArray();
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        var results = await _generator.GenerateAsync(texts, cancellationToken: ct);
        return results.Select(e => e.Vector.ToArray()).ToList();
    }
}
