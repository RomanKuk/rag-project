using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Models;
using DocumentQA.Application.Options;
using DocumentQA.Application.UseCases.AskQuestion;
using DocumentQA.Domain.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DocumentQA.Infrastructure.Agent;

public sealed class SemanticKernelOrchestrator(
    IConfiguration config,
    IOptions<RagOptions> ragOptions,
    IQueryProcessor queryProcessor,
    IEmbeddingPort embedding,
    IVectorStore vectorStore,
    IReranker reranker,
    ICompletionPort completion) : IAgentOrchestrator
{
    private const string SystemPrompt =
        "You are a document assistant. " +
        "Use the search_documents tool to find relevant information from the tenant's documents before answering. " +
        "Use the summarize tool when the user explicitly asks for a summary of a document or topic. " +
        "Always cite your sources using the document name and page number. " +
        "If no relevant information is found after searching, say so clearly.";

    public async IAsyncEnumerable<AskQuestionChunk> OrchestrateAsync(
        string question,
        string[] modelFallbackChain,
        RetrievalScope scope,
        IReadOnlyList<ConversationTurn>? priorHistory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var citations  = new List<Citation>();
        var toolEvents = Channel.CreateUnbounded<(string Tool, string Status)>();

        var model  = modelFallbackChain[0];
        var kernel = BuildKernel(model, scope, citations, toolEvents);

        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompt);

        // Replay prior conversation turns for multi-turn support
        if (priorHistory is { Count: > 0 })
        {
            foreach (var turn in priorHistory)
            {
                if (turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    history.AddUserMessage(turn.Content);
                else if (turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    history.AddAssistantMessage(turn.Content);
            }
        }

        history.AddUserMessage(question);

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            MaxTokens = 2048,
        };

        var chatSvc    = kernel.GetRequiredService<IChatCompletionService>();
        var accumulated = new StringBuilder();
        var citationsEmitted = false;

        await foreach (var content in chatSvc.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct))
        {
            // Drain tool events that arrived since the last iteration (tool ran while stream was paused)
            while (toolEvents.Reader.TryRead(out var te))
            {
                yield return AskQuestionChunk.OfToolCall(te.Tool, te.Status);

                // Emit sources after first search completes
                if (te is { Status: "done", Tool: "search_documents" } && !citationsEmitted && citations.Count > 0)
                {
                    yield return AskQuestionChunk.OfSources(citations);
                    citationsEmitted = true;
                }
            }

            if (content.Content is { Length: > 0 } text)
            {
                accumulated.Append(text);
                yield return AskQuestionChunk.OfToken(text);
            }
        }

        // Drain any remaining tool events (e.g. last tool was called after final stream ended)
        while (toolEvents.Reader.TryRead(out var remaining))
            yield return AskQuestionChunk.OfToolCall(remaining.Tool, remaining.Status);

        var inputTokens  = (SystemPrompt.Length + question.Length) / 4;
        var outputTokens = accumulated.Length / 4;
        yield return AskQuestionChunk.Done(new UsageSummary(
            inputTokens, outputTokens,
            CacheHit: false, FallbackUsed: false,
            Model: model));
    }

    private Kernel BuildKernel(string model, RetrievalScope scope, List<Citation> citations,
        Channel<(string, string)> toolEvents)
    {
        var options = ragOptions.Value;
        var builder = Kernel.CreateBuilder();

        var routerKey = config["OpenRouter:ApiKey"];
        if (!string.IsNullOrEmpty(routerKey))
        {
            var baseUrl = config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            builder.AddOpenAIChatCompletion(model, routerKey,
                httpClient: new HttpClient { BaseAddress = new Uri(baseUrl) });
        }
        else
        {
            builder.AddOpenAIChatCompletion(model, config["OpenAI:ApiKey"]!);
        }

        var kernel = builder.Build();

        kernel.Plugins.AddFromObject(new DocumentSearchPlugin(
            queryProcessor, embedding, vectorStore, reranker, options, scope, citations),
            "DocumentSearch");
        kernel.Plugins.AddFromObject(new SummarizePlugin(
            queryProcessor, embedding, vectorStore, completion, options, scope),
            "Summarize");

        // Invocation filter to emit tool_call SSE events
        kernel.FunctionInvocationFilters.Add(new ToolCallEventFilter(toolEvents.Writer));

        return kernel;
    }

    private sealed class ToolCallEventFilter(ChannelWriter<(string, string)> writer) : IFunctionInvocationFilter
    {
        public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
        {
            writer.TryWrite((context.Function.Name, "running"));
            await next(context);
            writer.TryWrite((context.Function.Name, "done"));
        }
    }
}
