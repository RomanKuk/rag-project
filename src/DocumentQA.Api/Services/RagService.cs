using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DocumentQA.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Memory;

namespace DocumentQA.Api.Services;

public class RagService
{
    private const string Collection = "documents";

    private const string SystemPromptTemplate = """
        You are a document assistant. Answer the user's question ONLY based on the provided context.

        Rules:
        - If the answer is not in the context, say "I cannot find this information in the available documents."
        - Always cite the source document and page number for each claim.
        - Be concise and factual. Do not add information from your general knowledge.
        - Format citations inline as: [DocumentName, page X]

        Context:
        {context}
        """;

    private readonly Kernel _kernel;
    private readonly ISemanticTextMemory _memory;

    public RagService(Kernel kernel, ISemanticTextMemory memory)
    {
        _kernel = kernel;
        _memory = memory;
    }

    public async IAsyncEnumerable<string> AskAsync(
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var contextBuilder = new StringBuilder();
        var sources = new List<SourceReference>();

        await foreach (var result in _memory.SearchAsync(
            collection: Collection,
            query: question,
            limit: 5,
            minRelevanceScore: 0.7,
            cancellationToken: cancellationToken))
        {
            var meta = JsonSerializer.Deserialize<ChunkMetadata>(
                result.Metadata.AdditionalMetadata ?? "{}");

            if (meta is not null)
            {
                contextBuilder.AppendLine($"[{meta.DocName}, page {meta.Page}]");
                contextBuilder.AppendLine(result.Metadata.Text);
                contextBuilder.AppendLine();

                sources.Add(new SourceReference(
                    meta.DocName,
                    meta.Page,
                    result.Metadata.Text[..Math.Min(200, result.Metadata.Text.Length)]));
            }
        }

        var prompt = SystemPromptTemplate.Replace("{context}", contextBuilder.ToString());
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(prompt);
        chatHistory.AddUserMessage(question);

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(
            chatHistory, cancellationToken: cancellationToken))
        {
            yield return chunk.Content ?? "";
        }
    }
}
