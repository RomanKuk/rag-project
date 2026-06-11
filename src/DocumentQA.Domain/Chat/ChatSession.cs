namespace DocumentQA.Domain.Chat;

public sealed class ChatSession
{
    public Guid     Id               { get; set; } = Guid.NewGuid();
    public string   TenantId         { get; set; } = string.Empty; // tenant slug
    public Guid     UserId           { get; set; }
    public string   Title            { get; set; } = "New chat";
    public bool     IncludeSharedDocs { get; set; } = true;
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt        { get; set; } = DateTime.UtcNow;

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();
}
