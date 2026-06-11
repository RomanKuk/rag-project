using DocumentQA.Application.Abstractions.Generation;
using SharpToken;

namespace DocumentQA.Infrastructure.Generation;

public sealed class SharpTokenCounter : ITokenCounter
{
    private static readonly GptEncoding Encoding = GptEncoding.GetEncoding("cl100k_base");

    public int Count(string text) =>
        string.IsNullOrEmpty(text) ? 0 : Encoding.CountTokens(text);
}
