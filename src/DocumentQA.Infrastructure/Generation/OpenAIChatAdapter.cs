using System.Runtime.CompilerServices;
using DocumentQA.Application.Abstractions.Generation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;

namespace DocumentQA.Infrastructure.Generation;

public sealed class OpenAIChatAdapter : IChatCompletionPort
{
    private readonly IChatCompletionService _sk;

    public OpenAIChatAdapter(
        [FromKeyedServices("chat")] IChatCompletionService sk)
        => _sk = sk;

    public async IAsyncEnumerable<string> StreamAsync(
        PromptBundle prompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var history = new ChatHistory();
        history.AddSystemMessage(prompt.SystemPrompt);
        history.AddUserMessage(prompt.UserPrompt);

        await foreach (var chunk in _sk.GetStreamingChatMessageContentsAsync(
            history, cancellationToken: ct))
        {
            yield return chunk.Content ?? "";
        }
    }
}
