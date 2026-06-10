namespace DocumentQA.Api.Services;

internal static class Pricing
{
    // $/1M tokens — input and output rates
    private static readonly Dictionary<string, (decimal Input, decimal Output)> Rates = new()
    {
        ["gpt-4o"]                                 = (2.50m,  10.0m),
        ["gpt-4o-mini"]                            = (0.15m,   0.60m),
        ["openai/gpt-4o"]                          = (2.50m,  10.0m),
        ["openai/gpt-4o-mini"]                     = (0.15m,   0.60m),
        ["meta-llama/llama-3.1-8b-instruct"]       = (0.10m,   0.10m),
        ["google/gemini-flash-1.5"]                = (0.075m,  0.30m),
        ["meta-llama/llama-3.2-3b-instruct:free"]  = (0.0m,    0.0m),
        ["anthropic/claude-3.5-haiku"]             = (0.80m,   4.00m),
        ["google/gemini-1.5-pro"]                  = (1.25m,   5.00m),
        ["anthropic/claude-opus-4"]                = (15.0m,  75.0m),
        ["google/gemini-1.5-ultra"]                = (3.50m,  10.5m),
    };

    public static decimal Calculate(string model, int inputTokens, int outputTokens)
    {
        if (!Rates.TryGetValue(model, out var rate)) return 0m;
        return (inputTokens * rate.Input + outputTokens * rate.Output) / 1_000_000m;
    }
}
