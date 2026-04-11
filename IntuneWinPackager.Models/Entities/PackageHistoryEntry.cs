using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record PackageHistoryEntry
{
    public DateTimeOffset TimestampUtc { get; init; }

    public string SetupFilePath { get; init; } = string.Empty;

    public string OutputPackagePath { get; init; } = string.Empty;

    public InstallerType InstallerType { get; init; } = InstallerType.Unknown;

    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;
}
