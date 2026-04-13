using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfDispatcherPriority = System.Windows.Threading.DispatcherPriority;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;
using WpfApplication = System.Windows.Application;
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
    private readonly IPreflightService _preflightService;
    private readonly IAppUpdateService _appUpdateService;
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
    private AppUpdateInfo? _latestUpdateInfo;

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
    private bool useSmartSourceStaging = true;

    [ObservableProperty]
    private IntuneInstallContext installContext = IntuneInstallContext.System;

    [ObservableProperty]
    private IntuneRestartBehavior restartBehavior = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes;

    [ObservableProperty]
    private int maxRunTimeMinutes = 60;

    [ObservableProperty]
    private string appliedTemplateName = string.Empty;

    [ObservableProperty]
    private string templateGuidance = string.Empty;

    [ObservableProperty]
    private bool requireSilentSwitchReview;

    [ObservableProperty]
    private bool silentSwitchesVerified;

    [ObservableProperty]
    private IntuneDetectionRuleType detectionRuleType = IntuneDetectionRuleType.None;

    [ObservableProperty]
    private string detectionMsiProductCode = string.Empty;

    [ObservableProperty]
    private string detectionMsiProductVersion = string.Empty;

    [ObservableProperty]
    private string detectionFilePath = string.Empty;

    [ObservableProperty]
    private string detectionFileOrFolderName = string.Empty;

    [ObservableProperty]
    private bool detectionFileCheck32BitOn64System;

    [ObservableProperty]
    private IntuneDetectionOperator detectionFileOperator = IntuneDetectionOperator.Exists;

    [ObservableProperty]
    private string detectionFileValue = string.Empty;

    [ObservableProperty]
    private string detectionRegistryHive = "HKEY_LOCAL_MACHINE";

    [ObservableProperty]
    private string detectionRegistryKeyPath = string.Empty;

    [ObservableProperty]
    private string detectionRegistryValueName = string.Empty;

    [ObservableProperty]
    private bool detectionRegistryCheck32BitOn64System;

    [ObservableProperty]
    private IntuneDetectionOperator detectionRegistryOperator = IntuneDetectionOperator.Exists;

    [ObservableProperty]
    private string detectionRegistryValue = string.Empty;

    [ObservableProperty]
    private string detectionScriptBody = string.Empty;

    [ObservableProperty]
    private bool detectionScriptRunAs32BitOn64System;

    [ObservableProperty]
    private bool detectionScriptEnforceSignatureCheck;

    [ObservableProperty]
    private string requirementOperatingSystemArchitecture = "x64";

    [ObservableProperty]
    private string requirementMinimumOperatingSystem = "Windows 10 1607";

    [ObservableProperty]
    private int requirementMinimumFreeDiskSpaceMb;

    [ObservableProperty]
    private int requirementMinimumMemoryMb;

    [ObservableProperty]
    private int requirementMinimumCpuSpeedMhz;

    [ObservableProperty]
    private int requirementMinimumLogicalProcessors;

    [ObservableProperty]
    private string requirementScriptBody = string.Empty;

    [ObservableProperty]
    private bool requirementScriptRunAs32BitOn64System;

    [ObservableProperty]
    private bool requirementScriptEnforceSignatureCheck;

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

    [ObservableProperty]
    private string resultMetadataPath = string.Empty;

    [ObservableProperty]
    private string resultChecklistPath = string.Empty;

    [ObservableProperty]
    private string intunePortalChecklist = string.Empty;

    [ObservableProperty]
    private bool useLowImpactMode = false;

    [ObservableProperty]
    private bool enableSilentAppUpdates;

    [ObservableProperty]
    private double packagingProgressPercentage;

    [ObservableProperty]
    private bool isPackagingProgressIndeterminate;

    [ObservableProperty]
    private string packagingProgressStep = "Ready";

    [ObservableProperty]
    private string packagingProgressDetail = "Waiting to start.";

    [ObservableProperty]
    private string currentVersion = "1.0.0";

    [ObservableProperty]
    private string latestVersion = "-";

    [ObservableProperty]
    private string updateStatus = "Not checked yet.";

    [ObservableProperty]
    private string updateChangelog = "Click 'Check for Updates' to load release notes.";

    [ObservableProperty]
    private bool isUpdateAvailable;

    [ObservableProperty]
    private bool hasPackagingRun;

    [ObservableProperty]
    private bool hasPreflightRun;

    [ObservableProperty]
    private string preflightSummary = "Run preflight checks to verify tool health, folders, permissions, and disk space.";

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
        IPreflightService preflightService,
        IAppUpdateService appUpdateService,
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
        _preflightService = preflightService;
        _appUpdateService = appUpdateService;
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

        PreflightChecks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPreflightErrors));
            OnPropertyChanged(nameof(HasPreflightWarnings));
            OnPropertyChanged(nameof(IsPreflightReady));
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
        RunPreflightCommand = new AsyncRelayCommand(RunPreflightAsync, () => !IsBusy);
        QuickFixCommand = new AsyncRelayCommand(ApplyQuickFixesAsync, () => !IsBusy);
        ResetCommand = new RelayCommand(ResetConfiguration, () => !IsBusy);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, CanOpenOutputFolder);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, () => !IsBusy);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync, () => !IsBusy);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, CanInstallUpdate);

        CurrentVersion = ResolveCurrentVersion();
        LatestVersion = CurrentVersion;
    }

    public ObservableCollection<string> ValidationErrors { get; } = new();

    public ObservableCollection<PackageHistoryEntry> RecentHistory { get; } = new();

    public ObservableCollection<SilentInstallPreset> ExePresets { get; } = new();

    public ObservableCollection<string> AvailableProfiles { get; } = new();

    public ObservableCollection<PreflightCheck> PreflightChecks { get; } = new();

    public ReadOnlyObservableCollection<string> Logs => _readonlyLogs;

    public IReadOnlyList<IntuneDetectionRuleType> DetectionRuleTypes { get; } = Enum.GetValues<IntuneDetectionRuleType>();

    public IReadOnlyList<IntuneDetectionOperator> DetectionOperators { get; } = Enum.GetValues<IntuneDetectionOperator>();

    public IReadOnlyList<IntuneInstallContext> InstallContexts { get; } = Enum.GetValues<IntuneInstallContext>();

    public IReadOnlyList<IntuneRestartBehavior> RestartBehaviors { get; } = Enum.GetValues<IntuneRestartBehavior>();

    public IReadOnlyList<string> RegistryHives { get; } =
    [
        "HKEY_LOCAL_MACHINE",
        "HKEY_CURRENT_USER",
        "HKEY_CLASSES_ROOT",
        "HKEY_USERS",
        "HKEY_CURRENT_CONFIG"
    ];

    public IReadOnlyList<string> RequirementArchitectures { get; } =
    [
        "x64",
        "x86",
        "Both"
    ];

    public IReadOnlyList<string> MinimumOperatingSystemOptions { get; } =
    [
        "Windows 10 1607",
        "Windows 10 1703",
        "Windows 10 1709",
        "Windows 10 1803",
        "Windows 10 1809",
        "Windows 10 1903",
        "Windows 10 1909",
        "Windows 10 2004",
        "Windows 10 20H2",
        "Windows 10 21H1",
        "Windows 10 21H2",
        "Windows 10 22H2",
        "Windows 11 21H2",
        "Windows 11 22H2",
        "Windows 11 23H2",
        "Windows 11 24H2"
    ];

    public bool IsMsiInstaller => InstallerType == InstallerType.Msi;

    public bool IsExeInstaller => InstallerType == InstallerType.Exe;

    public bool IsMsiDetectionRule => DetectionRuleType == IntuneDetectionRuleType.MsiProductCode;

    public bool IsFileDetectionRule => DetectionRuleType == IntuneDetectionRuleType.File;

    public bool IsRegistryDetectionRule => DetectionRuleType == IntuneDetectionRuleType.Registry;

    public bool IsScriptDetectionRule => DetectionRuleType == IntuneDetectionRuleType.Script;

    public bool IsSilentSwitchReviewVisible => InstallerType == InstallerType.Exe && RequireSilentSwitchReview;

    public bool HasValidationErrors => ValidationErrors.Count > 0;

    public bool IsConfigurationValid => !HasValidationErrors;

    public bool HasPreflightErrors => PreflightChecks.Any(check => !check.Passed && check.Severity == PreflightSeverity.Error);

    public bool HasPreflightWarnings => PreflightChecks.Any(check => !check.Passed && check.Severity == PreflightSeverity.Warning);

    public bool IsPreflightReady => HasPreflightRun && !HasPreflightErrors;

    public string InstallerTypeDisplay => InstallerType switch
    {
        InstallerType.Msi => "MSI detected",
        InstallerType.Exe => "EXE detected",
        InstallerType.AppxMsix => "APPX/MSIX detected",
        InstallerType.Script => "Script installer detected",
        _ => "No installer selected"
    };

    public WpfBrush InstallerTypeBrush => InstallerType switch
    {
        InstallerType.Msi => new WpfSolidColorBrush(WpfColor.FromRgb(15, 122, 87)),
        InstallerType.Exe => new WpfSolidColorBrush(WpfColor.FromRgb(33, 92, 176)),
        InstallerType.AppxMsix => new WpfSolidColorBrush(WpfColor.FromRgb(120, 76, 12)),
        InstallerType.Script => new WpfSolidColorBrush(WpfColor.FromRgb(79, 62, 148)),
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
        $"Output folder: {(IsOutputFolderValid ? "Ready" : "Missing")}{Environment.NewLine}" +
        $"Detection rule: {(DetectionRuleType == IntuneDetectionRuleType.None ? "Missing" : "Configured")}{Environment.NewLine}" +
        $"Smart staging: {(UseSmartSourceStaging ? "Enabled" : "Disabled")}";

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
                return "Drop or browse a supported setup file (.msi, .exe, .appx/.msix/.bundle, .ps1, .cmd, .bat).";
            }

            if (!IsSourceFolderValid)
            {
                return "Set the source folder (usually the installer's folder).";
            }

            if (!IsOutputFolderValid)
            {
                return "Set the output folder for the generated .intunewin.";
            }

            if (DetectionRuleType == IntuneDetectionRuleType.None)
            {
                return "Configure an Intune detection rule before packaging.";
            }

            if (IsSilentSwitchReviewVisible && !SilentSwitchesVerified)
            {
                return "Review and confirm EXE silent switches before packaging.";
            }

            if (HasValidationErrors)
            {
                return ValidationErrors.FirstOrDefault() ?? "Resolve the remaining validation errors.";
            }

            if (HasPreflightRun && HasPreflightErrors)
            {
                var blocking = PreflightChecks.FirstOrDefault(check => !check.Passed && check.Severity == PreflightSeverity.Error);
                return blocking is null
                    ? "Run preflight again to verify packaging readiness."
                    : $"Preflight blocked: {blocking.Title}. {blocking.Message}";
            }

            return "Everything is ready. Click Start Packaging.";
        }
    }

    public string PackagingProgressLabel => IsPackagingProgressIndeterminate
        ? PackagingProgressStep
        : $"{PackagingProgressStep} ({PackagingProgressPercentage:0}%)";

    public IRelayCommand BrowseSourceFolderCommand { get; }

    public IAsyncRelayCommand BrowseSetupFileCommand { get; }

    public IRelayCommand BrowseOutputFolderCommand { get; }

    public IRelayCommand BrowseToolPathCommand { get; }

    public IRelayCommand AutoLocateToolCommand { get; }

    public IAsyncRelayCommand InstallToolCommand { get; }

    public IRelayCommand ApplyPresetCommand { get; }

    public IAsyncRelayCommand PackageCommand { get; }

    public IAsyncRelayCommand RunPreflightCommand { get; }

    public IAsyncRelayCommand QuickFixCommand { get; }

    public IRelayCommand ResetCommand { get; }

    public IRelayCommand OpenOutputFolderCommand { get; }

    public IAsyncRelayCommand SaveProfileCommand { get; }

    public IAsyncRelayCommand LoadProfileCommand { get; }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand InstallUpdateCommand { get; }

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
            SetStatus(OperationState.Error, "Unsupported Drop", "Drop a supported setup file (.msi, .exe, .appx/.msix/.bundle, .ps1, .cmd, .bat).");
            return;
        }

        await SelectSetupFileAsync(installer);
    }

    partial void OnSourceFolderChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnOutputFolderChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        NotifyReadinessChanged();
    }

    partial void OnInstallCommandChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnUninstallCommandChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnUseSmartSourceStagingChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnInstallContextChanged(IntuneInstallContext value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRestartBehaviorChanged(IntuneRestartBehavior value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnMaxRunTimeMinutesChanged(int value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequireSilentSwitchReviewChanged(bool value)
    {
        if (!value)
        {
            SilentSwitchesVerified = true;
        }

        InvalidatePreflightIfNeeded();
        OnPropertyChanged(nameof(IsSilentSwitchReviewVisible));
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnSilentSwitchesVerifiedChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRuleTypeChanged(IntuneDetectionRuleType value)
    {
        InvalidatePreflightIfNeeded();
        OnPropertyChanged(nameof(IsMsiDetectionRule));
        OnPropertyChanged(nameof(IsFileDetectionRule));
        OnPropertyChanged(nameof(IsRegistryDetectionRule));
        OnPropertyChanged(nameof(IsScriptDetectionRule));
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionMsiProductCodeChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionMsiProductVersionChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionFilePathChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionFileOrFolderNameChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionFileCheck32BitOn64SystemChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionFileOperatorChanged(IntuneDetectionOperator value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionFileValueChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRegistryHiveChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRegistryKeyPathChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRegistryValueNameChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRegistryCheck32BitOn64SystemChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRegistryOperatorChanged(IntuneDetectionOperator value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionRegistryValueChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionScriptBodyChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionScriptRunAs32BitOn64SystemChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnDetectionScriptEnforceSignatureCheckChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementOperatingSystemArchitectureChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementMinimumOperatingSystemChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementMinimumFreeDiskSpaceMbChanged(int value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementMinimumMemoryMbChanged(int value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementMinimumCpuSpeedMhzChanged(int value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementMinimumLogicalProcessorsChanged(int value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementScriptBodyChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementScriptRunAs32BitOn64SystemChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnRequirementScriptEnforceSignatureCheckChanged(bool value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnIntuneWinAppUtilPathChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnSetupFilePathChanged(string value)
    {
        InvalidatePreflightIfNeeded();
        NotifyReadinessChanged();

        if (_suppressSetupRefresh)
        {
            return;
        }

        _ = HandleSetupFileChangedAsync(value);
    }

    partial void OnInstallerTypeChanged(InstallerType value)
    {
        InvalidatePreflightIfNeeded();
        OnPropertyChanged(nameof(IsMsiInstaller));
        OnPropertyChanged(nameof(IsExeInstaller));
        OnPropertyChanged(nameof(IsSilentSwitchReviewVisible));
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
        RunPreflightCommand.NotifyCanExecuteChanged();
        InstallToolCommand.NotifyCanExecuteChanged();
        QuickFixCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        LoadProfileCommand.NotifyCanExecuteChanged();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultOutputPathChanged(string value)
    {
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultMetadataPathChanged(string value)
    {
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnResultChecklistPathChanged(string value)
    {
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnPackagingProgressPercentageChanged(double value)
    {
        OnPropertyChanged(nameof(PackagingProgressLabel));
    }

    partial void OnPackagingProgressStepChanged(string value)
    {
        OnPropertyChanged(nameof(PackagingProgressLabel));
    }

    partial void OnIsPackagingProgressIndeterminateChanged(bool value)
    {
        OnPropertyChanged(nameof(PackagingProgressLabel));
    }

    partial void OnIsUpdateAvailableChanged(bool value)
    {
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasPreflightRunChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPreflightReady));
        OnPropertyChanged(nameof(NextStepHint));
    }

    partial void OnUseLowImpactModeChanged(bool value)
    {
        NotifyReadinessChanged();
        _ = PersistSettingsSafeAsync();
    }

    partial void OnEnableSilentAppUpdatesChanged(bool value)
    {
        _ = PersistSettingsSafeAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();

        IntuneWinAppUtilPath = settings.IntuneWinAppUtilPath;
        SourceFolder = settings.LastSourceFolder;
        OutputFolder = settings.LastOutputFolder;
        UseLowImpactMode = settings.UseLowImpactMode;
        EnableSilentAppUpdates = settings.EnableSilentAppUpdates;

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
            OutputFolder = BuildDefaultOutputFolder(folder);
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
            AppendLog("Configured tool path found. Verifying tool health...");
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
        ApplySuggestion(suggestion);

        SetStatus(OperationState.Idle, "Preset Applied", $"Applied preset: {SelectedPreset?.Name ?? "Custom"}.");
    }

    private async Task RunPreflightAsync()
    {
        IsBusy = true;
        AppendLog("Running preflight checks...");

        try
        {
            var result = await RunPreflightCoreAsync();

            if (result.HasErrors)
            {
                SetStatus(OperationState.Error, "Preflight Failed", "Fix the blocking preflight issues before packaging.");
            }
            else if (result.HasWarnings)
            {
                SetStatus(OperationState.Idle, "Preflight Complete", "Preflight passed with warnings. Review the notes before packaging.");
            }
            else
            {
                SetStatus(OperationState.Success, "Preflight Passed", "All critical preflight checks passed.");
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, "Preflight Error", ex.Message);
            AppendLog($"Preflight error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<PreflightResult> RunPreflightCoreAsync(CancellationToken cancellationToken = default)
    {
        var result = await _preflightService.RunAsync(BuildRequest(), cancellationToken);
        ApplyPreflightResult(result);
        return result;
    }

    private void ApplyPreflightResult(PreflightResult result)
    {
        HasPreflightRun = true;
        PreflightChecks.Clear();

        foreach (var check in result.Checks)
        {
            PreflightChecks.Add(check);

            var label = check.Passed
                ? "PRECHECK OK"
                : check.Severity == PreflightSeverity.Warning
                    ? "PRECHECK WARN"
                    : "PRECHECK ERROR";

            AppendLog($"{label} | {check.Title}: {check.Message}");
        }

        PreflightSummary = BuildPreflightSummary(result);
        OnPropertyChanged(nameof(HasPreflightErrors));
        OnPropertyChanged(nameof(HasPreflightWarnings));
        OnPropertyChanged(nameof(IsPreflightReady));
        OnPropertyChanged(nameof(NextStepHint));
    }

    private static string BuildPreflightSummary(PreflightResult result)
    {
        if (result.TotalCount == 0)
        {
            return "No preflight checks were executed.";
        }

        var warnings = result.Checks.Count(check => !check.Passed && check.Severity == PreflightSeverity.Warning);
        var errors = result.Checks.Count(check => !check.Passed && check.Severity == PreflightSeverity.Error);

        if (errors > 0)
        {
            return $"{result.PassedCount}/{result.TotalCount} checks passed. {errors} blocking issue(s), {warnings} warning(s).";
        }

        if (warnings > 0)
        {
            return $"{result.PassedCount}/{result.TotalCount} checks passed with {warnings} warning(s).";
        }

        return $"{result.PassedCount}/{result.TotalCount} checks passed. Ready to package.";
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
                    OutputFolder = BuildDefaultOutputFolder(SourceFolder);
                    fixes.Add("Default output folder created.");
                }

                InstallerType = _installerCommandService.DetectInstallerType(SetupFilePath);
                if (string.IsNullOrWhiteSpace(InstallCommand) ||
                    string.IsNullOrWhiteSpace(UninstallCommand) ||
                    DetectionRuleType == IntuneDetectionRuleType.None)
                {
                    var metadata = InstallerType == InstallerType.Msi
                        ? await _msiInspectorService.InspectAsync(SetupFilePath)
                        : null;

                    var suggestion = _installerCommandService.CreateSuggestion(
                        SetupFilePath,
                        InstallerType,
                        metadata);
                    ApplySuggestion(
                        suggestion,
                        overwriteCommands: string.IsNullOrWhiteSpace(InstallCommand) || string.IsNullOrWhiteSpace(UninstallCommand),
                        overwriteRules: DetectionRuleType == IntuneDetectionRuleType.None);

                    fixes.Add("Install/uninstall and Intune rules were completed from installer template data.");
                }
            }

            UpdateValidation();
            NotifyReadinessChanged();
            await RunPreflightCoreAsync();

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

    private bool CanInstallUpdate()
    {
        return !IsBusy && IsUpdateAvailable;
    }

    private async Task CheckForUpdatesAsync()
    {
        var previousState = OperationState;
        var previousTitle = StatusTitle;
        var previousMessage = StatusMessage;

        IsBusy = true;
        UpdateStatus = "Checking GitHub releases...";
        AppendLog("Checking for app updates...");

        try
        {
            var updateInfo = await _appUpdateService.CheckForUpdatesAsync(CurrentVersion);
            _latestUpdateInfo = updateInfo;

            LatestVersion = string.IsNullOrWhiteSpace(updateInfo.LatestVersion)
                ? CurrentVersion
                : updateInfo.LatestVersion;

            UpdateChangelog = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes)
                ? "No changelog published for this release."
                : updateInfo.ReleaseNotes.Trim();

            UpdateStatus = updateInfo.Message;
            IsUpdateAvailable = updateInfo.IsUpdateAvailable;

            if (updateInfo.IsUpdateAvailable)
            {
                SetStatus(
                    OperationState.Success,
                    "Update Available",
                    $"Version {updateInfo.LatestVersion} is available. Review changelog and click Install Update.");
                AppendLog($"Update available: {updateInfo.LatestVersion}.");
            }
            else
            {
                if (updateInfo.CheckSucceeded)
                {
                    SetStatus(
                        previousState,
                        previousTitle,
                        previousMessage);
                    if (!string.IsNullOrWhiteSpace(updateInfo.Message)
                        && (updateInfo.Message.Contains("UPD-HASH", StringComparison.OrdinalIgnoreCase)
                            || updateInfo.Message.Contains("newer release", StringComparison.OrdinalIgnoreCase)))
                    {
                        AppendLog(updateInfo.Message);
                    }
                    else
                    {
                        AppendLog($"No newer app update found. Current: {CurrentVersion}, Latest: {LatestVersion}.");
                    }
                }
                else
                {
                    SetStatus(
                        previousState,
                        previousTitle,
                        previousMessage);
                    AppendLog(string.IsNullOrWhiteSpace(updateInfo.Message)
                        ? "Update check did not return a public release feed."
                        : updateInfo.Message);
                }
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update check failed: {ex.Message}";
            IsUpdateAvailable = false;
            SetStatus(previousState, previousTitle, previousMessage);
            AppendLog($"Update check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (!CanInstallUpdate())
        {
            return;
        }

        if (_latestUpdateInfo is null || !_latestUpdateInfo.IsUpdateAvailable)
        {
            SetStatus(OperationState.Error, "No Update Selected", "Check for updates first.");
            return;
        }

        IsBusy = true;
        SetStatus(
            OperationState.Running,
            "Installing Update",
            EnableSilentAppUpdates
                ? "Downloading update and starting silent installer..."
                : "Downloading and launching update installer...");
        AppendLog($"Starting update install to version {_latestUpdateInfo.LatestVersion}...");

        try
        {
            var installResult = await _appUpdateService.DownloadAndLaunchInstallerAsync(
                _latestUpdateInfo,
                new InlineProgress<string>(AppendLog),
                silentInstall: EnableSilentAppUpdates);

            if (!installResult.Success)
            {
                SetStatus(OperationState.Error, "Update Install Failed", installResult.Message);
                UpdateStatus = installResult.Message;
                return;
            }

            SetStatus(
                OperationState.Success,
                "Update Installer Started",
                EnableSilentAppUpdates
                    ? "Silent update started. The app will close so setup can replace files."
                    : "Installer launched. Finish setup, then reopen this app.");
            UpdateStatus = EnableSilentAppUpdates
                ? "Silent update installer started."
                : "Update installer started.";
            AppendLog("Installer launched. Closing app to allow update installation...");

            await Task.Delay(700);
            WpfApplication.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, "Update Install Failed", ex.Message);
            UpdateStatus = $"Update install failed: {ex.Message}";
            AppendLog($"Update install failed: {ex.Message}");
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

        if (!string.IsNullOrWhiteSpace(ResultMetadataPath) && File.Exists(ResultMetadataPath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ResultChecklistPath) && File.Exists(ResultChecklistPath))
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
        HasPackagingRun = true;
        ResultOutputPath = string.Empty;
        ResultMetadataPath = string.Empty;
        ResultChecklistPath = string.Empty;
        IntunePortalChecklist = string.Empty;
        ClearLogs();
        SetPackagingProgress("Preparing", "Validating request and starting workflow.", 0);

        SetStatus(OperationState.Running, "Packaging In Progress", "Running IntuneWinAppUtil.exe...");
        AppendLog("Packaging started.");

        try
        {
            await PersistSettingsAsync();
            SetPackagingProgress("Preflight", "Running readiness checks before packaging.", 8);
            var preflight = await RunPreflightCoreAsync();
            if (preflight.HasErrors)
            {
                SetStatus(OperationState.Error, "Preflight Failed", "Resolve preflight errors before starting package creation.");
                SetPackagingProgress("Preflight Failed", "Fix blocking issues and run packaging again.", 100);
                AppendLog("Packaging stopped because preflight reported blocking issues.");
                return;
            }

            if (preflight.HasWarnings)
            {
                AppendLog("Preflight passed with warnings. Packaging continues.");
            }
            else
            {
                AppendLog("Preflight checks passed.");
            }

            var request = BuildRequest();
            var logProgress = new InlineProgress<string>(AppendLog);
            var workflowProgress = new InlineProgress<PackagingProgressUpdate>(ApplyWorkflowProgressUpdate);

            var result = await _packagingWorkflowService.PackageAsync(
                request,
                logProgress: logProgress,
                progressUpdate: workflowProgress);
            ResultOutputPath = result.OutputPackagePath ?? string.Empty;
            ResultMetadataPath = result.OutputMetadataPath ?? string.Empty;
            ResultChecklistPath = result.OutputChecklistPath ?? string.Empty;
            IntunePortalChecklist = result.IntunePortalChecklist;

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
                SetPackagingProgress("Completed", "Package created successfully.", 100);
                AppendLog("Packaging completed successfully.");
            }
            else
            {
                SetStatus(OperationState.Error, "Packaging Failed", result.Message);
                SetPackagingProgress("Failed", result.Message, 100);
                AppendLog($"Packaging failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, "Unexpected Error", ex.Message);
            SetPackagingProgress("Unexpected Error", ex.Message, 100);
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
            UseSmartSourceStaging = profile.Configuration.UseSmartSourceStaging;
            ApplyIntuneRules(profile.Configuration.IntuneRules);
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
            UseSmartSourceStaging = true;
            InstallContext = IntuneInstallContext.System;
            RestartBehavior = IntuneRestartBehavior.DetermineBehaviorBasedOnReturnCodes;
            MaxRunTimeMinutes = 60;
            AppliedTemplateName = string.Empty;
            TemplateGuidance = string.Empty;
            RequireSilentSwitchReview = false;
            SilentSwitchesVerified = false;
            DetectionRuleType = IntuneDetectionRuleType.None;
            DetectionMsiProductCode = string.Empty;
            DetectionMsiProductVersion = string.Empty;
            DetectionFilePath = string.Empty;
            DetectionFileOrFolderName = string.Empty;
            DetectionFileCheck32BitOn64System = false;
            DetectionFileOperator = IntuneDetectionOperator.Exists;
            DetectionFileValue = string.Empty;
            DetectionRegistryHive = "HKEY_LOCAL_MACHINE";
            DetectionRegistryKeyPath = string.Empty;
            DetectionRegistryValueName = string.Empty;
            DetectionRegistryCheck32BitOn64System = false;
            DetectionRegistryOperator = IntuneDetectionOperator.Exists;
            DetectionRegistryValue = string.Empty;
            DetectionScriptBody = string.Empty;
            DetectionScriptRunAs32BitOn64System = false;
            DetectionScriptEnforceSignatureCheck = false;
            RequirementOperatingSystemArchitecture = "x64";
            RequirementMinimumOperatingSystem = "Windows 10 1607";
            RequirementMinimumFreeDiskSpaceMb = 0;
            RequirementMinimumMemoryMb = 0;
            RequirementMinimumCpuSpeedMhz = 0;
            RequirementMinimumLogicalProcessors = 0;
            RequirementScriptBody = string.Empty;
            RequirementScriptRunAs32BitOn64System = false;
            RequirementScriptEnforceSignatureCheck = false;
            MsiMetadataSummary = string.Empty;
            ResultOutputPath = string.Empty;
            ResultMetadataPath = string.Empty;
            ResultChecklistPath = string.Empty;
            IntunePortalChecklist = string.Empty;
            HasPackagingRun = false;
            HasPreflightRun = false;
            PreflightSummary = "Run preflight checks to verify tool health, folders, permissions, and disk space.";
            PreflightChecks.Clear();
            PackagingProgressPercentage = 0;
            IsPackagingProgressIndeterminate = false;
            PackagingProgressStep = "Ready";
            PackagingProgressDetail = "Waiting to start.";
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

        var metadataToOpen = ResultMetadataPath;
        if (!string.IsNullOrWhiteSpace(metadataToOpen) && File.Exists(metadataToOpen))
        {
            var metadataParent = Path.GetDirectoryName(metadataToOpen);
            if (!string.IsNullOrWhiteSpace(metadataParent))
            {
                _dialogService.OpenFolder(metadataParent);
                return;
            }
        }

        var checklistToOpen = ResultChecklistPath;
        if (!string.IsNullOrWhiteSpace(checklistToOpen) && File.Exists(checklistToOpen))
        {
            var checklistParent = Path.GetDirectoryName(checklistToOpen);
            if (!string.IsNullOrWhiteSpace(checklistParent))
            {
                _dialogService.OpenFolder(checklistParent);
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
            OutputFolder = BuildDefaultOutputFolder(SourceFolder);
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
            ApplySuggestion(suggestion);
        }
        else if (InstallerType != InstallerType.Unknown)
        {
            MsiMetadataSummary = string.Empty;

            var suggestion = _installerCommandService.CreateSuggestion(filePath, InstallerType);
            ApplySuggestion(suggestion);
        }
        else
        {
            MsiMetadataSummary = string.Empty;
            InstallCommand = string.Empty;
            UninstallCommand = string.Empty;
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
            LastSetupFilePath = SetupFilePath,
            UseLowImpactMode = UseLowImpactMode,
            EnableSilentAppUpdates = EnableSilentAppUpdates
        };

        await _settingsService.SaveAsync(settings);
    }

    private async Task PersistSettingsSafeAsync()
    {
        try
        {
            await PersistSettingsAsync();
        }
        catch
        {
            // Non-blocking settings persistence for UI toggles.
        }
    }

    private PackagingRequest BuildRequest()
    {
        return new PackagingRequest
        {
            IntuneWinAppUtilPath = IntuneWinAppUtilPath,
            InstallerType = InstallerType,
            UseLowImpactMode = UseLowImpactMode,
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
            UninstallCommand = UninstallCommand,
            UseSmartSourceStaging = UseSmartSourceStaging,
            IntuneRules = BuildIntuneRules()
        };
    }

    private IntuneWin32AppRules BuildIntuneRules()
    {
        return new IntuneWin32AppRules
        {
            InstallContext = InstallContext,
            RestartBehavior = RestartBehavior,
            MaxRunTimeMinutes = MaxRunTimeMinutes,
            RequireSilentSwitchReview = RequireSilentSwitchReview,
            SilentSwitchesVerified = RequireSilentSwitchReview ? SilentSwitchesVerified : true,
            AppliedTemplateName = AppliedTemplateName,
            TemplateGuidance = TemplateGuidance,
            Requirements = BuildRequirementRules(),
            DetectionRule = BuildDetectionRule()
        };
    }

    private IntuneRequirementRules BuildRequirementRules()
    {
        return new IntuneRequirementRules
        {
            OperatingSystemArchitecture = RequirementOperatingSystemArchitecture,
            MinimumOperatingSystem = RequirementMinimumOperatingSystem,
            MinimumFreeDiskSpaceMb = RequirementMinimumFreeDiskSpaceMb,
            MinimumMemoryMb = RequirementMinimumMemoryMb,
            MinimumCpuSpeedMhz = RequirementMinimumCpuSpeedMhz,
            MinimumLogicalProcessors = RequirementMinimumLogicalProcessors,
            RequirementScriptBody = RequirementScriptBody,
            RequirementScriptRunAs32BitOn64System = RequirementScriptRunAs32BitOn64System,
            RequirementScriptEnforceSignatureCheck = RequirementScriptEnforceSignatureCheck
        };
    }

    private IntuneDetectionRule BuildDetectionRule()
    {
        return new IntuneDetectionRule
        {
            RuleType = DetectionRuleType,
            Msi = new MsiDetectionRule
            {
                ProductCode = DetectionMsiProductCode,
                ProductVersion = DetectionMsiProductVersion
            },
            File = new FileDetectionRule
            {
                Path = DetectionFilePath,
                FileOrFolderName = DetectionFileOrFolderName,
                Check32BitOn64System = DetectionFileCheck32BitOn64System,
                Operator = DetectionFileOperator,
                Value = DetectionFileValue
            },
            Registry = new RegistryDetectionRule
            {
                Hive = DetectionRegistryHive,
                KeyPath = DetectionRegistryKeyPath,
                ValueName = DetectionRegistryValueName,
                Check32BitOn64System = DetectionRegistryCheck32BitOn64System,
                Operator = DetectionRegistryOperator,
                Value = DetectionRegistryValue
            },
            Script = new ScriptDetectionRule
            {
                ScriptBody = DetectionScriptBody,
                RunAs32BitOn64System = DetectionScriptRunAs32BitOn64System,
                EnforceSignatureCheck = DetectionScriptEnforceSignatureCheck
            }
        };
    }

    private void ApplySuggestion(
        CommandSuggestion suggestion,
        bool overwriteCommands = true,
        bool overwriteRules = true)
    {
        if (overwriteCommands)
        {
            InstallCommand = suggestion.InstallCommand;
            UninstallCommand = suggestion.UninstallCommand;
        }

        if (overwriteRules)
        {
            ApplyIntuneRules(suggestion.SuggestedRules);
        }
    }

    private void ApplyIntuneRules(IntuneWin32AppRules rules)
    {
        InstallContext = rules.InstallContext;
        RestartBehavior = rules.RestartBehavior;
        MaxRunTimeMinutes = rules.MaxRunTimeMinutes;
        RequireSilentSwitchReview = rules.RequireSilentSwitchReview;
        SilentSwitchesVerified = rules.RequireSilentSwitchReview ? rules.SilentSwitchesVerified : true;
        AppliedTemplateName = rules.AppliedTemplateName;
        TemplateGuidance = rules.TemplateGuidance;
        ApplyRequirementRules(rules.Requirements);
        ApplyDetectionRule(rules.DetectionRule);
    }

    private void ApplyRequirementRules(IntuneRequirementRules requirements)
    {
        RequirementOperatingSystemArchitecture = string.IsNullOrWhiteSpace(requirements.OperatingSystemArchitecture)
            ? "x64"
            : requirements.OperatingSystemArchitecture;
        RequirementMinimumOperatingSystem = string.IsNullOrWhiteSpace(requirements.MinimumOperatingSystem)
            ? "Windows 10 1607"
            : requirements.MinimumOperatingSystem;
        RequirementMinimumFreeDiskSpaceMb = requirements.MinimumFreeDiskSpaceMb;
        RequirementMinimumMemoryMb = requirements.MinimumMemoryMb;
        RequirementMinimumCpuSpeedMhz = requirements.MinimumCpuSpeedMhz;
        RequirementMinimumLogicalProcessors = requirements.MinimumLogicalProcessors;
        RequirementScriptBody = requirements.RequirementScriptBody;
        RequirementScriptRunAs32BitOn64System = requirements.RequirementScriptRunAs32BitOn64System;
        RequirementScriptEnforceSignatureCheck = requirements.RequirementScriptEnforceSignatureCheck;
    }

    private void ApplyDetectionRule(IntuneDetectionRule detectionRule)
    {
        DetectionRuleType = detectionRule.RuleType;
        DetectionMsiProductCode = detectionRule.Msi.ProductCode;
        DetectionMsiProductVersion = detectionRule.Msi.ProductVersion;
        DetectionFilePath = detectionRule.File.Path;
        DetectionFileOrFolderName = detectionRule.File.FileOrFolderName;
        DetectionFileCheck32BitOn64System = detectionRule.File.Check32BitOn64System;
        DetectionFileOperator = detectionRule.File.Operator;
        DetectionFileValue = detectionRule.File.Value;
        DetectionRegistryHive = string.IsNullOrWhiteSpace(detectionRule.Registry.Hive)
            ? "HKEY_LOCAL_MACHINE"
            : detectionRule.Registry.Hive;
        DetectionRegistryKeyPath = detectionRule.Registry.KeyPath;
        DetectionRegistryValueName = detectionRule.Registry.ValueName;
        DetectionRegistryCheck32BitOn64System = detectionRule.Registry.Check32BitOn64System;
        DetectionRegistryOperator = detectionRule.Registry.Operator;
        DetectionRegistryValue = detectionRule.Registry.Value;
        DetectionScriptBody = detectionRule.Script.ScriptBody;
        DetectionScriptRunAs32BitOn64System = detectionRule.Script.RunAs32BitOn64System;
        DetectionScriptEnforceSignatureCheck = detectionRule.Script.EnforceSignatureCheck;
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

    private void InvalidatePreflightIfNeeded()
    {
        if (!HasPreflightRun)
        {
            return;
        }

        HasPreflightRun = false;
        PreflightSummary = "Configuration changed. Run preflight checks again.";
        PreflightChecks.Clear();
    }

    private void SetStatus(OperationState state, string title, string message)
    {
        OperationState = state;
        StatusTitle = title;
        StatusMessage = message;
    }

    private void SetPackagingProgress(string step, string detail, double percentage, bool isIndeterminate = false)
    {
        PackagingProgressStep = step;
        PackagingProgressDetail = detail;
        IsPackagingProgressIndeterminate = isIndeterminate;

        if (!isIndeterminate)
        {
            PackagingProgressPercentage = Math.Clamp(percentage, 0, 100);
        }
    }

    private void ApplyWorkflowProgressUpdate(PackagingProgressUpdate update)
    {
        if (update is null)
        {
            return;
        }

        void Apply()
        {
            if (!string.IsNullOrWhiteSpace(update.Step))
            {
                PackagingProgressStep = update.Step;
            }

            if (!string.IsNullOrWhiteSpace(update.Detail))
            {
                PackagingProgressDetail = update.Detail;
            }

            IsPackagingProgressIndeterminate = update.IsIndeterminate;

            if (!update.IsIndeterminate)
            {
                var normalized = Math.Clamp(update.Percentage, 0, 100);
                PackagingProgressPercentage = Math.Max(PackagingProgressPercentage, normalized);
            }
        }

        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        _ = dispatcher.InvokeAsync(Apply);
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

    private static string ResolveCurrentVersion()
    {
        var informational = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion?.ToString(3) ?? "1.0.0";
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

    private static string BuildDefaultOutputFolder(string sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder))
        {
            return string.Empty;
        }

        var fullSource = Path.GetFullPath(sourceFolder);
        var parent = Path.GetDirectoryName(fullSource);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return Path.Combine(fullSource, "IntuneWinOutput");
        }

        var folderName = Path.GetFileName(fullSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Path.Combine(parent, $"{folderName}-IntuneWinOutput");
    }

    private bool IsSupportedInstallerFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return _installerCommandService.DetectInstallerType(path) != InstallerType.Unknown;
    }

    private string? FindDroppedInstaller(IEnumerable<string> droppedPaths)
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
                    .FirstOrDefault(file => IsSupportedInstallerFile(file));

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







