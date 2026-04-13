namespace IntuneWinPackager.Models.Entities;

public sealed record IntuneRequirementRules
{
    public string OperatingSystemArchitecture { get; init; } = "x64";

    public string MinimumOperatingSystem { get; init; } = "Windows 10 1607";

    public int MinimumFreeDiskSpaceMb { get; init; }

    public int MinimumMemoryMb { get; init; }

    public int MinimumCpuSpeedMhz { get; init; }

    public int MinimumLogicalProcessors { get; init; }

    public string RequirementScriptBody { get; init; } = string.Empty;

    public bool RequirementScriptRunAs32BitOn64System { get; init; }

    public bool RequirementScriptEnforceSignatureCheck { get; init; }
}
