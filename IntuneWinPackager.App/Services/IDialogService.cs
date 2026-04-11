namespace IntuneWinPackager.App.Services;

public interface IDialogService
{
    string? PickInstallerFile(string? initialDirectory = null);

    string? PickExecutableFile(string? initialDirectory = null);

    string? PickFolder(string? initialDirectory = null);

    void OpenFolder(string folderPath);
}
