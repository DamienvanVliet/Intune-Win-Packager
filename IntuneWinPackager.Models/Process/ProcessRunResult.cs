namespace IntuneWinPackager.Models.Process;

public sealed record ProcessRunResult
{
    public int ExitCode { get; init; }

    public bool TimedOut { get; init; }
}
