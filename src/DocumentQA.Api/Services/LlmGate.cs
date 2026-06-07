namespace DocumentQA.Api.Services;

public sealed class LlmGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public int MaxConcurrent { get; }

    public LlmGate(int maxConcurrent = 20)
    {
        MaxConcurrent = maxConcurrent;
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public int Available => _semaphore.CurrentCount;

    public Task<bool> TryAcquireAsync(CancellationToken ct) =>
        _semaphore.WaitAsync(0, ct).ContinueWith(t => t.Result, ct);

    public async Task AcquireAsync(CancellationToken ct) =>
        await _semaphore.WaitAsync(ct);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
