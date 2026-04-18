namespace IntuneWinPackager.Models.Entities;

public sealed record CatalogProviderDiagnostic
{
    public string ProviderId { get; init; } = string.Empty;

    public string SourceChannel { get; init; } = string.Empty;

    public bool IsHealthy { get; init; } = true;

    public int TotalRequests { get; init; }

    public int TotalFailures { get; init; }

    public int ConsecutiveFailures { get; init; }

    public int TimeoutCount { get; init; }

    public long LastDurationMs { get; init; }

    public string LastError { get; init; } = string.Empty;

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public DateTimeOffset? LastFailureAtUtc { get; init; }
}
