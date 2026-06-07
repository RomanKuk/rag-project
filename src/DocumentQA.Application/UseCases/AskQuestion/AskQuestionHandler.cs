using System.Runtime.CompilerServices;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Options;

namespace DocumentQA.Application.UseCases.AskQuestion;

public sealed class AskQuestionHandler
{
    private readonly IQueryProcessor _queryProcessor;
    private readonly IEmbeddingPort _embedding;
    private readonly IVectorStore _vectorStore;
    private readonly IReranker _reranker;
    private readonly IPromptBuilder _promptBuilder;
    private readonly IChatCompletionPort _chat;
    private readonly RagOptions _options;

    public AskQuestionHandler(
        IQueryProcessor queryProcessor,
        IEmbeddingPort embedding,
        IVectorStore vectorStore,
        IReranker reranker,
        IPromptBuilder promptBuilder,
        IChatCompletionPort chat,
        IOptions<RagOptions> options)
    {
        _queryProcessor = queryProcessor;
        _embedding = embedding;
        _vectorStore = vectorStore;
        _reranker = reranker;
        _promptBuilder = promptBuilder;
        _chat = chat;
        _options = options.Value;
    }

    public async IAsyncEnumerable<AskQuestionChunk> HandleAsync(
        string question,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var processed = await _queryProcessor.ProcessAsync(question, ct);
        var queryVector = await _embedding.EmbedAsync(processed.SearchText, ct);

        var candidates = await _vectorStore.SearchAsync(
            queryVector, _options.RetrievalTopK, _options.MinRelevanceScore, ct);

        if (candidates.Count == 0)
        {
            yield return AskQuestionChunk.NoContext();
            yield break;
        }

        var ranked = await _reranker.RerankAsync(
            processed.SearchText, candidates, _options.RerankTopN, ct);

        var prompt = _promptBuilder.Build(question, ranked);

        yield return AskQuestionChunk.OfSources(prompt.Sources);

        await foreach (var token in _chat.StreamAsync(prompt, ct))
            yield return AskQuestionChunk.OfToken(token);
    }
}
