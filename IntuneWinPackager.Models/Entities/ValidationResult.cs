namespace IntuneWinPackager.Models.Entities;

public sealed record ValidationResult
{
    public List<ValidationIssue> Issues { get; init; } = new();

    public List<string> Errors => Issues
        .Where(issue => !string.IsNullOrWhiteSpace(issue.Message))
        .Select(issue => issue.Message)
        .Distinct()
        .ToList();

    public bool IsValid => Issues.Count == 0;

    public static ValidationResult Success() => new();

    public static ValidationResult FromErrors(IEnumerable<string> errors)
    {
        var issues = errors
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct()
            .Select(message => new ValidationIssue
            {
                Key = string.Empty,
                Message = message
            })
            .ToList();

        return new ValidationResult
        {
            Issues = issues
        };
    }

    public static ValidationResult FromIssues(IEnumerable<ValidationIssue> issues)
    {
        return new ValidationResult
        {
            Issues = issues
                .Where(issue => !string.IsNullOrWhiteSpace(issue.Message))
                .DistinctBy(issue => $"{issue.Key}|{issue.Message}")
                .ToList()
        };
    }
}
