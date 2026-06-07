namespace DocumentQA.Application.Abstractions.Generation;

public interface IChatCompletionPort
{
    IAsyncEnumerable<string> StreamAsync(PromptBundle prompt, CancellationToken ct);
}
