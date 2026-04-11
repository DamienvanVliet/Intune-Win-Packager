using System.IO;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Services;

public sealed class InstallerCommandService : IInstallerCommandService
{
    private static readonly IReadOnlyList<SilentInstallPreset> ExePresets = new List<SilentInstallPreset>
    {
        new()
        {
            Name = "Generic Quiet",
            Description = "Common vendor installer switches.",
            InstallArguments = "/quiet /norestart",
            UninstallArguments = "/uninstall /quiet /norestart"
        },
        new()
        {
            Name = "Inno Setup",
            Description = "Inno Setup silent mode.",
            InstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            UninstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
        },
        new()
        {
            Name = "NSIS",
            Description = "NSIS silent mode.",
            InstallArguments = "/S",
            UninstallArguments = "/S"
        },
        new()
        {
            Name = "InstallShield",
            Description = "InstallShield basic silent mode.",
            InstallArguments = "/s /v\"/qn REBOOT=ReallySuppress\"",
            UninstallArguments = "/s /x /v\"/qn REBOOT=ReallySuppress\""
        }
    };

    public InstallerType DetectInstallerType(string setupFilePath)
    {
        if (string.IsNullOrWhiteSpace(setupFilePath))
        {
            return InstallerType.Unknown;
        }

        var extension = Path.GetExtension(setupFilePath);
        return extension.ToLowerInvariant() switch
        {
            ".msi" => InstallerType.Msi,
            ".exe" => InstallerType.Exe,
            _ => InstallerType.Unknown
        };
    }

    public IReadOnlyList<SilentInstallPreset> GetExeSilentPresets() => ExePresets;

    public CommandSuggestion CreateSuggestion(
        string setupFilePath,
        InstallerType installerType,
        MsiMetadata? msiMetadata = null,
        SilentInstallPreset? preset = null)
    {
        var setupFileName = Path.GetFileName(setupFilePath);

        if (installerType == InstallerType.Msi)
        {
            var installCommand = $"msiexec /i \"{setupFileName}\" /qn /norestart";
            var uninstallTarget = string.IsNullOrWhiteSpace(msiMetadata?.ProductCode)
                ? $"\"{setupFileName}\""
                : msiMetadata.ProductCode;

            var uninstallCommand = $"msiexec /x {uninstallTarget} /qn /norestart";

            return new CommandSuggestion
            {
                InstallCommand = installCommand,
                UninstallCommand = uninstallCommand
            };
        }

        if (installerType == InstallerType.Exe)
        {
            var selectedPreset = preset ?? ExePresets.First();
            var quotedFile = $"\"{setupFileName}\"";

            return new CommandSuggestion
            {
                InstallCommand = string.IsNullOrWhiteSpace(selectedPreset.InstallArguments)
                    ? quotedFile
                    : $"{quotedFile} {selectedPreset.InstallArguments}".Trim(),
                UninstallCommand = string.IsNullOrWhiteSpace(selectedPreset.UninstallArguments)
                    ? $"{quotedFile} /uninstall"
                    : $"{quotedFile} {selectedPreset.UninstallArguments}".Trim()
            };
        }

        return new CommandSuggestion();
    }
}
