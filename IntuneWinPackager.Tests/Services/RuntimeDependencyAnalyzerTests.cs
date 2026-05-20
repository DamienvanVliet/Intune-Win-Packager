using IntuneWinPackager.Core.Utilities;

namespace IntuneWinPackager.Tests.Services;

public sealed class RuntimeDependencyAnalyzerTests
{
    [Fact]
    public void Analyze_FlagsMissingVisualCppRuntime_WhenImportTableReferencesVcruntime()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var exePath = Path.Combine(tempRoot, "SampleApp.exe");
        File.WriteAllBytes(exePath, BuildPeWithImports("VCRUNTIME140.dll", "MSVCP140.dll"));

        try
        {
            var analysis = RuntimeDependencyAnalyzer.Analyze(exePath, tempRoot);

            Assert.True(analysis.RequiresVisualCppRuntime);
            Assert.Contains("VCRUNTIME140.dll", analysis.ImportedRuntimeDlls, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("MSVCP140.dll", analysis.MissingRuntimeDlls, StringComparer.OrdinalIgnoreCase);
            Assert.True(RuntimeDependencyAnalyzer.HasBlockingMissingRuntime(analysis));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Analyze_Passes_WhenMatchingRuntimeDllsExistInSource()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var exePath = Path.Combine(tempRoot, "SampleApp.exe");
        File.WriteAllBytes(exePath, BuildPeWithImports("VCRUNTIME140.dll"));
        File.WriteAllBytes(Path.Combine(tempRoot, "VCRUNTIME140.dll"), [1, 2, 3]);

        try
        {
            var analysis = RuntimeDependencyAnalyzer.Analyze(exePath, tempRoot);

            Assert.True(analysis.RequiresVisualCppRuntime);
            Assert.Empty(analysis.MissingRuntimeDlls);
            Assert.False(RuntimeDependencyAnalyzer.HasBlockingMissingRuntime(analysis));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Analyze_Passes_WhenVcRedistInstallerExistsInSource()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var exePath = Path.Combine(tempRoot, "SampleApp.exe");
        File.WriteAllBytes(exePath, BuildPeWithImports("VCRUNTIME140.dll"));
        File.WriteAllBytes(Path.Combine(tempRoot, "vc_redist.x86.exe"), [1, 2, 3]);

        try
        {
            var analysis = RuntimeDependencyAnalyzer.Analyze(exePath, tempRoot);

            Assert.True(analysis.RequiresVisualCppRuntime);
            Assert.True(analysis.HasVisualCppRedistributableInstaller);
            Assert.False(RuntimeDependencyAnalyzer.HasBlockingMissingRuntime(analysis));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Analyze_FlagsMissingVisualCppRuntime_WhenInstallerContainsRuntimeDllStrings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var exePath = Path.Combine(tempRoot, "Setup.exe");
        File.WriteAllText(exePath, "embedded payload mentions MSVCP140.dll and VCRUNTIME140.dll");

        try
        {
            var analysis = RuntimeDependencyAnalyzer.Analyze(exePath, tempRoot);

            Assert.True(analysis.RequiresVisualCppRuntime);
            Assert.Contains("MSVCP140.dll", analysis.MissingRuntimeDlls, StringComparer.OrdinalIgnoreCase);
            Assert.True(RuntimeDependencyAnalyzer.HasBlockingMissingRuntime(analysis));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Analyze_DetectsWebView2Signals_WhenInstallerMentionsWebView2()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"iwp-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var exePath = Path.Combine(tempRoot, "Setup.exe");
        File.WriteAllText(exePath, "embedded payload mentions WebView2Loader.dll and Microsoft.Web.WebView2");

        try
        {
            var analysis = RuntimeDependencyAnalyzer.Analyze(exePath, tempRoot);

            Assert.True(analysis.RequiresWebView2Runtime);
            Assert.False(analysis.HasWebView2RuntimeInstaller);
            Assert.Contains("webview2loader.dll", analysis.DetectedWebView2Signals, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static byte[] BuildPeWithImports(params string[] importNames)
    {
        using var stream = new MemoryStream(new byte[4096], writable: true);
        using var writer = new BinaryWriter(stream);

        stream.Position = 0;
        writer.Write((ushort)0x5A4D);
        stream.Position = 0x3C;
        writer.Write(0x80);

        stream.Position = 0x80;
        writer.Write(0x00004550u);
        writer.Write((ushort)0x014C);
        writer.Write((ushort)1);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0xE0);
        writer.Write((ushort)0x010F);

        var optionalHeaderStart = stream.Position;
        writer.Write((ushort)0x010B);
        stream.Position = optionalHeaderStart + 96 + 8;
        writer.Write(0x2000u);
        writer.Write(0x200u);

        stream.Position = optionalHeaderStart + 0xE0;
        WriteAsciiPadded(writer, ".rdata", 8);
        writer.Write(0x1000u);
        writer.Write(0x2000u);
        writer.Write(0x1000u);
        writer.Write(0x200u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0x40000040u);

        stream.Position = 0x200;
        var nameRvas = new List<uint>();
        var namesOffset = 0x200 + ((importNames.Length + 1) * 20);
        foreach (var importName in importNames)
        {
            nameRvas.Add((uint)(0x2000 + (namesOffset - 0x200)));
            stream.Position = namesOffset;
            writer.Write(System.Text.Encoding.ASCII.GetBytes(importName));
            writer.Write((byte)0);
            namesOffset = (int)stream.Position;
        }

        stream.Position = 0x200;
        foreach (var nameRva in nameRvas)
        {
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(nameRva);
            writer.Write(0u);
        }

        writer.Write(new byte[20]);

        return stream.ToArray();
    }

    private static void WriteAsciiPadded(BinaryWriter writer, string value, int length)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        writer.Write(bytes);
        writer.Write(new byte[length - bytes.Length]);
    }
}
