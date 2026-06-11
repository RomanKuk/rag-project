using DocumentQA.Domain.Retrieval;

namespace DocumentQA.Application.Abstractions.Generation;

public interface IGroundednessCheck
{
    Task<GroundednessResult> CheckAsync(
        string answer,
        IReadOnlyList<RetrievedChunk> context,
        CancellationToken ct);
}

public sealed record GroundednessResult(bool IsGrounded, string? UnsupportedClaim);
