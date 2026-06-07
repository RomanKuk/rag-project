namespace DocumentQA.Ingestion;

public class SlidingWindowChunker
{
    private readonly int _chunkSize;
    private readonly int _overlapSize;

    public SlidingWindowChunker(int chunkSize = 500, int overlapSize = 100)
    {
        _chunkSize = chunkSize;
        _overlapSize = overlapSize;
    }

    public IEnumerable<string> Chunk(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) yield break;

        var step = _chunkSize - _overlapSize;
        for (int i = 0; i < words.Length; i += step)
        {
            yield return string.Join(" ", words.Skip(i).Take(_chunkSize));
            if (i + _chunkSize >= words.Length) break;
        }
    }
}
