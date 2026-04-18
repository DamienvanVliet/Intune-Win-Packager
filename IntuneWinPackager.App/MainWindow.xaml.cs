using IntuneWinPackager.App.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;

namespace IntuneWinPackager.App;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Startup failed: {ex.Message}\n\nThe app will stay open in safe mode. Check the Packaging Logs panel for details.",
                "Intune Win Packager",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void DropZone_DragEnter(object sender, WpfDragEventArgs e)
    {
        SetDropZoneVisualState(true);
        SetDropEffects(e);
    }

    private void DropZone_DragOver(object sender, WpfDragEventArgs e)
    {
        SetDropEffects(e);
    }

    private void DropZone_DragLeave(object sender, WpfDragEventArgs e)
    {
        SetDropZoneVisualState(false);
    }

    private async void DropZone_Drop(object sender, WpfDragEventArgs e)
    {
        SetDropZoneVisualState(false);

        if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(WpfDataFormats.FileDrop) is not string[] droppedPaths || droppedPaths.Length == 0)
        {
            return;
        }

        await _viewModel.HandleFileDropAsync(droppedPaths);
    }

    private static void SetDropEffects(WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(WpfDataFormats.FileDrop)
            ? WpfDragDropEffects.Copy
            : WpfDragDropEffects.None;

        e.Handled = true;
    }

    private void SetDropZoneVisualState(bool isActive)
    {
        _viewModel.SetDragOverState(isActive);

        DropZone.BorderBrush = ResolveBrush(isActive ? "DropZoneActiveBorderBrush" : "DropZoneBorderBrush", DropZone.BorderBrush);
        DropZone.Background = ResolveBrush(isActive ? "DropZoneActiveBackgroundBrush" : "DropZoneBackgroundBrush", DropZone.Background);
    }

    private WpfBrush ResolveBrush(string key, WpfBrush fallback)
    {
        if (TryFindResource(key) is WpfBrush brush)
        {
            return brush;
        }

        return fallback;
    }
}
