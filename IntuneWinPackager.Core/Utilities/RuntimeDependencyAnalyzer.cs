using System.Text;
using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Utilities;

public static class RuntimeDependencyAnalyzer
{
    private const int MaxFilesToScan = 80;
    private const int MaxImportNameLength = 260;

    private static readonly HashSet<string> VisualCppRuntimeDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "concrt140.dll",
        "mfc140.dll",
        "mfc140u.dll",
        "mfcm140.dll",
        "mfcm140u.dll",
        "vcamp140.dll",
        "vcomp140.dll"
    };

    public static RuntimeDependencyAnalysis Analyze(string? setupFilePath, string? sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            return new RuntimeDependencyAnalysis
            {
                Summary = "Source folder is missing; runtime dependency analysis was skipped."
            };
        }

        var sourceRoot = Path.GetFullPath(sourceFolder);
        var files = EnumerateCandidateFiles(sourceRoot, setupFilePath).ToList();
        var sourceDllNames = Directory
            .EnumerateFiles(sourceRoot, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var importedRuntimeDlls = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var analyzedFiles = new List<string>();

        foreach (var file in files)
        {
            var imports = TryReadImportedDllNames(file);
            if (imports.Count == 0)
            {
                imports = TryFindRuntimeDllStrings(file);
            }

            if (imports.Count == 0)
            {
                continue;
            }

            analyzedFiles.Add(ToRelativePath(sourceRoot, file));
            foreach (var import in imports)
            {
                if (VisualCppRuntimeDlls.Contains(import))
                {
                    importedRuntimeDlls.Add(import);
                }
            }
        }

        var missingRuntimeDlls = importedRuntimeDlls
            .Where(import => !sourceDllNames.Contains(import))
            .ToArray();
        var hasRedist = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Any(IsVisualCppRedistributableInstaller);
        var requiresVcpp = importedRuntimeDlls.Count > 0;
        var hasRuntimeFiles = missingRuntimeDlls.Length == 0 && requiresVcpp;

        return new RuntimeDependencyAnalysis
        {
            RequiresVisualCppRuntime = requiresVcpp,
            HasVisualCppRuntimeFiles = hasRuntimeFiles,
            HasVisualCppRedistributableInstaller = hasRedist,
            ImportedRuntimeDlls = importedRuntimeDlls.ToArray(),
            MissingRuntimeDlls = missingRuntimeDlls,
            AnalyzedFiles = analyzedFiles,
            Summary = BuildSummary(requiresVcpp, hasRuntimeFiles, hasRedist, missingRuntimeDlls)
        };
    }

    public static bool HasBlockingMissingRuntime(RuntimeDependencyAnalysis analysis)
    {
        return analysis.RequiresVisualCppRuntime &&
               analysis.MissingRuntimeDlls.Count > 0 &&
               !analysis.HasVisualCppRedistributableInstaller;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string sourceRoot, string? setupFilePath)
    {
        if (!string.IsNullOrWhiteSpace(setupFilePath) && File.Exists(setupFilePath))
        {
            yield return Path.GetFullPath(setupFilePath);
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .Where(static file =>
                     {
                         var extension = Path.GetExtension(file);
                         return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
                     })
                     .Take(MaxFilesToScan))
        {
            yield return file;
        }
    }

    private static IReadOnlyList<string> TryReadImportedDllNames(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
            if (stream.Length < 0x100)
            {
                return [];
            }

            if (reader.ReadUInt16() != 0x5A4D)
            {
                return [];
            }

            stream.Position = 0x3C;
            var peHeaderOffset = reader.ReadInt32();
            if (peHeaderOffset <= 0 || peHeaderOffset + 0x108 >= stream.Length)
            {
                return [];
            }

            stream.Position = peHeaderOffset;
            if (reader.ReadUInt32() != 0x00004550)
            {
                return [];
            }

            var machine = reader.ReadUInt16();
            _ = machine;
            var sectionCount = reader.ReadUInt16();
            stream.Position += 12;
            var optionalHeaderSize = reader.ReadUInt16();
            stream.Position += 2;

            var optionalHeaderStart = stream.Position;
            var magic = reader.ReadUInt16();
            var isPe32Plus = magic == 0x20B;
            var dataDirectoryStart = optionalHeaderStart + (isPe32Plus ? 112 : 96);
            if (dataDirectoryStart + 8 > optionalHeaderStart + optionalHeaderSize)
            {
                return [];
            }

            stream.Position = dataDirectoryStart + 8;
            var importDirectoryRva = reader.ReadUInt32();
            var importDirectorySize = reader.ReadUInt32();
            if (importDirectoryRva == 0 || importDirectorySize == 0)
            {
                return [];
            }

            stream.Position = optionalHeaderStart + optionalHeaderSize;
            var sections = new List<PeSection>();
            for (var index = 0; index < sectionCount; index++)
            {
                if (stream.Position + 40 > stream.Length)
                {
                    return [];
                }

                stream.Position += 8;
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var rawPointer = reader.ReadUInt32();
                stream.Position += 16;

                sections.Add(new PeSection(virtualAddress, Math.Max(virtualSize, rawSize), rawPointer));
            }

            var importOffset = RvaToOffset(importDirectoryRva, sections);
            if (importOffset < 0)
            {
                return [];
            }

            var imports = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            stream.Position = importOffset;
            while (stream.Position + 20 <= stream.Length)
            {
                var nextDescriptorOffset = stream.Position + 20;
                var originalFirstThunk = reader.ReadUInt32();
                var timeDateStamp = reader.ReadUInt32();
                var forwarderChain = reader.ReadUInt32();
                var nameRva = reader.ReadUInt32();
                var firstThunk = reader.ReadUInt32();

                if (originalFirstThunk == 0 &&
                    timeDateStamp == 0 &&
                    forwarderChain == 0 &&
                    nameRva == 0 &&
                    firstThunk == 0)
                {
                    break;
                }

                var nameOffset = RvaToOffset(nameRva, sections);
                if (nameOffset >= 0 && nameOffset < stream.Length)
                {
                    var name = ReadNullTerminatedAscii(stream, nameOffset);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        imports.Add(name);
                    }
                }

                stream.Position = nextDescriptorOffset;
            }

            return imports.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> TryFindRuntimeDllStrings(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length <= 0 || fileInfo.Length > 256L * 1024L * 1024L)
            {
                return [];
            }

            var bytes = File.ReadAllBytes(filePath);
            var ascii = Encoding.ASCII.GetString(bytes);
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var runtimeDll in VisualCppRuntimeDlls)
            {
                if (ascii.Contains(runtimeDll, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(runtimeDll);
                }
            }

            return found.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static long RvaToOffset(uint rva, IReadOnlyList<PeSection> sections)
    {
        foreach (var section in sections)
        {
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.VirtualSize)
            {
                return section.RawPointer + (rva - section.VirtualAddress);
            }
        }

        return -1;
    }

    private static string ReadNullTerminatedAscii(Stream stream, long offset)
    {
        stream.Position = offset;
        var bytes = new List<byte>();
        while (stream.Position < stream.Length && bytes.Count < MaxImportNameLength)
        {
            var value = stream.ReadByte();
            if (value <= 0)
            {
                break;
            }

            bytes.Add((byte)value);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static bool IsVisualCppRedistributableInstaller(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.Contains("vc_redist", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vcredist", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("visualcpp", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("visual-cpp", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToRelativePath(string sourceRoot, string filePath)
    {
        try
        {
            return Path.GetRelativePath(sourceRoot, filePath);
        }
        catch
        {
            return Path.GetFileName(filePath);
        }
    }

    private static string BuildSummary(
        bool requiresVcpp,
        bool hasRuntimeFiles,
        bool hasRedist,
        IReadOnlyList<string> missingRuntimeDlls)
    {
        if (!requiresVcpp)
        {
            return "No Visual C++ runtime imports were detected in source executables.";
        }

        if (hasRuntimeFiles)
        {
            return "Visual C++ runtime imports were detected and matching DLLs are present in the source folder.";
        }

        if (hasRedist)
        {
            return "Visual C++ runtime imports were detected and a VC++ redistributable installer is present in the source folder.";
        }

        return "Visual C++ runtime imports were detected, but required runtime DLLs are missing from the source folder: " +
               string.Join(", ", missingRuntimeDlls);
    }

    private sealed record PeSection(uint VirtualAddress, uint VirtualSize, uint RawPointer);
}
