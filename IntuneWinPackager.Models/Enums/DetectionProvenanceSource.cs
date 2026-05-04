namespace IntuneWinPackager.Models.Enums;

public enum DetectionProvenanceSource
{
    Unknown = 0,
    InstallerMetadata = 1,
    ManifestSource = 2,
    LocalUninstallRegistry = 3,
    VerifiedCache = 4,
    UserInput = 5,
    HeuristicFallback = 6
}

