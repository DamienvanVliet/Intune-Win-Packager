using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record IntuneDetectionRule
{
    public IntuneDetectionRuleType RuleType { get; init; } = IntuneDetectionRuleType.None;

    public MsiDetectionRule Msi { get; init; } = new();

    public FileDetectionRule File { get; init; } = new();

    public RegistryDetectionRule Registry { get; init; } = new();

    public ScriptDetectionRule Script { get; init; } = new();
}
