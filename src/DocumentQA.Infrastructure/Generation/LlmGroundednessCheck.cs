using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Options;
using DocumentQA.Domain.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Generation;

public sealed class LlmGroundednessCheck : IGroundednessCheck
{
    private readonly ICompletionPort _completion;
    private readonly string _model;
    private readonly ILogger<LlmGroundednessCheck> _logger;

    private const string SystemPrompt = """
        You are a factual grounding checker.
        Given an AI answer and a set of source passages, determine if every factual claim in the answer
        is supported by the provided sources.
        If all claims are supported, respond with exactly: GROUNDED
        If any claim is not supported, respond with exactly: UNSUPPORTED: <the first unsupported claim (one sentence)>
        Do not add any other text.
        """;

    public LlmGroundednessCheck(
        ICompletionPort completion,
        IOptions<RagOptions> opts,
        ILogger<LlmGroundednessCheck> logger)
    {
        _completion = completion;
        _model      = opts.Value.UtilityModel;
        _logger     = logger;
    }

    public async Task<GroundednessResult> CheckAsync(
        string answer,
        IReadOnlyList<RetrievedChunk> context,
        CancellationToken ct)
    {
        if (context.Count == 0) return new GroundednessResult(true, null);

        try
        {
            var contextText = string.Join("\n\n",
                context.Take(5).Select(c => $"[{c.Chunk.Metadata.DocumentName}] {c.Chunk.Content[..Math.Min(300, c.Chunk.Content.Length)]}"));

            var userMsg = $"Sources:\n{contextText}\n\nAnswer:\n{answer}";
            var result  = await _completion.CompleteAsync(SystemPrompt, userMsg, _model, ct);

            var trimmed = result.Trim();
            if (trimmed.Equals("GROUNDED", StringComparison.OrdinalIgnoreCase))
                return new GroundednessResult(true, null);

            if (trimmed.StartsWith("UNSUPPORTED:", StringComparison.OrdinalIgnoreCase))
            {
                var claim = trimmed["UNSUPPORTED:".Length..].Trim();
                _logger.LogWarning("Groundedness check failed: {Claim}", claim);
                return new GroundednessResult(false, claim);
            }

            return new GroundednessResult(true, null); // ambiguous → pass
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Groundedness check threw — allowing answer through");
            return new GroundednessResult(true, null); // fail-open
        }
    }
}
