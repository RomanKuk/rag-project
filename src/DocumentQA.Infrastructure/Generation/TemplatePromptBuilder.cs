using System.Text;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Infrastructure.Generation;

public sealed class TemplatePromptBuilder : IPromptBuilder
{
    private const string SystemPrompt = """
        You are a document assistant. Answer ONLY from the provided context.
        - If the answer is not in the context, say "I cannot find this information in the available documents."
        - Cite every claim inline as [DocumentName, page X].
        - Be concise and factual. Never use outside knowledge.
        """;

    public PromptBundle Build(string question, IReadOnlyList<RetrievedChunk> context)
    {
        var sb = new StringBuilder();
        var citations = new List<Citation>();

        foreach (var rc in context)
        {
            var m = rc.Chunk.Metadata;
            sb.AppendLine($"[{m.DocumentName}, page {m.Page}]");
            sb.AppendLine(rc.Chunk.Content);
            sb.AppendLine();
            citations.Add(new Citation(
                m.DocumentName,
                m.Page,
                rc.Chunk.Content[..Math.Min(200, rc.Chunk.Content.Length)]));
        }

        var userPrompt = $"Context:\n{sb}\n\nQuestion: {question}";
        return new PromptBundle(SystemPrompt, userPrompt, citations);
    }
}
