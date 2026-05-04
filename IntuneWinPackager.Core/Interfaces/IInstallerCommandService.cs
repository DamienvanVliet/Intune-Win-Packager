using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Interfaces;

public interface IInstallerCommandService
{
    InstallerType DetectInstallerType(string setupFilePath);

    IReadOnlyList<SilentInstallPreset> GetExeSilentPresets();

    CommandSuggestion CreateSuggestion(
        string setupFilePath,
        InstallerType installerType,
        MsiMetadata? msiMetadata = null,
        SilentInstallPreset? preset = null,
        DetectionDeploymentIntent detectionIntent = DetectionDeploymentIntent.Install,
        string sourceChannelHint = "",
        string installerArchitectureHint = "");

    void SaveVerifiedKnowledge(
        string setupFilePath,
        InstallerType installerType,
        string installCommand,
        string uninstallCommand,
        IntuneWin32AppRules intuneRules,
        string sourceChannelHint = "",
        string installerArchitectureHint = "");
}
