namespace IntuneWinPackager.Models.Entities;

public sealed record MsiMetadata
{
    public string ProductName { get; init; } = string.Empty;

    public string ProductCode { get; init; } = string.Empty;

    public string ProductVersion { get; init; } = string.Empty;

    public string Manufacturer { get; init; } = string.Empty;

    public string? InspectionWarning { get; init; }
}
