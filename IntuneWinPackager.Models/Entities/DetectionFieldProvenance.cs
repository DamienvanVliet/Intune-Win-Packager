using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Entities;

public sealed record DetectionFieldProvenance
{
    public string FieldName { get; init; } = string.Empty;

    public string FieldValue { get; init; } = string.Empty;

    public DetectionProvenanceSource Source { get; init; } = DetectionProvenanceSource.Unknown;

    public bool IsStrongEvidence { get; init; }

    public string Notes { get; init; } = string.Empty;
}

