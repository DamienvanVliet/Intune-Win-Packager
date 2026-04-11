namespace IntuneWinPackager.Models.Process;

public sealed record ProcessRunRequest
{
    public string FileName { get; init; } = string.Empty;

    public string Arguments { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public bool PreferLowImpact { get; init; } = true;
}
