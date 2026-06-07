using System.Diagnostics;

namespace DocumentQA.Application.Telemetry;

public static class RagActivitySource
{
    public static readonly ActivitySource Source = new("DocumentQA.Rag", "1.0.0");
}
