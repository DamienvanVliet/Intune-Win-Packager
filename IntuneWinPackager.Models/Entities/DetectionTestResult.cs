namespace IntuneWinPackager.Models.Entities;

public sealed record DetectionTestResult
{
    public bool Success { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public int ExitCode { get; init; }

    public bool HasStdOut { get; init; }

    public bool HasStdErr { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;
}
