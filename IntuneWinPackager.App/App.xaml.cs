using IntuneWinPackager.App.Services;
using IntuneWinPackager.App.ViewModels;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Core.Services;
using IntuneWinPackager.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IntuneWinPackager.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IValidationService, PackagingValidationService>();
                services.AddSingleton<IInstallerCommandService, InstallerCommandService>();
                services.AddSingleton<IPackagingWorkflowService, PackagingWorkflowService>();
                services.AddSingleton<IPreflightService, PreflightService>();

                services.AddSingleton<IProcessRunner, ProcessRunnerService>();
                services.AddSingleton<IMsiInspectorService, MsiInspectorService>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<IProfileService, ProfileService>();
                services.AddSingleton<IHistoryService, HistoryService>();
                services.AddSingleton<IToolLocatorService, ToolLocatorService>();
                services.AddSingleton<IToolInstallerService, ToolInstallerService>();
                services.AddSingleton<IAppUpdateService, AppUpdateService>();

                services.AddSingleton<IDialogService, SystemDialogService>();
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IThemeService, ThemeService>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
