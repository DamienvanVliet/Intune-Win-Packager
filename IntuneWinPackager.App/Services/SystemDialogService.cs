using System.Diagnostics;
using System.IO;
using WinForms = System.Windows.Forms;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace IntuneWinPackager.App.Services;

public sealed class SystemDialogService : IDialogService
{
    public string? PickInstallerFile(string? initialDirectory = null)
    {
        var dialog = new WpfOpenFileDialog
        {
            Filter = "Installer Files (*.msi;*.exe)|*.msi;*.exe|MSI files (*.msi)|*.msi|EXE files (*.exe)|*.exe",
            Title = "Select setup installer",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = NormalizeInitialDirectory(initialDirectory)
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExecutableFile(string? initialDirectory = null)
    {
        var dialog = new WpfOpenFileDialog
        {
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Locate IntuneWinAppUtil.exe",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = NormalizeInitialDirectory(initialDirectory)
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string? initialDirectory = null)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = NormalizeInitialDirectory(initialDirectory)
        };

        return dialog.ShowDialog() == WinForms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath}\"",
            UseShellExecute = true
        });
    }

    private static string NormalizeInitialDirectory(string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            return initialDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }
}
