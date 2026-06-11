using System.ComponentModel;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.Options;
using Microsoft.SemanticKernel;

namespace DocumentQA.Infrastructure.Agent;

public sealed class SummarizePlugin(
    IQueryProcessor queryProcessor,
    IEmbeddingPort embedding,
    IVectorStore vectorStore,
    ICompletionPort completion,
    RagOptions options,
    RetrievalScope scope)
{
    [KernelFunction("summarize")]
    [Description("Summarize a topic or document by name. Use this when the user explicitly asks for a summary.")]
    public async Task<string> SummarizeAsync(
        [Description("The document name or topic to summarize")] string topic,
        CancellationToken cancellationToken = default)
    {
        var processed = await queryProcessor.ProcessAsync(topic, cancellationToken);
        var queryVector = await embedding.EmbedAsync(processed.SearchText, cancellationToken);

        var candidates = await vectorStore.SearchHybridAsync(
            queryVector, processed.Keywords,
            options.RetrievalTopK, options.MinRelevanceScore,
            scope, cancellationToken);

        if (candidates.Count == 0)
            return $"No content found for '{topic}'.";

        var ranked = await vectorStore.SearchAsync(
            queryVector, options.RerankTopN, options.MinRelevanceScore, scope, cancellationToken);

        var context = string.Join("\n\n", ranked.Select(r => r.Chunk.Content));
        var system  = "You are a document summarizer. Produce a concise, accurate summary.";
        var user    = $"Summarize the following content about '{topic}':\n\n{context}";

        return await completion.CompleteAsync(system, user, options.UtilityModel, cancellationToken);
    }
}
