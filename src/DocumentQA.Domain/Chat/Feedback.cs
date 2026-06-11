namespace DocumentQA.Domain.Chat;

public sealed class Feedback
{
    public long     Id        { get; set; }
    public Guid?    MessageId { get; set; }
    public string   TenantId  { get; set; } = string.Empty;
    public Guid?    UserId    { get; set; }
    public int      Rating    { get; set; } // +1 or -1
    public string?  Comment   { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
