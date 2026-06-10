namespace DocumentQA.Application.Abstractions.Generation;

public interface IChatCompletionPort
{
    IAsyncEnumerable<string> StreamAsync(PromptBundle prompt, string model, CancellationToken ct);
}
