namespace DocumentQA.Application.Models;

public sealed class TierInfo
{
    public int TokensPerMinute { get; init; }
    public string[] Models { get; init; } = [];
}
