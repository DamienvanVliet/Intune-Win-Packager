namespace IntuneWinPackager.Models.Enums;

public enum EvidenceSourceType
{
    Unknown = 0,
    InstallerMetadata = 1,
    SourceManifest = 2,
    SandboxSnapshot = 3,
    ExecutionProbe = 4,
    LocalProofStore = 5,
    UserConfirmed = 6,
    Heuristic = 7
}
