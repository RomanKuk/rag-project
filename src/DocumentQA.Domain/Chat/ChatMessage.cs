namespace DocumentQA.Domain.Chat;

public sealed class ChatMessage
{
    public Guid     Id           { get; set; } = Guid.NewGuid();
    public Guid     SessionId    { get; set; }
    public string   Role         { get; set; } = string.Empty; // "user" | "assistant"
    public string   Content      { get; set; } = string.Empty;
    public string?  SourcesJson  { get; set; }
    public int      InputTokens  { get; set; }
    public int      OutputTokens { get; set; }
    public decimal  CostUsd      { get; set; }
    public DateTime CreatedAt    { get; set; } = DateTime.UtcNow;

    public ChatSession? Session  { get; set; }
}
