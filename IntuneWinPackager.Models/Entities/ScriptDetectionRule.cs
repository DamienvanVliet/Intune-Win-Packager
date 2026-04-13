namespace IntuneWinPackager.Models.Entities;

public sealed record ScriptDetectionRule
{
    public string ScriptBody { get; init; } = string.Empty;

    public bool RunAs32BitOn64System { get; init; }

    public bool EnforceSignatureCheck { get; init; }
}
