namespace IntuneWinPackager.Models.Entities;

public sealed record SandboxProofSession
{
    public bool Success { get; init; }

    public bool Launched { get; init; }

    public string Message { get; init; } = string.Empty;

    public string RunDirectory { get; init; } = string.Empty;

    public string WsbPath { get; init; } = string.Empty;

    public string InputPath { get; init; } = string.Empty;

    public string RunnerScriptPath { get; init; } = string.Empty;

    public string ReportPath { get; init; } = string.Empty;

    public string ResultPath { get; init; } = string.Empty;
}
