namespace IntuneWinPackager.Models.Entities;

public sealed record ToolInstallResult
{
    public bool Success { get; init; }

    public bool AlreadyInstalled { get; init; }

    public int ExitCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? ToolPath { get; init; }
}
