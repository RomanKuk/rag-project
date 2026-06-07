using DocumentQA.Application.Abstractions.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.Embeddings;

namespace DocumentQA.Infrastructure.Embeddings;

public sealed class OpenAIEmbeddingAdapter : IEmbeddingPort
{
    private readonly ITextEmbeddingGenerationService _sk;

    public OpenAIEmbeddingAdapter(
        [FromKeyedServices("embeddings")] ITextEmbeddingGenerationService sk)
        => _sk = sk;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        => (await _sk.GenerateEmbeddingAsync(text, cancellationToken: ct)).ToArray();

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
        => (await _sk.GenerateEmbeddingsAsync(texts.ToList(), cancellationToken: ct))
            .Select(e => e.ToArray())
            .ToList();
}
