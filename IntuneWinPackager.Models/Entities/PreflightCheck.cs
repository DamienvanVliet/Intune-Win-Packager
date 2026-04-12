using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PreflightCheck
{
    public string Key { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public PreflightSeverity Severity { get; init; } = PreflightSeverity.Info;
}
