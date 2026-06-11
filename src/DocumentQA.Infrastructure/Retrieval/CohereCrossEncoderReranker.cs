using System.Net.Http.Json;
using System.Text.Json;
using DocumentQA.Application.Abstractions.Retrieval;
using DocumentQA.Application.Options;
using DocumentQA.Domain.Retrieval;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocumentQA.Infrastructure.Retrieval;

public sealed class CohereCrossEncoderReranker : ICrossEncoderReranker
{
    private readonly HttpClient _http;
    private readonly ILogger<CohereCrossEncoderReranker> _logger;
    private readonly string _model;

    public CohereCrossEncoderReranker(
        IHttpClientFactory httpFactory,
        IOptions<RerankerOptions> opts,
        ILogger<CohereCrossEncoderReranker> logger)
    {
        _logger = logger;
        _model  = opts.Value.Model ?? "rerank-english-v3.0";
        _http   = httpFactory.CreateClient("Cohere");
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return candidates;

        try
        {
            var body = new
            {
                model     = _model,
                query,
                documents = candidates.Select(c => c.Chunk.Content).ToArray(),
                top_n     = Math.Min(topN, candidates.Count),
            };

            var response = await _http.PostAsJsonAsync(
                "https://api.cohere.com/v1/rerank", body, ct);
            response.EnsureSuccessStatusCode();

            using var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var results    = doc.RootElement.GetProperty("results");

            var reranked = results.EnumerateArray()
                .OrderBy(r => r.GetProperty("index").GetInt32())  // sorted by original idx
                .OrderByDescending(r => r.GetProperty("relevance_score").GetDouble())
                .Take(topN)
                .Select(r => candidates[r.GetProperty("index").GetInt32()])
                .ToList();

            _logger.LogInformation("Cohere reranker: {In} → {Out}", candidates.Count, reranked.Count);
            return reranked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cohere reranker failed, returning top-N by score");
            return candidates.Take(topN).ToList();
        }
    }
}

public sealed class RerankerOptions
{
    public string? Provider { get; init; } = "llm";
    public string? ApiKey   { get; init; }
    public string? Model    { get; init; }
}
