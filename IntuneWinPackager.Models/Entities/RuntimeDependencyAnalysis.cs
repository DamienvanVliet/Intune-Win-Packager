namespace IntuneWinPackager.Models.Entities;

public sealed record RuntimeDependencyAnalysis
{
    public bool RequiresVisualCppRuntime { get; init; }

    public bool HasVisualCppRuntimeFiles { get; init; }

    public bool HasVisualCppRedistributableInstaller { get; init; }

    public bool RequiresWebView2Runtime { get; init; }

    public bool HasWebView2RuntimeInstaller { get; init; }

    public IReadOnlyList<string> ImportedRuntimeDlls { get; init; } = [];

    public IReadOnlyList<string> MissingRuntimeDlls { get; init; } = [];

    public IReadOnlyList<string> DetectedWebView2Signals { get; init; } = [];

    public IReadOnlyList<string> AnalyzedFiles { get; init; } = [];

    public string Summary { get; init; } = string.Empty;
}
