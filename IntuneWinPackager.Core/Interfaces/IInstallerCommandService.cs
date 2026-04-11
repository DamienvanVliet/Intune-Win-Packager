using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Interfaces;

public interface IInstallerCommandService
{
    InstallerType DetectInstallerType(string setupFilePath);

    IReadOnlyList<SilentInstallPreset> GetExeSilentPresets();

    CommandSuggestion CreateSuggestion(string setupFilePath, InstallerType installerType, MsiMetadata? msiMetadata = null, SilentInstallPreset? preset = null);
}
