using DocumentQA.Application.UseCases.AskQuestion;

namespace DocumentQA.Application.Abstractions.Generation;

public interface IAgentOrchestrator
{
    IAsyncEnumerable<AskQuestionChunk> OrchestrateAsync(
        string question,
        string[] modelFallbackChain,
        string tenantId,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken ct);
}

public sealed record ConversationTurn(string Role, string Content);
