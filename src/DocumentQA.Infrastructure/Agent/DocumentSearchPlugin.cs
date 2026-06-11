using System.ComponentModel;
using System.Text;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.Options;
using DocumentQA.Domain.Retrieval;
using Microsoft.SemanticKernel;

namespace DocumentQA.Infrastructure.Agent;

public sealed class DocumentSearchPlugin(
    IQueryProcessor queryProcessor,
    IEmbeddingPort embedding,
    IVectorStore vectorStore,
    IReranker reranker,
    RagOptions options,
    RetrievalScope scope,
    List<Citation> citationSink)
{
    [KernelFunction("search_documents")]
    [Description("Search the tenant's documents for relevant information. Call this before answering factual questions.")]
    public async Task<string> SearchAsync(
        [Description("The search query")] string query,
        CancellationToken cancellationToken = default)
    {
        var processed = await queryProcessor.ProcessAsync(query, cancellationToken);
        var queryVector = await embedding.EmbedAsync(processed.SearchText, cancellationToken);

        var candidates = await vectorStore.SearchHybridAsync(
            queryVector, processed.Keywords,
            options.RetrievalTopK, options.MinRelevanceScore,
            scope, cancellationToken);

        if (candidates.Count == 0)
            return "No relevant documents found for this query.";

        var ranked = await reranker.RerankAsync(query, candidates, options.RerankTopN, cancellationToken);

        // Populate shared citation sink so the orchestrator can emit an SSE sources event
        citationSink.Clear();
        citationSink.AddRange(ranked.Select(r =>
            new Citation(r.Chunk.Metadata.DocumentName, r.Chunk.Metadata.Page,
                r.Chunk.Content.Length > 200 ? r.Chunk.Content[..200] + "…" : r.Chunk.Content)));

        // Build context block for the model
        var sb = new StringBuilder();
        sb.AppendLine("<context>");
        foreach (var (chunk, i) in ranked.Select((c, i) => (c, i + 1)))
        {
            sb.AppendLine($"[{i}] {chunk.Chunk.Metadata.DocumentName} p.{chunk.Chunk.Metadata.Page}:");
            sb.AppendLine(chunk.Chunk.Content);
            sb.AppendLine();
        }
        sb.AppendLine("</context>");
        return sb.ToString();
    }
}
