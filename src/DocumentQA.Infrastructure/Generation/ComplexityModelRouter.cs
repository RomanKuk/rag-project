using DocumentQA.Application.Abstractions.Generation;
using DocumentQA.Application.Options;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Generation;

public sealed class ComplexityModelRouter : IModelRouter
{
    private readonly string _simpleModel;
    private readonly string _complexModel;

    public ComplexityModelRouter(IOptions<RagOptions> opts)
    {
        _simpleModel  = opts.Value.SimpleModel;
        _complexModel = opts.Value.ComplexModel;
    }

    public string[] Route(string question, string[] defaultChain)
    {
        if (IsComplex(question))
            return [_complexModel, ..defaultChain.Where(m => m != _complexModel)];

        return [_simpleModel, ..defaultChain.Where(m => m != _simpleModel)];
    }

    private static bool IsComplex(string question)
    {
        // Heuristics: long questions, multiple sentences, analytical keywords
        if (question.Length > 300) return true;

        var lower   = question.ToLowerInvariant();
        var markers = new[] { "compare", "contrast", "summarize", "analyze", "explain", "difference", "why", "how does", "what are the" };
        var count   = markers.Count(m => lower.Contains(m));
        if (count >= 2) return true;

        var sentences = question.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries);
        return sentences.Length >= 3;
    }
}
