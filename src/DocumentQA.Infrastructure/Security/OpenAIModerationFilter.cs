using System.Net.Http.Json;
using System.Text.Json;
using DocumentQA.Application.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace DocumentQA.Infrastructure.Security;

public sealed class OpenAIModerationFilter : ISafetyFilter
{
    private readonly HttpClient _http;
    private readonly ILogger<OpenAIModerationFilter> _logger;

    public OpenAIModerationFilter(IHttpClientFactory httpFactory, ILogger<OpenAIModerationFilter> logger)
    {
        _http   = httpFactory.CreateClient("OpenAIModeration");
        _logger = logger;
    }

    public async Task<SafetyResult> CheckAsync(string text, CancellationToken ct)
    {
        try
        {
            var body = new { input = text[..Math.Min(2048, text.Length)] };
            var response = await _http.PostAsJsonAsync("https://api.openai.com/v1/moderations", body, ct);
            response.EnsureSuccessStatusCode();

            using var doc    = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var results      = doc.RootElement.GetProperty("results");
            var first        = results.EnumerateArray().FirstOrDefault();
            var flagged      = first.ValueKind != JsonValueKind.Undefined && first.GetProperty("flagged").GetBoolean();

            if (!flagged) return new SafetyResult(true, null);

            var categories = first.GetProperty("categories");
            var triggered  = categories.EnumerateObject()
                .FirstOrDefault(p => p.Value.GetBoolean()).Name ?? "unknown";

            _logger.LogWarning("Moderation flagged text as: {Category}", triggered);
            return new SafetyResult(false, triggered);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Moderation check failed — allowing text through");
            return new SafetyResult(true, null); // fail-open to avoid blocking on API errors
        }
    }
}
