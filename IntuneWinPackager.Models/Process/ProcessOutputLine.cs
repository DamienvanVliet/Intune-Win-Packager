using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.Models.Process;

public sealed record ProcessOutputLine
{
    public DateTimeOffset TimestampUtc { get; init; }

    public LogSeverity Severity { get; init; } = LogSeverity.Info;

    public string Text { get; init; } = string.Empty;
}
