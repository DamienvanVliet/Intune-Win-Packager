using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record FileDetectionRule
{
    public string Path { get; init; } = string.Empty;

    public string FileOrFolderName { get; init; } = string.Empty;

    public bool Check32BitOn64System { get; init; }

    public IntuneDetectionOperator Operator { get; init; } = IntuneDetectionOperator.Exists;

    public string Value { get; init; } = string.Empty;
}
