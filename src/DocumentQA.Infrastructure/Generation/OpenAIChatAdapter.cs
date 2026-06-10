using System.Runtime.CompilerServices;
using DocumentQA.Application.Abstractions.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DocumentQA.Infrastructure.Generation;

public sealed class OpenAIChatAdapter(IConfiguration config) : IChatCompletionPort
{
    public async IAsyncEnumerable<string> StreamAsync(
        PromptBundle prompt,
        string model,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var svc = BuildService(model);
        var history = new Microsoft.SemanticKernel.ChatCompletion.ChatHistory();
        history.AddSystemMessage(prompt.SystemPrompt);
        history.AddUserMessage(prompt.UserPrompt);

        await foreach (var chunk in svc.GetStreamingChatMessageContentsAsync(history, cancellationToken: ct))
        {
            if (chunk.Content is { Length: > 0 } t)
                yield return t;
        }
    }

    private OpenAIChatCompletionService BuildService(string model)
    {
        var routerKey = config["OpenRouter:ApiKey"];
        if (!string.IsNullOrEmpty(routerKey))
        {
            var baseUrl = config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            // HttpClient is created per-call — acceptable at course-project request rates.
            // For production, inject IHttpClientFactory and use a named client instead.
            var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            return new OpenAIChatCompletionService(model, routerKey, httpClient: http);
        }

        return new OpenAIChatCompletionService(model, config["OpenAI:ApiKey"]!);
    }
}
