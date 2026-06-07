using DocumentQA.Application.Abstractions.Security;

namespace DocumentQA.Infrastructure.Security;

public sealed class SuspiciousActivityLogger : ISuspiciousActivityLog
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public Task LogRequestAsync(string input, string reason, string remoteIp, CancellationToken ct = default)
        => AppendAsync("suspicious_requests.log",
            $"[{DateTimeOffset.UtcNow:O}] IP={remoteIp} Reason={reason} Input={Truncate(input, 300)}");

    public Task LogResponseAsync(string question, string fragment, CancellationToken ct = default)
        => AppendAsync("suspicious_responses.log",
            $"[{DateTimeOffset.UtcNow:O}] output_filtered=true Fragment={fragment} Question={Truncate(question, 200)}");

    private static async Task AppendAsync(string file, string line)
    {
        await FileLock.WaitAsync();
        try { await File.AppendAllTextAsync(file, line + Environment.NewLine); }
        finally { FileLock.Release(); }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
