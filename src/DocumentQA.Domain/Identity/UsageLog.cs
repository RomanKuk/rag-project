namespace DocumentQA.Domain.Identity;

public sealed class UsageLog
{
    public long     Id           { get; set; }
    public string   RequestId    { get; set; } = string.Empty;
    public string   ApiKey       { get; set; } = string.Empty;
    public string   TenantId     { get; set; } = "public"; // tenant slug
    public Guid?    UserId       { get; set; }
    public string   Model        { get; set; } = string.Empty;
    public int      InputTokens  { get; set; }
    public int      OutputTokens { get; set; }
    public decimal  CostUsd      { get; set; }
    public int      LatencyMs    { get; set; }
    public int?     TtftMs       { get; set; }
    public bool     CacheHit     { get; set; }
    public bool     FallbackUsed { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;
}
