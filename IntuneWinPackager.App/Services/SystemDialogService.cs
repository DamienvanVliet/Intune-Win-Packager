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
            Filter = "Setup Packages (*.msi;*.exe;*.appx;*.appxbundle;*.msix;*.msixbundle;*.ps1;*.cmd;*.bat;*.vbs;*.wsf)|*.msi;*.exe;*.appx;*.appxbundle;*.msix;*.msixbundle;*.ps1;*.cmd;*.bat;*.vbs;*.wsf|MSI files (*.msi)|*.msi|EXE files (*.exe)|*.exe|APPX/MSIX files (*.appx;*.appxbundle;*.msix;*.msixbundle)|*.appx;*.appxbundle;*.msix;*.msixbundle|Script files (*.ps1;*.cmd;*.bat;*.vbs;*.wsf)|*.ps1;*.cmd;*.bat;*.vbs;*.wsf",
            Title = "Select setup package",
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
