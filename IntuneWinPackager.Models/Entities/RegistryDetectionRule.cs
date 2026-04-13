using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record RegistryDetectionRule
{
    public string Hive { get; init; } = "HKEY_LOCAL_MACHINE";

    public string KeyPath { get; init; } = string.Empty;

    public string ValueName { get; init; } = string.Empty;

    public bool Check32BitOn64System { get; init; }

    public IntuneDetectionOperator Operator { get; init; } = IntuneDetectionOperator.Exists;

    public string Value { get; init; } = string.Empty;
}
