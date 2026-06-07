namespace DocumentQA.Application.Abstractions.Security;

public interface ISuspiciousActivityLog
{
    Task LogRequestAsync(string input, string reason, string remoteIp, CancellationToken ct = default);
    Task LogResponseAsync(string question, string fragment, CancellationToken ct = default);
}
