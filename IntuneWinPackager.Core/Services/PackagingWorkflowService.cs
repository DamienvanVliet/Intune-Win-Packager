using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;
using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Core.Services;

public sealed class PackagingWorkflowService : IPackagingWorkflowService
{
    private const int MaxStagingCopyParallelism = 8;

    private static readonly JsonSerializerOptions MetadataSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IValidationService _validationService;
    private readonly IProcessRunner _processRunner;

    public PackagingWorkflowService(
        IValidationService validationService,
        IProcessRunner processRunner)
    {
        _validationService = validationService;
        _processRunner = processRunner;
    }

    public async Task<PackagingResult> PackageAsync(
        PackagingRequest request,
        IProgress<string>? logProgress = null,
        IProgress<PackagingProgressUpdate>? progressUpdate = null,
        CancellationToken cancellationToken = default)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var lastReportedProgress = 0d;

        void ReportProgress(double percentage, string step, string detail, bool isIndeterminate = false)
        {
            var normalized = Math.Clamp(percentage, 0, 100);
            if (!isIndeterminate)
            {
                normalized = Math.Max(normalized, lastReportedProgress);
                lastReportedProgress = normalized;
            }

            progressUpdate?.Report(new PackagingProgressUpdate
            {
                Percentage = normalized,
                IsIndeterminate = isIndeterminate,
                Step = step,
                Detail = detail
            });
        }

        ReportProgress(5, "Validating Input", "Checking source, setup, output, and Intune rules.");

        var validation = _validationService.Validate(request);
        if (!validation.IsValid)
        {
            ReportProgress(100, "Validation Failed", "Resolve input errors before packaging.");

            return new PackagingResult
            {
                Success = false,
                Message = string.Join(Environment.NewLine, validation.Errors),
                ExitCode = -1,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        ReportProgress(15, "Preparing Output", "Ensuring output folder exists.");
        Directory.CreateDirectory(request.Configuration.OutputFolder);

        if (request.UseLowImpactMode)
        {
            ReportProgress(18, "Performance Mode", "Low impact mode is enabled to keep your PC responsive.");
            logProgress?.Report("Low impact mode enabled: packaging process priority will be reduced.");
        }

        PreparedSourceContext? preparedSource = null;

        try
        {
            ReportProgress(20, "Preparing Source", "Evaluating source optimization and setup path.");
            var sourcePreparationStopwatch = Stopwatch.StartNew();
            preparedSource = PrepareSourceContext(request, startedAtUtc, logProgress, cancellationToken);
            sourcePreparationStopwatch.Stop();

            logProgress?.Report($"Source preparation completed in {sourcePreparationStopwatch.Elapsed.TotalSeconds:0.00}s.");

            var arguments = $"-c {Quote(preparedSource.PackagingSourceFolder)} -s {Quote(preparedSource.SetupRelativePath)} -o {Quote(request.Configuration.OutputFolder)} -q";

            ReportProgress(28, "Starting Packager", "Launching IntuneWinAppUtil.exe.");
            logProgress?.Report($"Starting packaging for {Path.GetFileName(preparedSource.SetupRelativePath)}");
            logProgress?.Report($"Running: {Path.GetFileName(request.IntuneWinAppUtilPath)} {arguments}");

            var packagingStopwatch = Stopwatch.StartNew();
            var processResult = await _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = request.IntuneWinAppUtilPath,
                    Arguments = arguments,
                    WorkingDirectory = preparedSource.PackagingSourceFolder,
                    PreferLowImpact = request.UseLowImpactMode
                },
                new Progress<ProcessOutputLine>(line =>
                {
                    if (!string.IsNullOrWhiteSpace(line.Text))
                    {
                        logProgress?.Report(line.Text);
                        TryReportProgressFromToolOutput(line.Text, ReportProgress);
                    }
                }),
                cancellationToken);
            packagingStopwatch.Stop();

            logProgress?.Report($"Packaging process finished in {packagingStopwatch.Elapsed.TotalSeconds:0.00}s (exit code {processResult.ExitCode}).");

            ReportProgress(92, "Resolving Output", "Locating generated .intunewin package.");
            var outputPackagePath = ResolveOutputPackagePath(
                request.Configuration.OutputFolder,
                Path.GetFileName(preparedSource.SetupRelativePath),
                startedAtUtc);

            var completedAtUtc = DateTimeOffset.UtcNow;
            if (processResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(outputPackagePath))
            {
                var metadataPath = await WriteMetadataFileAsync(
                    request,
                    preparedSource,
                    outputPackagePath,
                    logProgress,
                    cancellationToken);

                var checklist = BuildIntunePortalChecklist(request, outputPackagePath, metadataPath);
                var checklistPath = await WriteChecklistFileAsync(
                    outputPackagePath,
                    checklist,
                    logProgress,
                    cancellationToken);

                ReportProgress(100, "Completed", "Packaging completed successfully.");

                return new PackagingResult
                {
                    Success = true,
                    Message = "Packaging completed successfully. Intune preparation artifacts exported.",
                    OutputPackagePath = outputPackagePath,
                    OutputMetadataPath = metadataPath,
                    OutputChecklistPath = checklistPath,
                    IntunePortalChecklist = checklist,
                    ExitCode = processResult.ExitCode,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc
                };
            }

            var failureMessage = processResult.ExitCode == 0
                ? "IntuneWinAppUtil finished but no .intunewin file was found in the output folder."
                : $"Packaging failed with exit code {processResult.ExitCode}.";

            ReportProgress(100, "Failed", failureMessage);

            return new PackagingResult
            {
                Success = false,
                Message = failureMessage,
                OutputPackagePath = outputPackagePath,
                ExitCode = processResult.ExitCode,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc
            };
        }
        finally
        {
            if (preparedSource?.StagingRootFolder is not null)
            {
                TryCleanupStaging(preparedSource.StagingRootFolder, logProgress);
            }
        }
    }

    private static PreparedSourceContext PrepareSourceContext(
        PackagingRequest request,
        DateTimeOffset startedAtUtc,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var sourceFolder = Path.GetFullPath(request.Configuration.SourceFolder);
        var setupFilePath = Path.GetFullPath(request.Configuration.SetupFilePath);
        var setupRelativePath = Path.GetRelativePath(sourceFolder, setupFilePath);

        if (setupRelativePath.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Setup file must be inside the selected source folder.");
        }

        var outputFolder = Path.GetFullPath(request.Configuration.OutputFolder);
        var outputInsideSource = IsPathInsideFolder(outputFolder, sourceFolder);
        var sourceContainsPackages =
            request.Configuration.UseSmartSourceStaging &&
            !outputInsideSource &&
            SourceContainsPackageArtifacts(sourceFolder);

        var shouldStage = request.Configuration.UseSmartSourceStaging &&
            (outputInsideSource || sourceContainsPackages);

        if (!shouldStage)
        {
            return new PreparedSourceContext
            {
                PackagingSourceFolder = sourceFolder,
                SetupRelativePath = setupRelativePath,
                StagingRootFolder = null,
                StagedFileCount = 0,
                StagedBytes = 0
            };
        }

        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "IntuneWinPackager",
            "staging",
            startedAtUtc.ToUnixTimeMilliseconds().ToString(),
            Guid.NewGuid().ToString("N"));
        var stagingSource = Path.Combine(stagingRoot, "source");
        Directory.CreateDirectory(stagingSource);

        logProgress?.Report(outputInsideSource
            ? "Smart staging enabled: output folder is inside source."
            : "Smart staging enabled: previous .intunewin artifacts detected in source.");

        var copiedFileCount = 0;
        long copiedBytes = 0;
        var hardLinkedFileCount = 0;
        var copyParallelism = DetermineStagingCopyParallelism(request.UseLowImpactMode);
        var preferHardLinks = CanUseHardLinkStaging(sourceFolder, stagingSource);

        if (preferHardLinks)
        {
            logProgress?.Report("Smart staging optimization: using hard links where possible for faster source preparation.");
        }

        CopyDirectoryTree(
            sourceFolder,
            stagingSource,
            outputInsideSource ? outputFolder : null,
            preferHardLinks,
            copyParallelism,
            ref copiedFileCount,
            ref hardLinkedFileCount,
            ref copiedBytes,
            cancellationToken);

        var copiedFileOnlyCount = Math.Max(0, copiedFileCount - hardLinkedFileCount);
        logProgress?.Report(
            $"Staged {copiedFileCount} file(s), {FormatBytes(copiedBytes)} prepared for isolated source " +
            $"(hard-linked: {hardLinkedFileCount}, copied: {copiedFileOnlyCount}, parallelism: {copyParallelism}).");

        return new PreparedSourceContext
        {
            PackagingSourceFolder = stagingSource,
            SetupRelativePath = setupRelativePath,
            StagingRootFolder = stagingRoot,
            StagedFileCount = copiedFileCount,
            HardLinkedFileCount = hardLinkedFileCount,
            StagedBytes = copiedBytes
        };
    }

    private static int DetermineStagingCopyParallelism(bool useLowImpactMode)
    {
        if (useLowImpactMode)
        {
            return 1;
        }

        var logicalCpuCount = Environment.ProcessorCount;
        if (logicalCpuCount <= 2)
        {
            return 2;
        }

        return Math.Clamp(logicalCpuCount / 2, 2, MaxStagingCopyParallelism);
    }

    private static void CopyDirectoryTree(
        string sourceFolder,
        string destinationFolder,
        string? excludedOutputFolder,
        bool preferHardLinks,
        int copyParallelism,
        ref int copiedFileCount,
        ref int hardLinkedFileCount,
        ref long copiedBytes,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<(string Source, string Destination)>();
        stack.Push((sourceFolder, destinationFolder));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = stack.Pop();
            Directory.CreateDirectory(current.Destination);

            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                if (ShouldSkipDirectory(directory, excludedOutputFolder))
                {
                    continue;
                }

                var targetDirectory = Path.Combine(current.Destination, Path.GetFileName(directory));
                stack.Push((directory, targetDirectory));
            }

            var filesToCopy = new List<string>();
            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                if (ShouldSkipFile(file))
                {
                    continue;
                }

                filesToCopy.Add(file);
            }

            if (filesToCopy.Count == 0)
            {
                continue;
            }

            if (copyParallelism <= 1 || filesToCopy.Count == 1)
            {
                foreach (var file in filesToCopy)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var destinationFile = Path.Combine(current.Destination, Path.GetFileName(file));
                    var stagedWithHardLink = TryStageFile(file, destinationFile, preferHardLinks);
                    copiedFileCount++;
                    if (stagedWithHardLink)
                    {
                        hardLinkedFileCount++;
                    }

                    copiedBytes += new FileInfo(file).Length;
                }

                continue;
            }

            var copiedInDirectory = 0;
            var hardLinkedInDirectory = 0;
            long copiedBytesInDirectory = 0;
            Parallel.ForEach(
                filesToCopy,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = copyParallelism,
                    CancellationToken = cancellationToken
                },
                file =>
                {
                    var destinationFile = Path.Combine(current.Destination, Path.GetFileName(file));
                    var stagedWithHardLink = TryStageFile(file, destinationFile, preferHardLinks);

                    Interlocked.Increment(ref copiedInDirectory);
                    if (stagedWithHardLink)
                    {
                        Interlocked.Increment(ref hardLinkedInDirectory);
                    }

                    Interlocked.Add(ref copiedBytesInDirectory, new FileInfo(file).Length);
                });

            copiedFileCount += copiedInDirectory;
            hardLinkedFileCount += hardLinkedInDirectory;
            copiedBytes += copiedBytesInDirectory;
        }
    }

    private static bool ShouldSkipDirectory(string directoryPath, string? excludedOutputFolder)
    {
        if (string.IsNullOrWhiteSpace(excludedOutputFolder))
        {
            return false;
        }

        var normalizedDirectory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedExcluded = Path.GetFullPath(excludedOutputFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedDirectory.Equals(normalizedExcluded, StringComparison.OrdinalIgnoreCase) ||
               normalizedDirectory.StartsWith(
                   normalizedExcluded + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".intunewin", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".intune.json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(filePath).EndsWith(".intune-checklist.md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanUseHardLinkStaging(string sourceFolder, string destinationFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return IsSameVolume(sourceFolder, destinationFolder);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStageFile(string sourceFile, string destinationFile, bool preferHardLinks)
    {
        if (preferHardLinks && TryCreateHardLink(destinationFile, sourceFile))
        {
            return true;
        }

        File.Copy(sourceFile, destinationFile, overwrite: true);
        return false;
    }

    private static bool IsSameVolume(string leftPath, string rightPath)
    {
        var leftRoot = Path.GetPathRoot(Path.GetFullPath(leftPath));
        var rightRoot = Path.GetPathRoot(Path.GetFullPath(rightPath));
        return !string.IsNullOrWhiteSpace(leftRoot) &&
               !string.IsNullOrWhiteSpace(rightRoot) &&
               leftRoot.Equals(rightRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return CreateHardLinkNative(linkPath, existingPath, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkNative(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private static async Task<string?> WriteMetadataFileAsync(
        PackagingRequest request,
        PreparedSourceContext preparedSource,
        string outputPackagePath,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.ChangeExtension(outputPackagePath, ".intune.json");

        var metadata = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            packageFile = Path.GetFileName(outputPackagePath),
            sourceFolder = request.Configuration.SourceFolder,
            setupFile = request.Configuration.SetupFilePath,
            setupRelativePath = preparedSource.SetupRelativePath,
            installerType = request.InstallerType.ToString(),
            installCommand = request.Configuration.InstallCommand,
            uninstallCommand = request.Configuration.UninstallCommand,
            intuneRules = request.Configuration.IntuneRules,
            packagingOptions = new
            {
                request.UseLowImpactMode,
                request.Configuration.UseSmartSourceStaging,
                usedIsolatedStaging = preparedSource.StagingRootFolder is not null,
                stagedFileCount = preparedSource.StagedFileCount,
                hardLinkedFileCount = preparedSource.HardLinkedFileCount,
                stagedBytes = preparedSource.StagedBytes
            }
        };

        try
        {
            await using var stream = File.Create(metadataPath);
            await JsonSerializer.SerializeAsync(stream, metadata, MetadataSerializerOptions, cancellationToken);
            logProgress?.Report($"Exported Intune metadata: {metadataPath}");
            return metadataPath;
        }
        catch (Exception ex)
        {
            logProgress?.Report($"Warning: package succeeded but metadata export failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> WriteChecklistFileAsync(
        string outputPackagePath,
        string checklist,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var checklistPath = Path.ChangeExtension(outputPackagePath, ".intune-checklist.md");
        try
        {
            await File.WriteAllTextAsync(checklistPath, checklist, cancellationToken);
            logProgress?.Report($"Exported Intune checklist: {checklistPath}");
            return checklistPath;
        }
        catch (Exception ex)
        {
            logProgress?.Report($"Warning: package succeeded but checklist export failed: {ex.Message}");
            return null;
        }
    }

    private static string BuildIntunePortalChecklist(
        PackagingRequest request,
        string outputPackagePath,
        string? metadataPath)
    {
        var rules = request.Configuration.IntuneRules;
        var requirements = rules.Requirements;
        var detectionSummary = BuildDetectionSummary(rules.DetectionRule, rules.AdditionalDetectionRules);

        var builder = new StringBuilder();
        builder.AppendLine("# Intune Win32 App - Manual Portal Steps");
        builder.AppendLine();
        builder.AppendLine("Use this checklist to create the app in Intune after generating the package.");
        builder.AppendLine();
        builder.AppendLine("## 1. App Package");
        builder.AppendLine($"- App type: Windows app (Win32)");
        builder.AppendLine($"- Select package file: `{outputPackagePath}`");
        if (!string.IsNullOrWhiteSpace(metadataPath))
        {
            builder.AppendLine($"- Optional reference metadata: `{metadataPath}`");
        }

        builder.AppendLine();
        builder.AppendLine("## 2. Program");
        builder.AppendLine($"- Install command: `{request.Configuration.InstallCommand}`");
        builder.AppendLine($"- Uninstall command: `{request.Configuration.UninstallCommand}`");
        builder.AppendLine($"- Install behavior: `{rules.InstallContext}`");
        builder.AppendLine($"- Device restart behavior: `{rules.RestartBehavior}`");
        builder.AppendLine($"- Max run time (minutes): `{rules.MaxRunTimeMinutes}`");

        builder.AppendLine();
        builder.AppendLine("## 3. Requirements");
        builder.AppendLine($"- OS architecture: `{requirements.OperatingSystemArchitecture}`");
        builder.AppendLine($"- Minimum OS: `{requirements.MinimumOperatingSystem}`");
        builder.AppendLine($"- Minimum free disk space (MB): `{FormatOptionalRequirement(requirements.MinimumFreeDiskSpaceMb)}`");
        builder.AppendLine($"- Minimum memory (MB): `{FormatOptionalRequirement(requirements.MinimumMemoryMb)}`");
        builder.AppendLine($"- Minimum CPU speed (MHz): `{FormatOptionalRequirement(requirements.MinimumCpuSpeedMhz)}`");
        builder.AppendLine($"- Minimum logical processors: `{FormatOptionalRequirement(requirements.MinimumLogicalProcessors)}`");

        if (!string.IsNullOrWhiteSpace(requirements.RequirementScriptBody))
        {
            builder.AppendLine("- Requirement script: configured below");
            builder.AppendLine("```powershell");
            builder.AppendLine(requirements.RequirementScriptBody.Trim());
            builder.AppendLine("```");
            builder.AppendLine($"- Requirement script 32-bit on 64-bit: `{requirements.RequirementScriptRunAs32BitOn64System}`");
            builder.AppendLine($"- Requirement script signature check: `{requirements.RequirementScriptEnforceSignatureCheck}`");
        }
        else
        {
            builder.AppendLine("- Requirement script: `Not configured`");
        }

        builder.AppendLine();
        builder.AppendLine("## 4. Detection Rules");
        builder.AppendLine(detectionSummary);

        builder.AppendLine();
        builder.AppendLine("## 5. Manual Verification Before Create");
        builder.AppendLine("- Confirm app info fields (name, description, publisher, version, icon).");
        builder.AppendLine("- Confirm assignments and availability scope.");
        builder.AppendLine("- Confirm return codes, dependencies, and supersedence where required.");
        builder.AppendLine("- Run pilot assignment before broad production rollout.");

        return builder.ToString().TrimEnd();
    }

    private static string FormatOptionalRequirement(int value)
    {
        return value > 0 ? value.ToString() : "Not configured";
    }

    private static string BuildDetectionSummary(
        IntuneDetectionRule detectionRule,
        IReadOnlyList<IntuneDetectionRule> additionalDetectionRules)
    {
        var primary = detectionRule.RuleType switch
        {
            IntuneDetectionRuleType.MsiProductCode =>
                $"- Type: `MSI`\n- Product code: `{detectionRule.Msi.ProductCode}`\n- Product version: `{(string.IsNullOrWhiteSpace(detectionRule.Msi.ProductVersion) ? "Not configured" : detectionRule.Msi.ProductVersion)}`\n- Version operator: `{detectionRule.Msi.ProductVersionOperator}`",

            IntuneDetectionRuleType.File =>
                $"- Type: `File`\n- Path: `{detectionRule.File.Path}`\n- File/Folder: `{detectionRule.File.FileOrFolderName}`\n- Operator: `{detectionRule.File.Operator}`\n- Value: `{(string.IsNullOrWhiteSpace(detectionRule.File.Value) ? "Not configured" : detectionRule.File.Value)}`\n- 32-bit on 64-bit: `{detectionRule.File.Check32BitOn64System}`",

            IntuneDetectionRuleType.Registry =>
                $"- Type: `Registry`\n- Hive: `{detectionRule.Registry.Hive}`\n- Key path: `{detectionRule.Registry.KeyPath}`\n- Value name: `{(string.IsNullOrWhiteSpace(detectionRule.Registry.ValueName) ? "Not configured" : detectionRule.Registry.ValueName)}`\n- Operator: `{detectionRule.Registry.Operator}`\n- Value: `{(string.IsNullOrWhiteSpace(detectionRule.Registry.Value) ? "Not configured" : detectionRule.Registry.Value)}`\n- 32-bit on 64-bit: `{detectionRule.Registry.Check32BitOn64System}`",

            IntuneDetectionRuleType.Script =>
                "- Type: `Script`\n- Script content: configured below\n```powershell\n" +
                detectionRule.Script.ScriptBody.Trim() +
                "\n```\n" +
                $"- 32-bit on 64-bit: `{detectionRule.Script.RunAs32BitOn64System}`\n" +
                $"- Signature check: `{detectionRule.Script.EnforceSignatureCheck}`",

            _ => "- Type: `Not configured`"
        };

        if (additionalDetectionRules is null || additionalDetectionRules.Count == 0)
        {
            return primary;
        }

        var builder = new StringBuilder(primary);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("- Additional detection rules (configure all in Intune):");
        for (var index = 0; index < additionalDetectionRules.Count; index++)
        {
            var rule = additionalDetectionRules[index];
            builder.AppendLine($"  - Rule {index + 1}: {SummarizeInlineRule(rule)}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string SummarizeInlineRule(IntuneDetectionRule detectionRule)
    {
        return detectionRule.RuleType switch
        {
            IntuneDetectionRuleType.MsiProductCode =>
                $"MSI ProductCode={detectionRule.Msi.ProductCode}, Version={detectionRule.Msi.ProductVersion}, Operator={detectionRule.Msi.ProductVersionOperator}",
            IntuneDetectionRuleType.File =>
                $"File Path={detectionRule.File.Path}, Name={detectionRule.File.FileOrFolderName}, Operator={detectionRule.File.Operator}, Value={detectionRule.File.Value}",
            IntuneDetectionRuleType.Registry =>
                $"Registry {detectionRule.Registry.Hive}\\{detectionRule.Registry.KeyPath} | {detectionRule.Registry.ValueName} {detectionRule.Registry.Operator} {detectionRule.Registry.Value}",
            IntuneDetectionRuleType.Script =>
                "Script detection rule (see metadata JSON for full script body).",
            _ => "Not configured"
        };
    }

    private static void TryCleanupStaging(string stagingRootFolder, IProgress<string>? logProgress)
    {
        try
        {
            if (Directory.Exists(stagingRootFolder))
            {
                Directory.Delete(stagingRootFolder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logProgress?.Report($"Warning: could not clean staging folder '{stagingRootFolder}': {ex.Message}");
        }
    }

    private static void TryReportProgressFromToolOutput(
        string line,
        Action<double, string, string, bool> reportProgress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var lowered = line.Trim().ToLowerInvariant();

        var percentMatch = Regex.Match(lowered, @"\b(?<p>\d{1,3})\s*%");
        if (percentMatch.Success && int.TryParse(percentMatch.Groups["p"].Value, out var parsedPercent))
        {
            var mapped = Math.Clamp(28 + (parsedPercent * 0.64), 28, 95);
            reportProgress(mapped, "Packaging", line.Trim(), false);
            return;
        }

        if (lowered.Contains("validating parameters"))
        {
            reportProgress(38, "Validating Parameters", line.Trim(), false);
            return;
        }

        if (lowered.Contains("validated parameters"))
        {
            reportProgress(45, "Parameters Validated", line.Trim(), false);
            return;
        }

        if (lowered.Contains("compressing the source folder"))
        {
            reportProgress(60, "Compressing Files", line.Trim(), false);
            return;
        }

        if (lowered.Contains("calculated size"))
        {
            reportProgress(68, "Analyzing Contents", line.Trim(), false);
            return;
        }

        if (lowered.Contains("encrypt"))
        {
            reportProgress(80, "Encrypting Package", line.Trim(), false);
            return;
        }

        if (lowered.Contains("catalog") || lowered.Contains("detection.xml") || lowered.Contains("manifest"))
        {
            reportProgress(89, "Finalizing Package", line.Trim(), false);
            return;
        }

        if (lowered.Contains("done") || lowered.Contains("completed"))
        {
            reportProgress(95, "Finishing Up", line.Trim(), false);
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string? ResolveOutputPackagePath(string outputFolder, string setupFileName, DateTimeOffset startedAtUtc)
    {
        var expectedPath = Path.Combine(outputFolder, $"{Path.GetFileNameWithoutExtension(setupFileName)}.intunewin");
        if (File.Exists(expectedPath))
        {
            return expectedPath;
        }

        if (!Directory.Exists(outputFolder))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(outputFolder, "*.intunewin", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.LastWriteTimeUtc >= startedAtUtc.UtcDateTime.AddMinutes(-1))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static bool IsPathInsideFolder(string fileOrFolderPath, string parentFolderPath)
    {
        var parentPath = Path.GetFullPath(parentFolderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(fileOrFolderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Equals(parentPath, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    private static bool SourceContainsPackageArtifacts(string sourceFolder)
    {
        try
        {
            return Directory
                .EnumerateFiles(sourceFolder, "*.intunewin", SearchOption.AllDirectories)
                .Any();
        }
        catch
        {
            return false;
        }
    }

    private sealed record PreparedSourceContext
    {
        public string PackagingSourceFolder { get; init; } = string.Empty;

        public string SetupRelativePath { get; init; } = string.Empty;

        public string? StagingRootFolder { get; init; }

        public int StagedFileCount { get; init; }

        public int HardLinkedFileCount { get; init; }

        public long StagedBytes { get; init; }
    }
}
