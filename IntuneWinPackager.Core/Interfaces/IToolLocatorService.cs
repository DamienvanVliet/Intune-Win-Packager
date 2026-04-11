namespace IntuneWinPackager.Core.Interfaces;

public interface IToolLocatorService
{
    string? LocateToolPath();

    IReadOnlyList<string> GetCandidatePaths();
}
