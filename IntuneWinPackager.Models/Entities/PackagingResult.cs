namespace IntuneWinPackager.Models.Entities;

public sealed record PackagingResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? OutputPackagePath { get; init; }

    public int ExitCode { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }
}
