using DocumentQA.Application.Abstractions.Generation;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace DocumentQA.Infrastructure.Generation;

public sealed class OpenAICompletionAdapter(IConfiguration config) : ICompletionPort
{
    public async Task<string> CompleteAsync(string system, string user, string model, CancellationToken ct)
    {
        var svc = BuildService(model);
        var history = new ChatHistory();
        history.AddSystemMessage(system);
        history.AddUserMessage(user);
        var result = await svc.GetChatMessageContentAsync(history, cancellationToken: ct);
        return result.Content ?? string.Empty;
    }

    private OpenAIChatCompletionService BuildService(string model)
    {
        var routerKey = config["OpenRouter:ApiKey"];
        if (!string.IsNullOrEmpty(routerKey))
        {
            var baseUrl = config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
            var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            return new OpenAIChatCompletionService(model, routerKey, httpClient: http);
        }
        return new OpenAIChatCompletionService(model, config["OpenAI:ApiKey"]!);
    }
}
