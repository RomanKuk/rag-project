namespace DocumentQA.Core.Models;

public record ChatRequest(
    string Question,
    string? ConversationId = null
);
