namespace DocumentQA.Application.Options;

public sealed class CacheOptions
{
    public bool Enabled { get; init; } = true;
    public double SimilarityThreshold { get; init; } = 0.92;
    public int TtlMinutes { get; init; } = 60;
}
