namespace DocumentQA.Application.Abstractions.Generation;

/// <summary>Counts tokens the way the LLM tokenizer would (replaces length/4 estimates).</summary>
public interface ITokenCounter
{
    int Count(string text);
}
