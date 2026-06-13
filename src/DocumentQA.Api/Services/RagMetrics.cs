using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DocumentQA.Api.Services;

/// <summary>
/// Custom RAG metrics exported to Prometheus via the OpenTelemetry meter
/// "DocumentQA.Rag". Hooked from ChatEndpoints (the only place with tenant,
/// model, cost, latency, and guard outcome all in scope). Observable gauges
/// read the existing <see cref="StreamMetrics"/> singleton.
/// </summary>
public sealed class RagMetrics
{
    public const string MeterName = "DocumentQA.Rag";

    private readonly Counter<long> _requests;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _ttft;
    private readonly Counter<long> _guardBlocks;

    public RagMetrics(StreamMetrics streams)
    {
        var meter = new Meter(MeterName);

        // No explicit units: the OTel Prometheus exporter appends a unit suffix
        // to the metric name, which would mangle these (e.g. rag_cost_usd_total).
        // Names are already Prometheus-idiomatic, so keep them verbatim.
        _requests = meter.CreateCounter<long>(
            "rag_requests_total", description: "Completed chat requests.");
        _tokens = meter.CreateCounter<long>(
            "rag_tokens_total", description: "LLM tokens by direction.");
        _cost = meter.CreateCounter<double>(
            "rag_cost_usd_total", description: "Estimated LLM spend (USD).");
        _duration = meter.CreateHistogram<double>(
            "rag_request_duration_seconds", description: "End-to-end request latency (s).");
        _ttft = meter.CreateHistogram<double>(
            "rag_ttft_seconds", description: "Time to first streamed token (s).");
        _guardBlocks = meter.CreateCounter<long>(
            "rag_guard_blocks_total", description: "Requests blocked by InputGuard.");

        meter.CreateObservableGauge(
            "rag_active_streams", () => (long)streams.Active, description: "Active SSE streams.");
        meter.CreateObservableCounter(
            "rag_aborted_streams_total", () => (long)streams.Aborted, description: "Aborted SSE streams.");
    }

    public void RecordRequest(
        string tenant, string model, bool cacheHit,
        int inputTokens, int outputTokens, decimal costUsd,
        double durationSeconds, double? ttftSeconds)
    {
        var tags = new TagList
        {
            { "tenant", tenant },
            { "model", model },
            { "cache_hit", cacheHit ? "true" : "false" },
        };
        _requests.Add(1, tags);
        _cost.Add((double)costUsd, new TagList { { "tenant", tenant }, { "model", model } });

        _tokens.Add(inputTokens, new TagList { { "tenant", tenant }, { "direction", "input" } });
        _tokens.Add(outputTokens, new TagList { { "tenant", tenant }, { "direction", "output" } });

        _duration.Record(durationSeconds, new TagList { { "tenant", tenant } });
        if (ttftSeconds is { } t)
            _ttft.Record(t, new TagList { { "tenant", tenant } });
    }

    public void RecordGuardBlock() => _guardBlocks.Add(1);
}
