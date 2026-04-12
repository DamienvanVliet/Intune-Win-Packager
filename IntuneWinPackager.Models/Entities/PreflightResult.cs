using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PreflightResult
{
    public List<PreflightCheck> Checks { get; init; } = new();

    public bool HasErrors => Checks.Any(check => !check.Passed && check.Severity == PreflightSeverity.Error);

    public bool HasWarnings => Checks.Any(check => !check.Passed && check.Severity == PreflightSeverity.Warning);

    public int PassedCount => Checks.Count(check => check.Passed);

    public int TotalCount => Checks.Count;
}
