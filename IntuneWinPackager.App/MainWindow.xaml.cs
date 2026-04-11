using IntuneWinPackager.App.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace IntuneWinPackager.App;

public partial class MainWindow : System.Windows.Window
{
    private static readonly WpfBrush DropDefaultBorder = new WpfSolidColorBrush(WpfColor.FromRgb(184, 202, 230));
    private static readonly WpfBrush DropActiveBorder = new WpfSolidColorBrush(WpfColor.FromRgb(30, 94, 168));
    private static readonly WpfBrush DropDefaultBackground = new WpfSolidColorBrush(WpfColor.FromRgb(245, 249, 255));
    private static readonly WpfBrush DropActiveBackground = new WpfSolidColorBrush(WpfColor.FromRgb(234, 244, 255));

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
        await _viewModel.InitializeAsync();
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

        DropZone.BorderBrush = isActive ? DropActiveBorder : DropDefaultBorder;
        DropZone.Background = isActive ? DropActiveBackground : DropDefaultBackground;
    }
}
