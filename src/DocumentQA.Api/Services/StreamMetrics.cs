namespace DocumentQA.Api.Services;

public sealed class StreamMetrics
{
    private int _active;
    private int _aborted;

    public int Active => _active;
    public int Aborted => _aborted;

    public void Increment() => Interlocked.Increment(ref _active);
    public void Decrement() => Interlocked.Decrement(ref _active);
    public void RecordAbort() => Interlocked.Increment(ref _aborted);
}
