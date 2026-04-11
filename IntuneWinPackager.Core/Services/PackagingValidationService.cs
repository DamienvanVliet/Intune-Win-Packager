using System.IO;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Core.Services;

public sealed class PackagingValidationService : IValidationService
{
    public ValidationResult Validate(PackagingRequest request)
    {
        var errors = new List<string>();

        if (request is null)
        {
            errors.Add("Packaging request is required.");
            return ValidationResult.FromErrors(errors);
        }

        if (string.IsNullOrWhiteSpace(request.IntuneWinAppUtilPath))
        {
            errors.Add("Path to IntuneWinAppUtil.exe is required.");
        }
        else if (!File.Exists(request.IntuneWinAppUtilPath))
        {
            errors.Add("IntuneWinAppUtil.exe was not found at the configured path.");
        }

        var config = request.Configuration;

        if (string.IsNullOrWhiteSpace(config.SourceFolder))
        {
            errors.Add("Source folder is required.");
        }
        else if (!Directory.Exists(config.SourceFolder))
        {
            errors.Add("Selected source folder does not exist.");
        }

        if (string.IsNullOrWhiteSpace(config.SetupFilePath))
        {
            errors.Add("Setup file is required.");
        }
        else if (!File.Exists(config.SetupFilePath))
        {
            errors.Add("Selected setup file was not found.");
        }

        var extension = Path.GetExtension(config.SetupFilePath ?? string.Empty);
        if (!string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Only .msi and .exe setup files are supported.");
        }

        if (request.InstallerType == InstallerType.Unknown)
        {
            errors.Add("Installer type could not be determined from the selected file.");
        }

        if (string.IsNullOrWhiteSpace(config.OutputFolder))
        {
            errors.Add("Output folder is required.");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(config.OutputFolder);
            }
            catch (Exception)
            {
                errors.Add("Output folder path is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.SourceFolder) && !string.IsNullOrWhiteSpace(config.SetupFilePath))
        {
            if (!IsPathInsideFolder(config.SetupFilePath, config.SourceFolder))
            {
                errors.Add("Setup file must be inside the selected source folder.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.InstallCommand))
        {
            errors.Add("Install command is required.");
        }

        if (string.IsNullOrWhiteSpace(config.UninstallCommand))
        {
            errors.Add("Uninstall command is required.");
        }

        return ValidationResult.FromErrors(errors);
    }

    private static bool IsPathInsideFolder(string filePath, string folderPath)
    {
        var folderFullPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var fileFullPath = Path.GetFullPath(filePath);

        return fileFullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase);
    }
}
