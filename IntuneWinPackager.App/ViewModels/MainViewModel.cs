using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfDispatcherPriority = System.Windows.Threading.DispatcherPriority;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IntuneWinPackager.App.Services;
using IntuneWinPackager.Core.Interfaces;
using IntuneWinPackager.Models.Entities;
using IntuneWinPackager.Models.Enums;

namespace IntuneWinPackager.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IPackagingWorkflowService _packagingWorkflowService;
    private readonly IValidationService _validationService;
    private readonly IInstallerCommandService _installerCommandService;
    private readonly IMsiInspectorService _msiInspectorService;
    private readonly ISettingsService _settingsService;
    private readonly IProfileService _profileService;
    private readonly IHistoryService _historyService;
    private readonly IToolLocatorService _toolLocatorService;
    private readonly IToolInstallerService _toolInstallerService;
    private readonly IDialogService _dialogService;

    private readonly ObservableCollection<string> _logs = new();
    private readonly ReadOnlyObservableCollection<string> _readonlyLogs;
    private readonly ConcurrentQueue<string> _pendingLogQueue = new();
    private readonly WpfDispatcherTimer _logFlushTimer;

    private const int MaxVisibleLogLines = 500;
    private const int MaxLogFlushBatchSize = 40;

    private CancellationTokenSource? _msiInspectionCancellation;
    private bool _isInitialized;
    private bool _suppressSetupRefresh;

    [ObservableProperty]
    private string sourceFolder = string.Empty;

    [ObservableProperty]
    private string setupFilePath = string.Empty;

    [ObservableProperty]
    private string outputFolder = string.Empty;

    [ObservableProperty]
    private string installCommand = string.Empty;

    [ObservableProperty]
    private string uninstallCommand = string.Empty;

    [ObservableProperty]
    private InstallerType installerType = InstallerType.Unknown;

    [ObservableProperty]
    private string msiMetadataSummary = string.Empty;

    [ObservableProperty]
    private string intuneWinAppUtilPath = string.Empty;

    [ObservableProperty]
    private string profileName = string.Empty;

    [ObservableProperty]
    private string? selectedProfileName;

    [ObservableProperty]
    private SilentInstallPreset? selectedPreset;

    [ObservableProperty]
    private string statusTitle = "Ready";

    [ObservableProperty]
    private string statusMessage = "Select an installer to start packaging.";

    [ObservableProperty]
    private OperationState operationState = OperationState.Idle;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isDragOver;

    [ObservableProperty]
    private string resultOutputPath = string.Empty;

    public MainViewModel(
        IPackagingWorkflowService packagingWorkflowService,
        IValidationService validationService,
        IInstallerCommandService installerCommandService,
        IMsiInspectorService msiInspectorService,
        ISettingsService settingsService,
        IProfileService profileService,
        IHistoryService historyService,
        IToolLocatorService toolLocatorService,
        IToolInstallerService toolInstallerService,
        IDialogService dialogService)
    {
        _packagingWorkflowService = packagingWorkflowService;
        _validationService = validationService;
        _installerCommandService = installerCommandService;
        _msiInspectorService = msiInspectorService;
        _settingsService = settingsService;
        _profileService = profileService;
        _historyService = historyService;
        _toolLocatorService = toolLocatorService;
        _toolInstallerService = toolInstallerService;
        _dialogService = dialogService;

        _readonlyLogs = new ReadOnlyObservableCollection<string>(_logs);

        _logFlushTimer = new WpfDispatcherTimer(WpfDispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogs(MaxLogFlushBatchSize);
        _logFlushTimer.Start();

        ValidationErrors.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasValidationErrors));
            OnPropertyChanged(nameof(IsConfigurationValid));
            if (PackageCommand is not null)
            {
                PackageCommand.NotifyCanExecuteChanged();
            }
        };

        foreach (var preset in _installerCommandService.GetExeSilentPresets())
        {
            ExePresets.Add(preset);
        }

        SelectedPreset = ExePresets.FirstOrDefault();

        BrowseSourceFolderCommand = new RelayCommand(BrowseSourceFolder);
        BrowseSetupFileCommand = new AsyncRelayCommand(BrowseSetupFileAsync);
        BrowseOutputFolderCommand = new RelayCommand(BrowseOutputFolder);
        BrowseToolPathCommand = new RelayCommand(BrowseToolPath);
        AutoLocateToolCommand = new RelayCommand(AutoLocateToolPath);
        InstallToolCommand = new AsyncRelayCommand(InstallToolAsync, () => !IsBusy);
        ApplyPresetCommand = new RelayCommand(ApplySelectedPreset);
        PackageCommand = new AsyncRelayCommand(PackageAsync, CanPackage);
        QuickFixCommand = new AsyncRelayCommand(ApplyQuickFixesAsync, () => !IsBusy);
        ResetCommand = new RelayCommand(ResetConfiguration, () => !IsBusy);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, CanOpenOutputFolder);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync, () => !IsBusy);
    }

    public ObservableCollection<string> ValidationErrors { get; } = new();

    public ObservableCollection<PackageHistoryEntry> RecentHistory { get; } = new();

    public ObservableCollection<SilentInstallPreset> ExePresets { get; } = new();

    public ObservableCollection<string> AvailableProfiles { get; } = new();

    public ReadOnlyObservableCollection<string> Logs => _readonlyLogs;

    public bool IsMsiInstaller => InstallerType == InstallerType.Msi;

    public bool IsExeInstaller => InstallerType == InstallerType.Exe;

    public bool HasValidationErrors => ValidationErrors.Count > 0;

    public bool IsConfigurationValid => !HasValidationErrors;

    public string InstallerTypeDisplay => InstallerType switch
    {
        InstallerType.Msi => "MSI detected",
        InstallerType.Exe => "EXE detected",
        _ => "No installer selected"
    };

    public WpfBrush InstallerTypeBrush => InstallerType switch
    {
        InstallerType.Msi => new WpfSolidColorBrush(WpfColor.FromRgb(15, 122, 87)),
        InstallerType.Exe => new WpfSolidColorBrush(WpfColor.FromRgb(33, 92, 176)),
        _ => new WpfSolidColorBrush(WpfColor.FromRgb(98, 107, 128))
    };

    public WpfBrush StatusBrush => OperationState switch
    {
        OperationState.Success => new WpfSolidColorBrush(WpfColor.FromRgb(15, 122, 87)),
        OperationState.Error => new WpfSolidColorBrush(WpfColor.FromRgb(176, 42, 55)),
        OperationState.Running => new WpfSolidColorBrush(WpfColor.FromRgb(33, 92, 176)),
        _ => new WpfSolidColorBrush(WpfColor.FromRgb(87, 96, 116))
    };

    public bool IsToolPathValid => !string.IsNullOrWhiteSpace(IntuneWinAppUtilPath) && File.Exists(IntuneWinAppUtilPath);

    public bool IsSetupFileValid => !string.IsNullOrWhiteSpace(SetupFilePath) && File.Exists(SetupFilePath);

    public bool IsSourceFolderValid => !string.IsNullOrWhiteSpace(SourceFolder) && Directory.Exists(SourceFolder);

    public bool IsOutputFolderValid => !string.IsNullOrWhiteSpace(OutputFolder);

    public string ReadinessSummary =>
        $"Tool path: {(IsToolPathValid ? "Ready" : "Missing")}{Environment.NewLine}" +
        $"Installer file: {(IsSetupFileValid ? "Ready" : "Missing")}{Environment.NewLine}" +
        $"Source folder: {(IsSourceFolderValid ? "Ready" : "Missing")}{Environment.NewLine}" +
        $"Output folder: {(IsOutputFolderValid ? "Ready" : "Missing")}";

    public string NextStepHint
    {
        get
        {
            if (!IsToolPathValid)
            {
                return "Tool not found. Click Install Tool (1 click), Auto Locate, or set the path manually.";
            }

            if (!IsSetupFileValid)
            {
                return "Drop or browse a .msi/.exe installer file.";
            }

            if (!IsSourceFolderValid)
            {
                return "Set the source folder (usually the installer's folder).";
            }

            if (!IsOutputFolderValid)
            {
                return "Set the output folder for the generated .intunewin.";
            }

            if (HasValidationErrors)
            {
                return ValidationErrors.FirstOrDefault() ?? "Resolve the remaining validation errors.";
            }

            return "Everything is ready. Click Start Packaging.";
        }
    }

    public IRelayCommand BrowseSourceFolderCommand { get; }

    public IAsyncRelayCommand BrowseSetupFileCommand { get; }

    public IRelayCommand BrowseOutputFolderCommand { get; }

    public IRelayCommand BrowseToolPathCommand { get; }

    public IRelayCommand AutoLocateToolCommand { get; }

    public IAsyncRelayCommand InstallToolCommand { get; }

    public IRelayCommand ApplyPresetCommand { get; }

    public IAsyncRelayCommand PackageCommand { get; }

    public IAsyncRelayCommand QuickFixCommand { get; }

    public IRelayCommand ResetCommand { get; }

    public IRelayCommand OpenOutputFolderCommand { get; }

    public IAsyncRelayCommand SaveProfileCommand { get; }

    public IAsyncRelayCommand LoadProfileCommand { get; }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;

        await LoadSettingsAsync();
        await RefreshProfileListAsync();
        await RefreshHistoryAsync();

        UpdateValidation();
    }

    public void SetDragOverState(bool dragOver)
    {
        IsDragOver = dragOver;
    }

    public async Task HandleFileDropAsync(IEnumerable<string> droppedPaths)
    {
        var installer = FindDroppedInstaller(droppedPaths);
        if (installer is null)
        {
            SetStatus(OperationState.Error, "Unsupported Drop", "Drop a valid .msi or .exe installer file.");
            return;
        }

        await SelectSetupFileAsync(installer);
    }

    partial void OnSourceFolderChanged(string value)
    {
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnOutputFolderChanged(string value)
    {
        UpdateValidation();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        NotifyReadinessChanged();
    }

    partial void OnInstallCommandChanged(string value)
    {
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnUninstallCommandChanged(string value)
    {
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnIntuneWinAppUtilPathChanged(string value)
    {
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnSetupFilePathChanged(string value)
    {
        NotifyReadinessChanged();

        if (_suppressSetupRefresh)
        {
            return;
        }

        _ = HandleSetupFileChangedAsync(value);
    }

    partial void OnInstallerTypeChanged(InstallerType value)
    {
        OnPropertyChanged(nameof(IsMsiInstaller));
        OnPropertyChanged(nameof(IsExeInstaller));
        OnPropertyChanged(nameof(InstallerTypeDisplay));
        OnPropertyChanged(nameof(InstallerTypeBrush));
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnOperationStateChanged(OperationState value)
    {
        OnPropertyChanged(nameof(StatusBrush));
    }

    partial void OnIsBusyChanged(bool value)
    {
        PackageCommand.NotifyCanExecuteChanged();
        InstallToolCommand.NotifyCanExecuteChanged();
        QuickFixCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        LoadProfileCommand.NotifyCanExecuteChanged();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultOutputPathChanged(string value)
    {
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();

        IntuneWinAppUtilPath = settings.IntuneWinAppUtilPath;
        SourceFolder = settings.LastSourceFolder;
        OutputFolder = settings.LastOutputFolder;

        if (File.Exists(settings.LastSetupFilePath))
        {
            await SelectSetupFileAsync(settings.LastSetupFilePath, updateOutputWhenEmpty: false);
        }

        if (string.IsNullOrWhiteSpace(IntuneWinAppUtilPath) || !File.Exists(IntuneWinAppUtilPath))
        {
            IntuneWinAppUtilPath = _toolLocatorService.LocateToolPath() ?? string.Empty;
        }
    }

    private async Task RefreshProfileListAsync()
    {
        var profiles = await _profileService.GetProfilesAsync();

        AvailableProfiles.Clear();
        foreach (var profile in profiles)
        {
            AvailableProfiles.Add(profile.Name);
        }

        if (!string.IsNullOrWhiteSpace(ProfileName) && AvailableProfiles.Contains(ProfileName))
        {
            SelectedProfileName = ProfileName;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var entries = await _historyService.GetRecentAsync(20);

        RecentHistory.Clear();
        foreach (var entry in entries)
        {
            RecentHistory.Add(entry);
        }
    }

    private async Task BrowseSetupFileAsync()
    {
        var initialDir = Directory.Exists(SourceFolder) ? SourceFolder : null;
        var file = _dialogService.PickInstallerFile(initialDir);
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        await SelectSetupFileAsync(file);
    }

    private void BrowseSourceFolder()
    {
        var folder = _dialogService.PickFolder(SourceFolder);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        SourceFolder = folder;

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            OutputFolder = Path.Combine(folder, "IntuneWinOutput");
        }
    }

    private void BrowseOutputFolder()
    {
        var folder = _dialogService.PickFolder(OutputFolder);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputFolder = folder;
        }
    }

    private void BrowseToolPath()
    {
        var initialDirectory = Path.GetDirectoryName(IntuneWinAppUtilPath);
        var file = _dialogService.PickExecutableFile(initialDirectory);
        if (!string.IsNullOrWhiteSpace(file))
        {
            IntuneWinAppUtilPath = file;
        }
    }

    private void AutoLocateToolPath()
    {
        var located = _toolLocatorService.LocateToolPath();
        if (!string.IsNullOrWhiteSpace(located))
        {
            IntuneWinAppUtilPath = located;
            SetStatus(OperationState.Idle, "Tool Located", "IntuneWinAppUtil.exe was located automatically.");
            return;
        }

        SetStatus(OperationState.Error, "Tool Not Found", "Could not auto-locate IntuneWinAppUtil.exe. Configure it manually.");
    }

    private async Task InstallToolAsync()
    {
        if (IsToolPathValid)
        {
            SetStatus(OperationState.Idle, "Tool Already Installed", "IntuneWinAppUtil.exe is already configured.");
            return;
        }

        IsBusy = true;
        AppendLog("Starting one-click tool install...");

        try
        {
            var installResult = await _toolInstallerService.InstallOrLocateAsync(new InlineProgress<string>(AppendLog));
            if (installResult.Success && !string.IsNullOrWhiteSpace(installResult.ToolPath))
            {
                IntuneWinAppUtilPath = installResult.ToolPath;
                await PersistSettingsAsync();

                SetStatus(
                    OperationState.Success,
                    installResult.AlreadyInstalled ? "Tool Ready" : "Tool Installed",
                    installResult.AlreadyInstalled
                        ? "IntuneWinAppUtil.exe was found and configured."
                        : "Installed and configured IntuneWinAppUtil.exe.");
            }
            else
            {
                SetStatus(
                    OperationState.Error,
                    "Tool Install Failed",
                    installResult.Message);
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, "Install Error", ex.Message);
            AppendLog($"Install error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            UpdateValidation();
            NotifyReadinessChanged();
        }
    }

    private void ApplySelectedPreset()
    {
        if (InstallerType != InstallerType.Exe || string.IsNullOrWhiteSpace(SetupFilePath))
        {
            return;
        }

        var suggestion = _installerCommandService.CreateSuggestion(
            SetupFilePath,
            InstallerType.Exe,
            preset: SelectedPreset);

        InstallCommand = suggestion.InstallCommand;
        UninstallCommand = suggestion.UninstallCommand;

        SetStatus(OperationState.Idle, "Preset Applied", $"Applied preset: {SelectedPreset?.Name ?? "Custom"}.");
    }

    private async Task ApplyQuickFixesAsync()
    {
        IsBusy = true;
        var fixes = new List<string>();

        try
        {
            if (!IsToolPathValid)
            {
                var locatedTool = _toolLocatorService.LocateToolPath();
                if (!string.IsNullOrWhiteSpace(locatedTool))
                {
                    IntuneWinAppUtilPath = locatedTool;
                    fixes.Add("Tool path auto-located.");
                }
                else
                {
                    var installResult = await _toolInstallerService.InstallOrLocateAsync(new InlineProgress<string>(AppendLog));
                    if (installResult.Success && !string.IsNullOrWhiteSpace(installResult.ToolPath))
                    {
                        IntuneWinAppUtilPath = installResult.ToolPath;
                        fixes.Add("Tool installed and configured.");
                    }
                }
            }

            if (!IsSetupFileValid && IsSourceFolderValid)
            {
                var candidateInstaller = Directory
                    .EnumerateFiles(SourceFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(IsSupportedInstallerFile);

                if (!string.IsNullOrWhiteSpace(candidateInstaller))
                {
                    await SelectSetupFileAsync(candidateInstaller);
                    fixes.Add($"Installer detected: {Path.GetFileName(candidateInstaller)}.");
                }
            }

            if (IsSetupFileValid)
            {
                var installerFolder = Path.GetDirectoryName(SetupFilePath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(installerFolder) &&
                    (!IsSourceFolderValid || !IsPathInsideFolder(SetupFilePath, SourceFolder)))
                {
                    SourceFolder = installerFolder;
                    fixes.Add("Source folder aligned with installer location.");
                }

                if (!IsOutputFolderValid)
                {
                    OutputFolder = Path.Combine(SourceFolder, "IntuneWinOutput");
                    fixes.Add("Default output folder created.");
                }

                InstallerType = _installerCommandService.DetectInstallerType(SetupFilePath);
                if (string.IsNullOrWhiteSpace(InstallCommand) || string.IsNullOrWhiteSpace(UninstallCommand))
                {
                    var metadata = InstallerType == InstallerType.Msi
                        ? await _msiInspectorService.InspectAsync(SetupFilePath)
                        : null;

                    var suggestion = _installerCommandService.CreateSuggestion(
                        SetupFilePath,
                        InstallerType,
                        metadata,
                        SelectedPreset);

                    if (string.IsNullOrWhiteSpace(InstallCommand))
                    {
                        InstallCommand = suggestion.InstallCommand;
                    }

                    if (string.IsNullOrWhiteSpace(UninstallCommand))
                    {
                        UninstallCommand = suggestion.UninstallCommand;
                    }

                    fixes.Add("Install and uninstall commands filled automatically.");
                }
            }

            UpdateValidation();
            NotifyReadinessChanged();

            if (fixes.Count == 0)
            {
                SetStatus(
                    HasValidationErrors ? OperationState.Error : OperationState.Idle,
                    "No Auto-Fix Applied",
                    NextStepHint);
                return;
            }

            SetStatus(
                HasValidationErrors ? OperationState.Idle : OperationState.Success,
                "Quick Fix Applied",
                string.Join(" ", fixes));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPackage()
    {
        return !IsBusy && IsConfigurationValid;
    }

    private bool CanOpenOutputFolder()
    {
        if (IsBusy)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ResultOutputPath) && File.Exists(ResultOutputPath))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder);
    }

    private async Task PackageAsync()
    {
        if (!CanPackage())
        {
            UpdateValidation();
            return;
        }

        IsBusy = true;
        ResultOutputPath = string.Empty;
        ClearLogs();

        SetStatus(OperationState.Running, "Packaging In Progress", "Running IntuneWinAppUtil.exe...");
        AppendLog("Packaging started.");

        try
        {
            await PersistSettingsAsync();

            var request = BuildRequest();
            var progress = new InlineProgress<string>(AppendLog);

            var result = await _packagingWorkflowService.PackageAsync(request, progress);
            ResultOutputPath = result.OutputPackagePath ?? string.Empty;

            var historyEntry = new PackageHistoryEntry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                SetupFilePath = SetupFilePath,
                OutputPackagePath = result.OutputPackagePath ?? OutputFolder,
                InstallerType = InstallerType,
                Success = result.Success,
                Message = result.Message
            };

            await _historyService.AddEntryAsync(historyEntry);
            await RefreshHistoryAsync();

            if (result.Success)
            {
                SetStatus(
                    OperationState.Success,
                    "Package Created",
                    $"Created: {result.OutputPackagePath}");
                AppendLog("Packaging completed successfully.");
            }
            else
            {
                SetStatus(OperationState.Error, "Packaging Failed", result.Message);
                AppendLog($"Packaging failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, "Unexpected Error", ex.Message);
            AppendLog($"Unexpected error: {ex.Message}");
        }
        finally
        {
            FlushPendingLogs(int.MaxValue);
            IsBusy = false;
            await PersistSettingsAsync();
            UpdateValidation();
        }
    }

    private async Task SaveProfileAsync()
    {
        var resolvedName = string.IsNullOrWhiteSpace(ProfileName)
            ? BuildDefaultProfileName()
            : ProfileName.Trim();

        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            SetStatus(OperationState.Error, "Profile Name Required", "Provide a profile name before saving.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await _profileService.GetProfileAsync(resolvedName);

        var profile = new PackageProfile
        {
            Name = resolvedName,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now,
            InstallerType = InstallerType,
            Configuration = BuildConfiguration()
        };

        await _profileService.SaveProfileAsync(profile);

        ProfileName = resolvedName;
        SelectedProfileName = resolvedName;

        await RefreshProfileListAsync();

        SetStatus(OperationState.Idle, "Profile Saved", $"Saved profile '{resolvedName}'.");
    }

    private async Task LoadProfileAsync()
    {
        var nameToLoad = !string.IsNullOrWhiteSpace(SelectedProfileName)
            ? SelectedProfileName
            : ProfileName;

        if (string.IsNullOrWhiteSpace(nameToLoad))
        {
            SetStatus(OperationState.Error, "No Profile Selected", "Select a profile to load.");
            return;
        }

        var profile = await _profileService.GetProfileAsync(nameToLoad);
        if (profile is null)
        {
            SetStatus(OperationState.Error, "Profile Missing", $"Profile '{nameToLoad}' was not found.");
            return;
        }

        _suppressSetupRefresh = true;
        try
        {
            ProfileName = profile.Name;
            SelectedProfileName = profile.Name;
            SourceFolder = profile.Configuration.SourceFolder;
            SetupFilePath = profile.Configuration.SetupFilePath;
            OutputFolder = profile.Configuration.OutputFolder;
            InstallCommand = profile.Configuration.InstallCommand;
            UninstallCommand = profile.Configuration.UninstallCommand;
            InstallerType = profile.InstallerType != InstallerType.Unknown
                ? profile.InstallerType
                : _installerCommandService.DetectInstallerType(profile.Configuration.SetupFilePath);
        }
        finally
        {
            _suppressSetupRefresh = false;
        }

        if (File.Exists(SetupFilePath) && InstallerType == InstallerType.Msi)
        {
            await RefreshMsiMetadataSummaryAsync(SetupFilePath);
        }

        UpdateValidation();
        SetStatus(OperationState.Idle, "Profile Loaded", $"Loaded profile '{profile.Name}'.");
    }

    private void ResetConfiguration()
    {
        _suppressSetupRefresh = true;
        try
        {
            SourceFolder = string.Empty;
            SetupFilePath = string.Empty;
            OutputFolder = string.Empty;
            InstallCommand = string.Empty;
            UninstallCommand = string.Empty;
            InstallerType = InstallerType.Unknown;
            MsiMetadataSummary = string.Empty;
            ResultOutputPath = string.Empty;
            ProfileName = string.Empty;
            SelectedProfileName = null;
            OperationState = OperationState.Idle;
            StatusTitle = "Ready";
            StatusMessage = "Configuration reset.";
            ClearLogs();
        }
        finally
        {
            _suppressSetupRefresh = false;
        }

        UpdateValidation();
    }

    private void OpenOutputFolder()
    {
        var pathToOpen = ResultOutputPath;

        if (!string.IsNullOrWhiteSpace(pathToOpen) && File.Exists(pathToOpen))
        {
            var parent = Path.GetDirectoryName(pathToOpen);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                _dialogService.OpenFolder(parent);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(OutputFolder))
        {
            _dialogService.OpenFolder(OutputFolder);
        }
    }

    private async Task SelectSetupFileAsync(string filePath, bool updateOutputWhenEmpty = true)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        SetupFilePath = filePath;

        if (string.IsNullOrWhiteSpace(SourceFolder) || !IsPathInsideFolder(filePath, SourceFolder))
        {
            SourceFolder = Path.GetDirectoryName(filePath) ?? string.Empty;
        }

        if (updateOutputWhenEmpty && string.IsNullOrWhiteSpace(OutputFolder) && !string.IsNullOrWhiteSpace(SourceFolder))
        {
            OutputFolder = Path.Combine(SourceFolder, "IntuneWinOutput");
        }

        await HandleSetupFileChangedAsync(filePath);
    }

    private async Task HandleSetupFileChangedAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            InstallerType = InstallerType.Unknown;
            MsiMetadataSummary = string.Empty;
            UpdateValidation();
            return;
        }

        InstallerType = _installerCommandService.DetectInstallerType(filePath);

        if (InstallerType == InstallerType.Msi)
        {
            await RefreshMsiMetadataSummaryAsync(filePath);

            var metadata = await _msiInspectorService.InspectAsync(filePath);
            var suggestion = _installerCommandService.CreateSuggestion(filePath, InstallerType.Msi, metadata);

            InstallCommand = suggestion.InstallCommand;
            UninstallCommand = suggestion.UninstallCommand;
        }
        else if (InstallerType == InstallerType.Exe)
        {
            MsiMetadataSummary = string.Empty;

            var suggestion = _installerCommandService.CreateSuggestion(filePath, InstallerType.Exe, preset: SelectedPreset);
            InstallCommand = suggestion.InstallCommand;
            UninstallCommand = suggestion.UninstallCommand;
        }

        UpdateValidation();
    }

    private async Task RefreshMsiMetadataSummaryAsync(string msiPath)
    {
        _msiInspectionCancellation?.Cancel();
        _msiInspectionCancellation = new CancellationTokenSource();

        try
        {
            var metadata = await _msiInspectorService.InspectAsync(msiPath, _msiInspectionCancellation.Token);

            if (metadata is null)
            {
                MsiMetadataSummary = string.Empty;
                return;
            }

            var summaryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(metadata.ProductName))
            {
                summaryParts.Add($"Product: {metadata.ProductName}");
            }

            if (!string.IsNullOrWhiteSpace(metadata.ProductVersion))
            {
                summaryParts.Add($"Version: {metadata.ProductVersion}");
            }

            if (!string.IsNullOrWhiteSpace(metadata.ProductCode))
            {
                summaryParts.Add($"Code: {metadata.ProductCode}");
            }

            if (!string.IsNullOrWhiteSpace(metadata.InspectionWarning))
            {
                summaryParts.Add($"Warning: {metadata.InspectionWarning}");
            }

            MsiMetadataSummary = summaryParts.Count == 0
                ? "MSI metadata available."
                : string.Join(" | ", summaryParts);
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this inspection request.
        }
    }

    private async Task PersistSettingsAsync()
    {
        var settings = new AppSettings
        {
            IntuneWinAppUtilPath = IntuneWinAppUtilPath,
            LastSourceFolder = SourceFolder,
            LastOutputFolder = OutputFolder,
            LastSetupFilePath = SetupFilePath
        };

        await _settingsService.SaveAsync(settings);
    }

    private PackagingRequest BuildRequest()
    {
        return new PackagingRequest
        {
            IntuneWinAppUtilPath = IntuneWinAppUtilPath,
            InstallerType = InstallerType,
            Configuration = BuildConfiguration()
        };
    }

    private PackageConfiguration BuildConfiguration()
    {
        return new PackageConfiguration
        {
            SourceFolder = SourceFolder,
            SetupFilePath = SetupFilePath,
            OutputFolder = OutputFolder,
            InstallCommand = InstallCommand,
            UninstallCommand = UninstallCommand
        };
    }

    private void UpdateValidation()
    {
        var validation = _validationService.Validate(BuildRequest());

        ValidationErrors.Clear();
        foreach (var error in validation.Errors)
        {
            ValidationErrors.Add(error);
        }

        NotifyReadinessChanged();
    }

    private void NotifyReadinessChanged()
    {
        OnPropertyChanged(nameof(IsToolPathValid));
        OnPropertyChanged(nameof(IsSetupFileValid));
        OnPropertyChanged(nameof(IsSourceFolderValid));
        OnPropertyChanged(nameof(IsOutputFolderValid));
        OnPropertyChanged(nameof(ReadinessSummary));
        OnPropertyChanged(nameof(NextStepHint));
    }

    private void SetStatus(OperationState state, string title, string message)
    {
        OperationState = state;
        StatusTitle = title;
        StatusMessage = message;
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _pendingLogQueue.Enqueue(timestamped);
    }

    private void ClearLogs()
    {
        while (_pendingLogQueue.TryDequeue(out _))
        {
        }

        _logs.Clear();
    }

    private void FlushPendingLogs(int maxBatchSize)
    {
        var itemsAdded = 0;
        while (itemsAdded < maxBatchSize && _pendingLogQueue.TryDequeue(out var line))
        {
            _logs.Add(line);
            itemsAdded++;
        }

        if (itemsAdded > 0)
        {
            while (_logs.Count > MaxVisibleLogLines)
            {
                _logs.RemoveAt(0);
            }
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onReport;

        public InlineProgress(Action<T> onReport)
        {
            _onReport = onReport;
        }

        public void Report(T value)
        {
            _onReport(value);
        }
    }

    private string BuildDefaultProfileName()
    {
        var installerName = Path.GetFileNameWithoutExtension(SetupFilePath);
        if (string.IsNullOrWhiteSpace(installerName))
        {
            return string.Empty;
        }

        return $"{installerName}-profile";
    }

    private static bool IsSupportedInstallerFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindDroppedInstaller(IEnumerable<string> droppedPaths)
    {
        foreach (var droppedPath in droppedPaths)
        {
            if (IsSupportedInstallerFile(droppedPath))
            {
                return droppedPath;
            }

            if (Directory.Exists(droppedPath))
            {
                var installerFromFolder = Directory
                    .EnumerateFiles(droppedPath, "*.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(IsSupportedInstallerFile);

                if (!string.IsNullOrWhiteSpace(installerFromFolder))
                {
                    return installerFromFolder;
                }
            }
        }

        return null;
    }

    private static bool IsPathInsideFolder(string filePath, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        var folderFullPath = Path.GetFullPath(folderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var fileFullPath = Path.GetFullPath(filePath);

        return fileFullPath.StartsWith(folderFullPath, StringComparison.OrdinalIgnoreCase);
    }
}






