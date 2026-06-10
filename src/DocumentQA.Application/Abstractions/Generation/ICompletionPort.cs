namespace DocumentQA.Application.Abstractions.Generation;

public interface ICompletionPort
{
    Task<string> CompleteAsync(string system, string user, string model, CancellationToken ct);
}
