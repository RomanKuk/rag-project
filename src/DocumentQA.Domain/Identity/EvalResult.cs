namespace DocumentQA.Domain.Identity;

public sealed class EvalResult
{
    public long     Id          { get; set; }
    public DateTime RunAt       { get; set; } = DateTime.UtcNow;
    public bool     Passed      { get; set; }
    public string   Mode        { get; set; } = "full";
    public string   ResultsJson { get; set; } = "{}";
}
