namespace IntuneWinPackager.Models.Entities;

public sealed record ValidationResult
{
    public List<string> Errors { get; init; } = new();

    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success() => new();

    public static ValidationResult FromErrors(IEnumerable<string> errors)
    {
        return new ValidationResult
        {
            Errors = errors.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList()
        };
    }
}
