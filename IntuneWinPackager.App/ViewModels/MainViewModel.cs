using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
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
    private readonly IPackageCatalogService _packageCatalogService;
    private readonly IPackageProfileStoreService _packageProfileStoreService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IThemeService _themeService;
    private readonly IDensityService _densityService;

    private readonly ObservableCollection<string> _logs = new();
    private readonly ReadOnlyObservableCollection<string> _readonlyLogs;
    private readonly ConcurrentQueue<string> _pendingLogQueue = new();
    private readonly WpfDispatcherTimer _logFlushTimer;
    private readonly WpfDispatcherTimer _updateCheckTimer;

    private const int MaxVisibleLogLines = 500;
    private const int MaxLogFlushBatchSize = 40;
    private static readonly TimeSpan PreflightReuseWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BackgroundUpdateCheckInterval = TimeSpan.FromHours(4);
    private static readonly TimeSpan MinimumUpdateRecheckInterval = TimeSpan.FromMinutes(10);

    private CancellationTokenSource? _msiInspectionCancellation;
    private bool _isInitialized;
    private bool _suppressSetupRefresh;
    private bool _isApplyingPreferences;
    private bool _isCheckingForUpdates;
    private AppUpdateInfo? _latestUpdateInfo;
    private DateTimeOffset? _lastUpdateCheckCompletedAtUtc;
    private DateTimeOffset? _lastPreflightCompletedAtUtc;
    private CancellationTokenSource? _catalogSearchCancellation;
    private CancellationTokenSource? _catalogDetailsCancellation;
    private List<CatalogPackageProfile> _catalogProfiles = [];
    private CatalogSelectionContext? _activeCatalogSelectionContext;

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
    private string statusTitle = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

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
    private string selectedLanguage = "English";

    [ObservableProperty]
    private string selectedTheme = "Light";

    [ObservableProperty]
    private string selectedDensity = "Comfortable";

    [ObservableProperty]
    private double packagingProgressPercentage;

    [ObservableProperty]
    private bool isPackagingProgressIndeterminate;

    [ObservableProperty]
    private string packagingProgressStep = string.Empty;

    [ObservableProperty]
    private string packagingProgressDetail = string.Empty;

    [ObservableProperty]
    private string currentVersion = "1.0.0";

    [ObservableProperty]
    private string latestVersion = "-";

    [ObservableProperty]
    private string updateStatus = string.Empty;

    [ObservableProperty]
    private string updateChangelog = string.Empty;

    [ObservableProperty]
    private bool isUpdateAvailable;

    [ObservableProperty]
    private bool hasPackagingRun;

    [ObservableProperty]
    private bool hasPreflightRun;

    [ObservableProperty]
    private string preflightSummary = string.Empty;

    [ObservableProperty]
    private bool suggestionUsedKnowledgeCache;

    [ObservableProperty]
    private string switchVerificationStatus = string.Empty;

    [ObservableProperty]
    private string packageCatalogSearchTerm = string.Empty;

    [ObservableProperty]
    private bool includeWingetCatalogSource = true;

    [ObservableProperty]
    private bool includeChocolateyCatalogSource = true;

    [ObservableProperty]
    private bool isPackageCatalogBusy;

    [ObservableProperty]
    private bool isPackageCatalogDetailBusy;

    [ObservableProperty]
    private bool isCatalogDownloadBusy;

    [ObservableProperty]
    private string packageCatalogStatus = string.Empty;

    [ObservableProperty]
    private bool showStoreAdvancedDetails;

    [ObservableProperty]
    private PackageCatalogEntry? selectedCatalogEntry;

    [ObservableProperty]
    private PackageCatalogEntry? catalogEntryDetails;

    [ObservableProperty]
    private int selectedMainTabIndex;

    [ObservableProperty]
    private bool installerParameterProbeDetected;

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
        IPackageCatalogService packageCatalogService,
        IPackageProfileStoreService packageProfileStoreService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        IThemeService themeService,
        IDensityService densityService)
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
        _packageCatalogService = packageCatalogService;
        _packageProfileStoreService = packageProfileStoreService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _themeService = themeService;
        _densityService = densityService;

        _readonlyLogs = new ReadOnlyObservableCollection<string>(_logs);

        _logFlushTimer = new WpfDispatcherTimer(WpfDispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _logFlushTimer.Tick += (_, _) => FlushPendingLogs(MaxLogFlushBatchSize);
        _logFlushTimer.Start();

        _updateCheckTimer = new WpfDispatcherTimer(WpfDispatcherPriority.Background)
        {
            Interval = BackgroundUpdateCheckInterval
        };
        _updateCheckTimer.Tick += (_, _) => _ = CheckForUpdatesInBackgroundAsync();
        _updateCheckTimer.Start();

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

        PackageCatalogResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCatalogResults));
        };

        foreach (var preset in _installerCommandService.GetExeSilentPresets())
        {
            ExePresets.Add(preset);
        }

        SelectedPreset = ExePresets.FirstOrDefault();
        SelectedLanguage = _localizationService.CurrentLanguage;
        SelectedTheme = _themeService.CurrentTheme;
        SelectedDensity = _densityService.CurrentDensity;

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
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, CanDeleteProfile);
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, CanInstallUpdate);
        SearchCatalogCommand = new AsyncRelayCommand(SearchCatalogAsync, CanSearchCatalog);
        DownloadCatalogEntryCommand = new AsyncRelayCommand(DownloadCatalogEntryAsync, CanDownloadCatalogEntrySelection);
        UseCatalogEntryCommand = new AsyncRelayCommand(UseCatalogEntryAsync, CanUseCatalogEntrySelection);
        OpenCatalogHomepageCommand = new RelayCommand(OpenCatalogHomepage, CanOpenCatalogHomepageLink);

        _localizationService.LanguageChanged += HandleLanguageChanged;

        CurrentVersion = ResolveCurrentVersion();
        LatestVersion = CurrentVersion;
        StatusTitle = T("Vm.Status.ReadyTitle");
        StatusMessage = T("Vm.Status.SelectInstallerMessage");
        UpdateChangelog = T("Vm.Update.ChangelogDefault");
        UpdateStatus = T("Vm.Update.NotChecked");
        PreflightSummary = T("Vm.Preflight.DefaultSummary");
        PackagingProgressStep = T("Vm.Progress.ReadyStep");
        PackagingProgressDetail = T("Vm.Progress.ReadyDetail");
        PackageCatalogStatus = T("Vm.Store.Ready");
        RefreshSwitchVerificationStatus();
    }

    public ObservableCollection<string> ValidationErrors { get; } = new();

    public ObservableCollection<PackageHistoryEntry> RecentHistory { get; } = new();

    public ObservableCollection<SilentInstallPreset> ExePresets { get; } = new();

    public ObservableCollection<string> AvailableProfiles { get; } = new();

    public ObservableCollection<PreflightCheck> PreflightChecks { get; } = new();

    public ObservableCollection<PackageCatalogEntry> PackageCatalogResults { get; } = new();

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

    public IReadOnlyList<string> LanguageOptions => _localizationService.LanguageOptions;

    public IReadOnlyList<string> ThemeOptions => _themeService.ThemeOptions;

    public IReadOnlyList<string> DensityOptions => _densityService.DensityOptions;

    public bool IsMsiInstaller => InstallerType == InstallerType.Msi;

    public bool IsExeInstaller => InstallerType == InstallerType.Exe;

    public bool IsMsiDetectionRule => DetectionRuleType == IntuneDetectionRuleType.MsiProductCode;

    public bool IsFileDetectionRule => DetectionRuleType == IntuneDetectionRuleType.File;

    public bool IsRegistryDetectionRule => DetectionRuleType == IntuneDetectionRuleType.Registry;

    public bool IsScriptDetectionRule => DetectionRuleType == IntuneDetectionRuleType.Script;

    public bool IsDetectionFileComparisonOperator => DetectionFileOperator != IntuneDetectionOperator.Exists;

    public bool IsDetectionRegistryComparisonOperator => DetectionRegistryOperator != IntuneDetectionOperator.Exists;

    public bool IsSilentSwitchReviewVisible => InstallerType == InstallerType.Exe && RequireSilentSwitchReview;

    public bool IsSwitchVerificationStatusVisible => InstallerType == InstallerType.Exe;

    public string DetectionSelectionHint => InstallerType switch
    {
        InstallerType.Msi => T("Vm.Detection.SelectionHint.Msi"),
        InstallerType.Exe => T("Vm.Detection.SelectionHint.Exe"),
        InstallerType.AppxMsix => T("Vm.Detection.SelectionHint.Appx"),
        InstallerType.Script => T("Vm.Detection.SelectionHint.Script"),
        _ => T("Vm.Detection.SelectionHint.Unknown")
    };

    public string DetectionRuleTypeGuidance => DetectionRuleType switch
    {
        IntuneDetectionRuleType.None => T("Vm.Detection.Guidance.None"),
        IntuneDetectionRuleType.MsiProductCode => T("Vm.Detection.Guidance.Msi"),
        IntuneDetectionRuleType.File => T("Vm.Detection.Guidance.File"),
        IntuneDetectionRuleType.Registry => T("Vm.Detection.Guidance.Registry"),
        IntuneDetectionRuleType.Script => T("Vm.Detection.Guidance.Script"),
        _ => T("Vm.Detection.Guidance.None")
    };

    public string DetectionFileOperatorGuidance => DetectionFileOperator == IntuneDetectionOperator.Exists
        ? T("Vm.Detection.OperatorHint.FileExists")
        : T("Vm.Detection.OperatorHint.FileCompare");

    public string DetectionRegistryOperatorGuidance => DetectionRegistryOperator == IntuneDetectionOperator.Exists
        ? T("Vm.Detection.OperatorHint.RegistryExists")
        : T("Vm.Detection.OperatorHint.RegistryCompare");

    public string DetectionRegistryValueNameLabel => IsDetectionRegistryComparisonOperator
        ? T("Ui.ValueName")
        : T("Ui.ValueNameOptional");

    public bool HasValidationErrors => ValidationErrors.Count > 0;

    public bool IsConfigurationValid => !HasValidationErrors;

    public bool HasPreflightErrors => PreflightChecks.Any(check => !check.Passed && check.Severity == PreflightSeverity.Error);

    public bool HasPreflightWarnings => PreflightChecks.Any(check => !check.Passed && check.Severity == PreflightSeverity.Warning);

    public bool IsPreflightReady => HasPreflightRun && !HasPreflightErrors;

    public bool IsUpdateNotificationVisible => IsUpdateAvailable;

    public bool HasCatalogResults => PackageCatalogResults.Count > 0;

    public bool HasCatalogSelection => SelectedCatalogEntry is not null;

    public bool HasCatalogDetails => CatalogEntryDetails is not null;

    public string UpdateNotificationText => TF("Vm.Update.NotificationFormat", LatestVersion, CurrentVersion);

    public string InstallerTypeDisplay => InstallerType switch
    {
        InstallerType.Msi => T("Vm.InstallerType.Msi"),
        InstallerType.Exe => T("Vm.InstallerType.Exe"),
        InstallerType.AppxMsix => T("Vm.InstallerType.Appx"),
        InstallerType.Script => T("Vm.InstallerType.Script"),
        _ => T("Vm.InstallerType.None")
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
        $"{T("Vm.Readiness.ToolPath")}: {(IsToolPathValid ? T("Vm.Readiness.Ready") : T("Vm.Readiness.Missing"))}{Environment.NewLine}" +
        $"{T("Vm.Readiness.InstallerFile")}: {(IsSetupFileValid ? T("Vm.Readiness.Ready") : T("Vm.Readiness.Missing"))}{Environment.NewLine}" +
        $"{T("Vm.Readiness.SourceFolder")}: {(IsSourceFolderValid ? T("Vm.Readiness.Ready") : T("Vm.Readiness.Missing"))}{Environment.NewLine}" +
        $"{T("Vm.Readiness.OutputFolder")}: {(IsOutputFolderValid ? T("Vm.Readiness.Ready") : T("Vm.Readiness.Missing"))}{Environment.NewLine}" +
        $"{T("Vm.Readiness.DetectionRule")}: {(DetectionRuleType == IntuneDetectionRuleType.None ? T("Vm.Readiness.Missing") : T("Vm.Readiness.Configured"))}{Environment.NewLine}" +
        $"{T("Vm.Readiness.SmartStaging")}: {(UseSmartSourceStaging ? T("Vm.Readiness.Enabled") : T("Vm.Readiness.Disabled"))}";

    public string NextStepHint
    {
        get
        {
            if (!IsToolPathValid)
            {
                return T("Vm.NextStep.ToolMissing");
            }

            if (!IsSetupFileValid)
            {
                return T("Vm.NextStep.SetupMissing");
            }

            if (!IsSourceFolderValid)
            {
                return T("Vm.NextStep.SourceMissing");
            }

            if (!IsOutputFolderValid)
            {
                return T("Vm.NextStep.OutputMissing");
            }

            if (DetectionRuleType == IntuneDetectionRuleType.None)
            {
                return T("Vm.NextStep.DetectionMissing");
            }

            if (IsSilentSwitchReviewVisible && !SilentSwitchesVerified)
            {
                return T("Vm.NextStep.SwitchVerify");
            }

            if (HasValidationErrors)
            {
                return ValidationErrors.FirstOrDefault() ?? T("Vm.NextStep.ResolveValidation");
            }

            if (HasPreflightRun && HasPreflightErrors)
            {
                var blocking = PreflightChecks.FirstOrDefault(check => !check.Passed && check.Severity == PreflightSeverity.Error);
                return blocking is null
                    ? T("Vm.NextStep.PreflightRunAgain")
                    : string.Format(CultureInfo.CurrentCulture, T("Vm.NextStep.PreflightBlocked"), blocking.Title, blocking.Message);
            }

            return T("Vm.NextStep.ReadyToPackage");
        }
    }

    public string PackagingProgressLabel => IsPackagingProgressIndeterminate
        ? PackagingProgressStep
        : $"{PackagingProgressStep} ({PackagingProgressPercentage:0}%)";

    public string CurrentVersionDisplay => string.Format(
        CultureInfo.CurrentCulture,
        T("Vm.Version.CurrentFormat"),
        CurrentVersion);

    public string LatestVersionDisplay => string.Format(
        CultureInfo.CurrentCulture,
        T("Vm.Version.LatestFormat"),
        LatestVersion);

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

    public IAsyncRelayCommand DeleteProfileCommand { get; }

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand InstallUpdateCommand { get; }

    public IAsyncRelayCommand SearchCatalogCommand { get; }

    public IAsyncRelayCommand DownloadCatalogEntryCommand { get; }

    public IAsyncRelayCommand UseCatalogEntryCommand { get; }

    public IRelayCommand OpenCatalogHomepageCommand { get; }

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
        await ReloadCatalogProfilesAsync();

        UpdateValidation();

        _ = CheckForUpdatesOnStartupAsync();
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
            SetStatus(
                OperationState.Error,
                T("Vm.Status.UnsupportedDropTitle"),
                T("Vm.Status.UnsupportedDropMessage"));
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
        RefreshSwitchVerificationStatus();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnSilentSwitchesVerifiedChanged(bool value)
    {
        if (value && InstallerType == InstallerType.Exe)
        {
            TrySaveVerifiedInstallerKnowledge();
        }

        InvalidatePreflightIfNeeded();
        RefreshSwitchVerificationStatus();
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
        OnPropertyChanged(nameof(DetectionRuleTypeGuidance));
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
        OnPropertyChanged(nameof(IsDetectionFileComparisonOperator));
        OnPropertyChanged(nameof(DetectionFileOperatorGuidance));
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
        OnPropertyChanged(nameof(IsDetectionRegistryComparisonOperator));
        OnPropertyChanged(nameof(DetectionRegistryOperatorGuidance));
        OnPropertyChanged(nameof(DetectionRegistryValueNameLabel));
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

        if (_activeCatalogSelectionContext is not null &&
            !string.Equals(_activeCatalogSelectionContext.InstallerPath, value, StringComparison.OrdinalIgnoreCase))
        {
            _activeCatalogSelectionContext = null;
        }

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
        OnPropertyChanged(nameof(IsSwitchVerificationStatusVisible));
        OnPropertyChanged(nameof(DetectionRuleTypeGuidance));
        OnPropertyChanged(nameof(DetectionSelectionHint));
        OnPropertyChanged(nameof(InstallerTypeDisplay));
        OnPropertyChanged(nameof(InstallerTypeBrush));
        RefreshSwitchVerificationStatus();
        UpdateValidation();
        NotifyReadinessChanged();
    }

    partial void OnProfileNameChanged(string value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnPackageCatalogSearchTermChanged(string value)
    {
        SearchCatalogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIncludeWingetCatalogSourceChanged(bool value)
    {
        SearchCatalogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIncludeChocolateyCatalogSourceChanged(bool value)
    {
        SearchCatalogCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPackageCatalogBusyChanged(bool value)
    {
        SearchCatalogCommand.NotifyCanExecuteChanged();
        DownloadCatalogEntryCommand.NotifyCanExecuteChanged();
        UseCatalogEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPackageCatalogDetailBusyChanged(bool value)
    {
        DownloadCatalogEntryCommand.NotifyCanExecuteChanged();
        UseCatalogEntryCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCatalogEntryChanged(PackageCatalogEntry? value)
    {
        OnPropertyChanged(nameof(HasCatalogSelection));
        DownloadCatalogEntryCommand.NotifyCanExecuteChanged();
        UseCatalogEntryCommand.NotifyCanExecuteChanged();
        OpenCatalogHomepageCommand.NotifyCanExecuteChanged();

        if (value is null ||
            _activeCatalogSelectionContext is null ||
            value.Source != _activeCatalogSelectionContext.Source ||
            !value.PackageId.Equals(_activeCatalogSelectionContext.PackageId, StringComparison.OrdinalIgnoreCase) ||
            !IsCatalogVersionMatch(value.Version, _activeCatalogSelectionContext.Version))
        {
            _activeCatalogSelectionContext = null;
        }

        _ = LoadCatalogDetailsAsync(value);
    }

    partial void OnCatalogEntryDetailsChanged(PackageCatalogEntry? value)
    {
        OnPropertyChanged(nameof(HasCatalogDetails));
        DownloadCatalogEntryCommand.NotifyCanExecuteChanged();
        UseCatalogEntryCommand.NotifyCanExecuteChanged();
        OpenCatalogHomepageCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCatalogDownloadBusyChanged(bool value)
    {
        DownloadCatalogEntryCommand.NotifyCanExecuteChanged();
        UseCatalogEntryCommand.NotifyCanExecuteChanged();
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
        DeleteProfileCommand.NotifyCanExecuteChanged();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateCommand.NotifyCanExecuteChanged();
        SearchCatalogCommand.NotifyCanExecuteChanged();
        DownloadCatalogEntryCommand.NotifyCanExecuteChanged();
        UseCatalogEntryCommand.NotifyCanExecuteChanged();
        OpenCatalogHomepageCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(IsUpdateNotificationVisible));
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

    partial void OnShowStoreAdvancedDetailsChanged(bool value)
    {
        _ = PersistSettingsSafeAsync();
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        var normalized = _localizationService.NormalizeLanguage(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            SelectedLanguage = normalized;
            return;
        }

        _localizationService.SetLanguage(normalized);

        if (_isApplyingPreferences)
        {
            return;
        }

        _ = PersistSettingsSafeAsync();
    }

    partial void OnSelectedThemeChanged(string value)
    {
        var normalized = _themeService.NormalizeTheme(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            SelectedTheme = normalized;
            return;
        }

        _themeService.SetTheme(normalized);

        if (_isApplyingPreferences)
        {
            return;
        }

        _ = PersistSettingsSafeAsync();
    }

    partial void OnSelectedDensityChanged(string value)
    {
        var normalized = _densityService.NormalizeDensity(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            SelectedDensity = normalized;
            return;
        }

        _densityService.SetDensity(normalized);

        if (_isApplyingPreferences)
        {
            return;
        }

        _ = PersistSettingsSafeAsync();
    }

    partial void OnCurrentVersionChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentVersionDisplay));
        OnPropertyChanged(nameof(UpdateNotificationText));
    }

    partial void OnLatestVersionChanged(string value)
    {
        OnPropertyChanged(nameof(LatestVersionDisplay));
        OnPropertyChanged(nameof(UpdateNotificationText));
    }

    partial void OnPackageCatalogStatusChanged(string value)
    {
        OnPropertyChanged(nameof(HasCatalogResults));
        OnPropertyChanged(nameof(HasCatalogSelection));
        OnPropertyChanged(nameof(HasCatalogDetails));
    }

    private async Task LoadSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();

        _isApplyingPreferences = true;
        try
        {
            var language = _localizationService.DisplayNameFromCode(settings.UiLanguage);
            _localizationService.SetLanguage(language);
            SelectedLanguage = language;

            var theme = _themeService.DisplayNameFromCode(settings.UiTheme);
            _themeService.SetTheme(theme);
            SelectedTheme = theme;

            var density = _densityService.DisplayNameFromCode(settings.UiDensity);
            _densityService.SetDensity(density);
            SelectedDensity = density;
        }
        finally
        {
            _isApplyingPreferences = false;
        }

        IntuneWinAppUtilPath = settings.IntuneWinAppUtilPath;
        SourceFolder = settings.LastSourceFolder;
        OutputFolder = settings.LastOutputFolder;
        UseLowImpactMode = settings.UseLowImpactMode;
        EnableSilentAppUpdates = settings.EnableSilentAppUpdates;
        ShowStoreAdvancedDetails = settings.StoreShowAdvancedDetails;
        _lastUpdateCheckCompletedAtUtc = settings.LastUpdateCheckUtc;

        if (!string.IsNullOrWhiteSpace(settings.LastKnownLatestVersion) &&
            IsVersionGreaterForNotification(settings.LastKnownLatestVersion, CurrentVersion))
        {
            LatestVersion = settings.LastKnownLatestVersion;
            IsUpdateAvailable = true;
            UpdateStatus = TF("Vm.Update.NotificationFormat", LatestVersion, CurrentVersion);
        }

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
        else if (!string.IsNullOrWhiteSpace(SelectedProfileName) && !AvailableProfiles.Contains(SelectedProfileName))
        {
            SelectedProfileName = null;
        }

        DeleteProfileCommand.NotifyCanExecuteChanged();
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
            SetStatus(
                OperationState.Idle,
                T("Vm.Status.ToolLocatedTitle"),
                T("Vm.Status.ToolLocatedMessage"));
            return;
        }

        SetStatus(
            OperationState.Error,
            T("Vm.Status.ToolNotFoundTitle"),
            T("Vm.Status.ToolNotFoundMessage"));
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
                    installResult.AlreadyInstalled
                        ? T("Vm.Status.ToolReadyTitle")
                        : T("Vm.Status.ToolInstalledTitle"),
                    installResult.AlreadyInstalled
                        ? T("Vm.Status.ToolReadyMessage")
                        : T("Vm.Status.ToolInstalledMessage"));
            }
            else
            {
                SetStatus(
                    OperationState.Error,
                    T("Vm.Status.ToolInstallFailedTitle"),
                    installResult.Message);
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, T("Vm.Status.InstallErrorTitle"), ex.Message);
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

        SetStatus(
            OperationState.Idle,
            T("Vm.Status.PresetAppliedTitle"),
            TF("Vm.Status.PresetAppliedMessage", SelectedPreset?.Name ?? T("Vm.Preset.Custom")));
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
                SetStatus(
                    OperationState.Error,
                    T("Vm.Status.PreflightFailedTitle"),
                    T("Vm.Status.PreflightFailedMessage"));
            }
            else if (result.HasWarnings)
            {
                SetStatus(
                    OperationState.Idle,
                    T("Vm.Status.PreflightCompleteTitle"),
                    T("Vm.Status.PreflightCompleteMessage"));
            }
            else
            {
                SetStatus(
                    OperationState.Success,
                    T("Vm.Status.PreflightPassedTitle"),
                    T("Vm.Status.PreflightPassedMessage"));
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, T("Vm.Status.PreflightErrorTitle"), ex.Message);
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
        _lastPreflightCompletedAtUtc = DateTimeOffset.UtcNow;
        HasPreflightRun = true;
        PreflightChecks.Clear();

        foreach (var check in result.Checks)
        {
            var localizedCheck = LocalizePreflightCheck(check);
            PreflightChecks.Add(localizedCheck);

            var label = check.Passed
                ? "PRECHECK OK"
                : check.Severity == PreflightSeverity.Warning
                    ? "PRECHECK WARN"
                    : "PRECHECK ERROR";

            AppendLog($"{label} | {localizedCheck.Title}: {localizedCheck.Message}");
        }

        PreflightSummary = BuildPreflightSummary(result);
        OnPropertyChanged(nameof(HasPreflightErrors));
        OnPropertyChanged(nameof(HasPreflightWarnings));
        OnPropertyChanged(nameof(IsPreflightReady));
        OnPropertyChanged(nameof(NextStepHint));
    }

    private string BuildPreflightSummary(PreflightResult result)
    {
        if (result.TotalCount == 0)
        {
            return T("Vm.Preflight.SummaryNone");
        }

        var warnings = result.Checks.Count(check => !check.Passed && check.Severity == PreflightSeverity.Warning);
        var errors = result.Checks.Count(check => !check.Passed && check.Severity == PreflightSeverity.Error);

        if (errors > 0)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                T("Vm.Preflight.SummaryErrors"),
                result.PassedCount,
                result.TotalCount,
                errors,
                warnings);
        }

        if (warnings > 0)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                T("Vm.Preflight.SummaryWarnings"),
                result.PassedCount,
                result.TotalCount,
                warnings);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            T("Vm.Preflight.SummaryPassed"),
            result.PassedCount,
            result.TotalCount);
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
                    fixes.Add(T("Vm.Fix.ToolPathAutoLocated"));
                }
                else
                {
                    var installResult = await _toolInstallerService.InstallOrLocateAsync(new InlineProgress<string>(AppendLog));
                    if (installResult.Success && !string.IsNullOrWhiteSpace(installResult.ToolPath))
                    {
                        IntuneWinAppUtilPath = installResult.ToolPath;
                        fixes.Add(T("Vm.Fix.ToolInstalledConfigured"));
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
                    fixes.Add(TF("Vm.Fix.InstallerDetected", Path.GetFileName(candidateInstaller)));
                }
            }

            if (IsSetupFileValid)
            {
                var installerFolder = Path.GetDirectoryName(SetupFilePath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(installerFolder) &&
                    (!IsSourceFolderValid || !IsPathInsideFolder(SetupFilePath, SourceFolder)))
                {
                    SourceFolder = installerFolder;
                    fixes.Add(T("Vm.Fix.SourceAligned"));
                }

                if (!IsOutputFolderValid)
                {
                    OutputFolder = BuildDefaultOutputFolder(SourceFolder);
                    fixes.Add(T("Vm.Fix.DefaultOutputCreated"));
                }

                InstallerType = _installerCommandService.DetectInstallerType(SetupFilePath);
                var installNeedsRefresh = string.IsNullOrWhiteSpace(InstallCommand) || ContainsCatalogTemplatePlaceholder(InstallCommand);
                var uninstallNeedsRefresh = string.IsNullOrWhiteSpace(UninstallCommand) || ContainsCatalogTemplatePlaceholder(UninstallCommand);

                if (installNeedsRefresh ||
                    uninstallNeedsRefresh ||
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
                        overwriteCommands: installNeedsRefresh || uninstallNeedsRefresh,
                        overwriteRules: DetectionRuleType == IntuneDetectionRuleType.None);

                    fixes.Add(T("Vm.Fix.RulesCompletedFromTemplate"));
                }
            }

            UpdateValidation();
            NotifyReadinessChanged();
            await RunPreflightCoreAsync();

            if (fixes.Count == 0)
            {
                SetStatus(
                    HasValidationErrors ? OperationState.Error : OperationState.Idle,
                    T("Vm.Status.NoAutoFixTitle"),
                    NextStepHint);
                return;
            }

            SetStatus(
                HasValidationErrors ? OperationState.Idle : OperationState.Success,
                T("Vm.Status.QuickFixAppliedTitle"),
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

    private bool CanDeleteProfile()
    {
        var name = !string.IsNullOrWhiteSpace(SelectedProfileName)
            ? SelectedProfileName
            : ProfileName;

        return !IsBusy && !string.IsNullOrWhiteSpace(name);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        var previousState = OperationState;
        var previousTitle = StatusTitle;
        var previousMessage = StatusMessage;

        _isCheckingForUpdates = true;
        IsBusy = true;
        UpdateStatus = T("Vm.Update.Checking");
        AppendLog("Checking for app updates...");

        try
        {
            var updateInfo = await _appUpdateService.CheckForUpdatesAsync(
                CurrentVersion,
                ResolveCurrentBuildTimestampUtc());
            ApplyUpdateInfo(updateInfo);

            if (updateInfo.IsUpdateAvailable)
            {
                SetStatus(
                    OperationState.Success,
                    T("Vm.Status.UpdateAvailableTitle"),
                    TF("Vm.Status.UpdateAvailableMessage", updateInfo.LatestVersion));
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
                        ? T("Vm.Update.NoPublicFeed")
                        : updateInfo.Message);
                }
            }

            await PersistSettingsSafeAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus = TF("Vm.Update.CheckFailedFormat", ex.Message);
            IsUpdateAvailable = false;
            SetStatus(previousState, previousTitle, previousMessage);
            AppendLog($"Update check failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _isCheckingForUpdates = false;
            _lastUpdateCheckCompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;

        try
        {
            var updateInfo = await _appUpdateService.CheckForUpdatesAsync(
                CurrentVersion,
                ResolveCurrentBuildTimestampUtc());
            ApplyUpdateInfo(updateInfo);

            if (updateInfo.IsUpdateAvailable)
            {
                SetStatus(
                    OperationState.Success,
                    T("Vm.Status.UpdateAvailableTitle"),
                    TF("Vm.Status.UpdateAvailableMessage", updateInfo.LatestVersion));
                AppendLog($"Update available at startup: {updateInfo.LatestVersion}.");
            }

            await PersistSettingsSafeAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Startup update check skipped: {ex.Message}");
        }
        finally
        {
            _isCheckingForUpdates = false;
            _lastUpdateCheckCompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        if (_isCheckingForUpdates || IsBusy)
        {
            return;
        }

        if (_lastUpdateCheckCompletedAtUtc.HasValue &&
            DateTimeOffset.UtcNow - _lastUpdateCheckCompletedAtUtc.Value < MinimumUpdateRecheckInterval)
        {
            return;
        }

        _isCheckingForUpdates = true;
        var hadUpdateAvailable = IsUpdateAvailable;

        try
        {
            var updateInfo = await _appUpdateService.CheckForUpdatesAsync(
                CurrentVersion,
                ResolveCurrentBuildTimestampUtc());
            ApplyUpdateInfo(updateInfo);

            if (updateInfo.IsUpdateAvailable && !hadUpdateAvailable)
            {
                SetStatus(
                    OperationState.Success,
                    T("Vm.Status.UpdateAvailableTitle"),
                    TF("Vm.Status.UpdateAvailableMessage", updateInfo.LatestVersion));
                AppendLog($"Background update check found new version: {updateInfo.LatestVersion}.");
            }

            await PersistSettingsSafeAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Background update check skipped: {ex.Message}");
        }
        finally
        {
            _isCheckingForUpdates = false;
            _lastUpdateCheckCompletedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private void ApplyUpdateInfo(AppUpdateInfo updateInfo)
    {
        _latestUpdateInfo = updateInfo;
        LatestVersion = string.IsNullOrWhiteSpace(updateInfo.LatestVersion)
            ? CurrentVersion
            : updateInfo.LatestVersion;
        UpdateChangelog = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes)
            ? T("Vm.Update.NoChangelog")
            : updateInfo.ReleaseNotes.Trim();
        UpdateStatus = string.IsNullOrWhiteSpace(updateInfo.Message)
            ? T("Vm.Update.NotChecked")
            : updateInfo.Message;
        IsUpdateAvailable = updateInfo.IsUpdateAvailable;
    }

    private async Task InstallUpdateAsync()
    {
        if (!CanInstallUpdate())
        {
            return;
        }

        if (_latestUpdateInfo is null || !_latestUpdateInfo.IsUpdateAvailable)
        {
            SetStatus(
                OperationState.Error,
                T("Vm.Status.NoUpdateSelectedTitle"),
                T("Vm.Status.NoUpdateSelectedMessage"));
            return;
        }

        IsBusy = true;
        SetStatus(
            OperationState.Running,
            T("Vm.Status.InstallingUpdateTitle"),
            EnableSilentAppUpdates
                ? T("Vm.Status.InstallingUpdateSilentMessage")
                : T("Vm.Status.InstallingUpdateInteractiveMessage"));
        AppendLog($"Starting update install to version {_latestUpdateInfo.LatestVersion}...");

        try
        {
            var installResult = await _appUpdateService.DownloadAndLaunchInstallerAsync(
                _latestUpdateInfo,
                new InlineProgress<string>(AppendLog),
                silentInstall: EnableSilentAppUpdates);

            if (!installResult.Success)
            {
                SetStatus(OperationState.Error, T("Vm.Status.UpdateInstallFailedTitle"), installResult.Message);
                UpdateStatus = installResult.Message;
                return;
            }

            SetStatus(
                OperationState.Success,
                T("Vm.Status.UpdateInstallerStartedTitle"),
                EnableSilentAppUpdates
                    ? T("Vm.Status.UpdateInstallerStartedSilentMessage")
                    : T("Vm.Status.UpdateInstallerStartedInteractiveMessage"));
            UpdateStatus = EnableSilentAppUpdates
                ? T("Vm.Update.SilentInstallerStarted")
                : T("Vm.Update.InstallerStarted");
            AppendLog("Installer scheduled. Closing app to allow update installation...");

            await Task.Delay(700);
            WpfApplication.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, T("Vm.Status.UpdateInstallFailedTitle"), ex.Message);
            UpdateStatus = TF("Vm.Update.InstallFailedFormat", ex.Message);
            AppendLog($"Update install failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearchCatalog()
    {
        var hasQuery = !string.IsNullOrWhiteSpace(PackageCatalogSearchTerm) && PackageCatalogSearchTerm.Trim().Length >= 2;
        var hasSource = IncludeWingetCatalogSource || IncludeChocolateyCatalogSource;
        return !IsBusy && !IsPackageCatalogBusy && hasQuery && hasSource;
    }

    private bool CanUseCatalogEntrySelection()
    {
        return !IsBusy &&
               !IsCatalogDownloadBusy &&
               !IsPackageCatalogBusy &&
               !IsPackageCatalogDetailBusy &&
               (CatalogEntryDetails is not null || SelectedCatalogEntry is not null);
    }

    private bool CanDownloadCatalogEntrySelection()
    {
        return !IsBusy &&
               !IsCatalogDownloadBusy &&
               !IsPackageCatalogBusy &&
               !IsPackageCatalogDetailBusy &&
               (CatalogEntryDetails is not null || SelectedCatalogEntry is not null);
    }

    private bool CanOpenCatalogHomepageLink()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace((CatalogEntryDetails ?? SelectedCatalogEntry)?.HomepageUrl);
    }

    private async Task SearchCatalogAsync()
    {
        if (!CanSearchCatalog())
        {
            if (!IncludeWingetCatalogSource && !IncludeChocolateyCatalogSource)
            {
                PackageCatalogStatus = T("Vm.Store.SelectSource");
            }
            else if (string.IsNullOrWhiteSpace(PackageCatalogSearchTerm) || PackageCatalogSearchTerm.Trim().Length < 2)
            {
                PackageCatalogStatus = T("Vm.Store.QueryTooShort");
            }

            return;
        }

        _catalogSearchCancellation?.Cancel();
        _catalogSearchCancellation = new CancellationTokenSource();
        var cancellationToken = _catalogSearchCancellation.Token;

        IsPackageCatalogBusy = true;
        PackageCatalogStatus = T("Vm.Store.Searching");
        CatalogEntryDetails = null;
        SelectedCatalogEntry = null;
        _activeCatalogSelectionContext = null;
        PackageCatalogResults.Clear();
        OnPropertyChanged(nameof(HasCatalogResults));

        try
        {
            await ReloadCatalogProfilesAsync(cancellationToken);

            var results = await _packageCatalogService.SearchAsync(new PackageCatalogQuery
            {
                SearchTerm = PackageCatalogSearchTerm.Trim(),
                MaxResults = 24,
                IncludeWinget = IncludeWingetCatalogSource,
                IncludeChocolatey = IncludeChocolateyCatalogSource
            }, cancellationToken);

            foreach (var item in results)
            {
                PackageCatalogResults.Add(DecorateCatalogEntry(item));
            }

            if (PackageCatalogResults.Count == 0)
            {
                PackageCatalogStatus = TF("Vm.Store.NoResults", PackageCatalogSearchTerm.Trim());
                return;
            }

            PackageCatalogStatus = TF("Vm.Store.Results", PackageCatalogResults.Count, PackageCatalogSearchTerm.Trim());
            SelectedCatalogEntry = PackageCatalogResults[0];
            _ = PreloadCatalogIconsAsync(PackageCatalogResults.ToList(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            PackageCatalogStatus = T("Vm.Store.Canceled");
        }
        catch (Exception ex)
        {
            PackageCatalogStatus = TF("Vm.Store.Error", ex.Message);
        }
        finally
        {
            IsPackageCatalogBusy = false;
            SearchCatalogCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadCatalogDetailsAsync(PackageCatalogEntry? entry)
    {
        CatalogEntryDetails = null;
        if (entry is null)
        {
            return;
        }

        _catalogDetailsCancellation?.Cancel();
        _catalogDetailsCancellation = new CancellationTokenSource();
        var cancellationToken = _catalogDetailsCancellation.Token;

        IsPackageCatalogDetailBusy = true;
        PackageCatalogStatus = TF("Vm.Store.LoadingDetails", entry.Name);

        try
        {
            var detailed = await _packageCatalogService.GetDetailsAsync(entry, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            CatalogEntryDetails = DecorateCatalogEntry(detailed ?? entry);
            PackageCatalogStatus = TF("Vm.Store.DetailReady", CatalogEntryDetails.Name);
        }
        catch (OperationCanceledException)
        {
            // A newer item selection replaced this details load.
        }
        catch (Exception ex)
        {
            CatalogEntryDetails = DecorateCatalogEntry(entry);
            PackageCatalogStatus = TF("Vm.Store.DetailsError", ex.Message);
        }
        finally
        {
            IsPackageCatalogDetailBusy = false;
        }
    }

    private async Task DownloadCatalogEntryAsync()
    {
        if (!CanDownloadCatalogEntrySelection())
        {
            return;
        }

        var entry = CatalogEntryDetails ?? SelectedCatalogEntry;
        if (entry is null)
        {
            return;
        }

        IsBusy = true;
        IsCatalogDownloadBusy = true;

        SetStatus(
            OperationState.Running,
            T("Vm.Status.CatalogDownloadStartedTitle"),
            TF("Vm.Status.CatalogDownloadStartedMessage", entry.Name));
        PackageCatalogStatus = TF("Vm.Store.Downloading", entry.Name);
        AppendLog($"Store download started for {entry.Name} ({entry.PackageId}).");

        try
        {
            var downloadResult = await _packageCatalogService.DownloadInstallerAsync(
                entry,
                new InlineProgress<string>(AppendLog));

            if (!downloadResult.Success || string.IsNullOrWhiteSpace(downloadResult.InstallerPath) || !File.Exists(downloadResult.InstallerPath))
            {
                var errorMessage = string.IsNullOrWhiteSpace(downloadResult.Message)
                    ? T("Vm.Store.DownloadFailedGeneric")
                    : downloadResult.Message;

                PackageCatalogStatus = TF("Vm.Store.DownloadFailed", entry.Name);
                SetStatus(
                    OperationState.Error,
                    T("Vm.Status.CatalogDownloadFailedTitle"),
                    errorMessage);
                AppendLog($"Store download failed for {entry.Name}: {errorMessage}");
                return;
            }

            await SelectSetupFileAsync(downloadResult.InstallerPath);
            if (!string.IsNullOrWhiteSpace(downloadResult.WorkingFolderPath) &&
                Directory.Exists(downloadResult.WorkingFolderPath))
            {
                SourceFolder = downloadResult.WorkingFolderPath;
            }

            if (string.IsNullOrWhiteSpace(ProfileName))
            {
                ProfileName = $"{entry.PackageId}-profile";
            }

            ApplyCatalogAutomaticDefaults(entry);
            await SaveCatalogProfileFromCurrentStateAsync(
                entry,
                downloadResult,
                CatalogProfileConfidence.Likely);
            await ReloadCatalogProfilesAsync();
            RefreshCatalogEntriesFromProfiles();

            var refreshedEntry = DecorateCatalogEntry(entry);
            SelectedCatalogEntry = refreshedEntry;
            CatalogEntryDetails = refreshedEntry;
            _activeCatalogSelectionContext = new CatalogSelectionContext(
                refreshedEntry.Source,
                refreshedEntry.PackageId,
                refreshedEntry.Version,
                downloadResult.InstallerSha256,
                downloadResult.InstallerPath);

            SelectedMainTabIndex = 0;
            PackageCatalogStatus = TF("Vm.Store.DownloadReady", entry.Name);

            var readyMessage = TF("Vm.Status.CatalogDownloadReadyMessage", Path.GetFileName(downloadResult.InstallerPath));
            SetStatus(
                OperationState.Success,
                T("Vm.Status.CatalogDownloadReadyTitle"),
                readyMessage);
            AppendLog($"Store download complete: {downloadResult.InstallerPath}");
            AppendLog("Packaging fields were auto-filled from the downloaded installer.");
        }
        catch (OperationCanceledException)
        {
            PackageCatalogStatus = T("Vm.Store.Canceled");
        }
        catch (Exception ex)
        {
            PackageCatalogStatus = TF("Vm.Store.DownloadError", ex.Message);
            SetStatus(
                OperationState.Error,
                T("Vm.Status.CatalogDownloadFailedTitle"),
                ex.Message);
            AppendLog($"Store download error for {entry.Name}: {ex.Message}");
        }
        finally
        {
            IsCatalogDownloadBusy = false;
            IsBusy = false;
        }
    }

    private async Task UseCatalogEntryAsync()
    {
        var entry = CatalogEntryDetails ?? SelectedCatalogEntry;
        if (entry is null)
        {
            return;
        }

        if (await TryApplyPreparedCatalogProfileAsync(entry))
        {
            SelectedMainTabIndex = 0;
            PackageCatalogStatus = TF("Vm.Store.DownloadReady", entry.Name);
            SetStatus(
                OperationState.Success,
                T("Vm.Status.CatalogDownloadReadyTitle"),
                TF("Vm.Store.PreparedFromProfile", entry.Name));
            AppendLog($"Store profile reused for {entry.Name} ({entry.PackageId}).");
            return;
        }

        await DownloadCatalogEntryAsync();
    }

    private static bool ContainsCatalogTemplatePlaceholder(string command)
    {
        return command.Contains('<') && command.Contains('>');
    }

    private void ApplyCatalogAutomaticDefaults(PackageCatalogEntry entry)
    {
        var autoFixes = new List<string>();

        if (InstallerType == InstallerType.Exe)
        {
            var installHasPlaceholder = ContainsCatalogTemplatePlaceholder(InstallCommand);
            var uninstallHasPlaceholder = ContainsCatalogTemplatePlaceholder(UninstallCommand);

            if (installHasPlaceholder || uninstallHasPlaceholder)
            {
                var fallbackPreset = _installerCommandService
                    .GetExeSilentPresets()
                    .FirstOrDefault(preset =>
                        !ContainsCatalogTemplatePlaceholder(preset.InstallArguments) &&
                        !ContainsCatalogTemplatePlaceholder(preset.UninstallArguments));

                if (fallbackPreset is not null &&
                    !string.IsNullOrWhiteSpace(SetupFilePath) &&
                    File.Exists(SetupFilePath))
                {
                    var fallbackSuggestion = _installerCommandService.CreateSuggestion(
                        SetupFilePath,
                        InstallerType.Exe,
                        preset: fallbackPreset);

                    if (installHasPlaceholder && !ContainsCatalogTemplatePlaceholder(fallbackSuggestion.InstallCommand))
                    {
                        InstallCommand = fallbackSuggestion.InstallCommand;
                    }

                    if (uninstallHasPlaceholder && !ContainsCatalogTemplatePlaceholder(fallbackSuggestion.UninstallCommand))
                    {
                        UninstallCommand = fallbackSuggestion.UninstallCommand;
                    }

                    if (DetectionRuleType == IntuneDetectionRuleType.None &&
                        fallbackSuggestion.SuggestedRules.DetectionRule.RuleType != IntuneDetectionRuleType.None)
                    {
                        ApplyDetectionRule(fallbackSuggestion.SuggestedRules.DetectionRule);
                    }

                    autoFixes.Add("Catalog automation replaced EXE command placeholders.");
                }
            }

            if (ContainsCatalogTemplatePlaceholder(UninstallCommand))
            {
                UninstallCommand = BuildCatalogAdaptiveUninstallCommand(entry);
                autoFixes.Add("Catalog automation generated uninstall command from registry uninstall entries.");
            }

            if (RequireSilentSwitchReview &&
                !SilentSwitchesVerified &&
                !ContainsCatalogTemplatePlaceholder(InstallCommand) &&
                !ContainsCatalogTemplatePlaceholder(UninstallCommand))
            {
                SilentSwitchesVerified = true;
                autoFixes.Add("Catalog automation marked EXE silent switches as verified.");
            }
        }

        if (DetectionRuleType == IntuneDetectionRuleType.None)
        {
            autoFixes.Add(
                "Catalog automation did not create a heuristic detection rule. " +
                "Configure a deterministic MSI/Registry/File detection rule before packaging.");
        }

        UpdateValidation();
        NotifyReadinessChanged();

        foreach (var autoFix in autoFixes)
        {
            AppendLog(autoFix);
        }
    }

    private static string BuildCatalogAdaptiveUninstallCommand(PackageCatalogEntry entry)
    {
        var patterns = BuildCatalogMatchPatterns(entry);
        var patternValues = string.Join(", ", patterns.Select(pattern => $"'{EscapePowerShellSingleQuoted(pattern)}'"));

        var script = string.Join(
            "; ",
            $"$patterns=@({patternValues})",
            "$roots=@('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*')",
            "$app=Get-ItemProperty -Path $roots -ErrorAction SilentlyContinue | Where-Object { $displayName=$_.DisplayName; if([string]::IsNullOrWhiteSpace($displayName)){ $false } else { ($patterns | Where-Object { $displayName -like ('*'+$_+'*') } | Measure-Object).Count -gt 0 } } | Select-Object -First 1",
            "if($null -eq $app){ exit 0 }",
            "$command = if(-not [string]::IsNullOrWhiteSpace($app.QuietUninstallString)){ $app.QuietUninstallString } else { $app.UninstallString }",
            "if([string]::IsNullOrWhiteSpace($command)){ exit 1 }",
            "Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $command -WindowStyle Hidden -Wait",
            "exit $LASTEXITCODE");

        return $"powershell.exe -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -Command \"{script}\"";
    }

    private static List<string> BuildCatalogMatchPatterns(PackageCatalogEntry entry)
    {
        var patterns = new List<string>();

        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            patterns.Add(entry.Name.Trim());
        }

        if (!string.IsNullOrWhiteSpace(entry.PackageId))
        {
            patterns.Add(entry.PackageId.Trim());

            var shortId = entry.PackageId
                .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            if (!string.IsNullOrWhiteSpace(shortId))
            {
                patterns.Add(shortId);
            }
        }

        if (patterns.Count == 0)
        {
            patterns.Add("Package");
        }

        return patterns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private async Task ReloadCatalogProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _packageProfileStoreService.GetProfilesAsync(cancellationToken);
        _catalogProfiles = profiles.ToList();
    }

    private PackageCatalogEntry DecorateCatalogEntry(PackageCatalogEntry entry)
    {
        if (entry is null)
        {
            return new PackageCatalogEntry();
        }

        var packageProfiles = _catalogProfiles
            .Where(profile =>
                profile.Source == entry.Source &&
                profile.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var exactProfile = packageProfiles
            .Where(profile => IsCatalogVersionMatch(profile.Version, entry.Version))
            .OrderByDescending(profile => profile.LastVerifiedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(profile => profile.LastPreparedAtUtc)
            .FirstOrDefault();

        var latestPreparedProfile = packageProfiles
            .OrderByDescending(profile => profile.LastPreparedAtUtc)
            .ThenByDescending(profile => profile.LastVerifiedAtUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        var confidence = exactProfile?.Confidence ?? CatalogProfileConfidence.ManualReview;
        var confidenceText = confidence switch
        {
            CatalogProfileConfidence.Verified => T("Ui.Store.Badge.Verified"),
            CatalogProfileConfidence.Likely => T("Ui.Store.Badge.Likely"),
            _ => T("Ui.Store.Badge.Manual")
        };

        var effectiveProfile = exactProfile ?? latestPreparedProfile;
        var isUpgradeAvailable = latestPreparedProfile is not null &&
                                 !IsCatalogVersionMatch(entry.Version, latestPreparedProfile.Version) &&
                                 CompareVersionsForNotification(entry.Version, latestPreparedProfile.Version) > 0;

        return entry with
        {
            ProfileConfidence = confidence,
            ConfidenceBadgeText = confidenceText,
            IsUpgradeAvailable = isUpgradeAvailable,
            UpgradeFromVersion = isUpgradeAvailable ? latestPreparedProfile?.Version ?? string.Empty : string.Empty,
            HashVerifiedBySource = exactProfile?.HashVerifiedBySource ?? entry.HashVerifiedBySource,
            VendorSigned = exactProfile?.VendorSigned ?? entry.VendorSigned,
            SilentSwitchProbeDetected = exactProfile?.SilentSwitchProbeDetected ?? entry.SilentSwitchProbeDetected,
            DetectionReady = exactProfile?.DetectionReady ?? entry.DetectionReady,
            LocalInstallerPath = exactProfile?.InstallerPath ?? entry.LocalInstallerPath,
            InstallerSha256 = exactProfile?.InstallerSha256 ?? entry.InstallerSha256,
            CachedIconPath = CoalescePath(exactProfile?.IconPath, entry.CachedIconPath),
            LastPreparedAtUtc = effectiveProfile?.LastPreparedAtUtc ?? entry.LastPreparedAtUtc,
            LastVerifiedAtUtc = effectiveProfile?.LastVerifiedAtUtc ?? entry.LastVerifiedAtUtc
        };
    }

    private void RefreshCatalogEntriesFromProfiles()
    {
        for (var index = 0; index < PackageCatalogResults.Count; index++)
        {
            PackageCatalogResults[index] = DecorateCatalogEntry(PackageCatalogResults[index]);
        }

        if (SelectedCatalogEntry is not null)
        {
            SelectedCatalogEntry = DecorateCatalogEntry(SelectedCatalogEntry);
        }

        if (CatalogEntryDetails is not null)
        {
            CatalogEntryDetails = DecorateCatalogEntry(CatalogEntryDetails);
        }
    }

    private async Task PreloadCatalogIconsAsync(IReadOnlyList<PackageCatalogEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries.Take(16))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(entry.CachedIconPath) && File.Exists(entry.CachedIconPath))
            {
                continue;
            }

            var iconPath = await _packageCatalogService.ResolveCachedIconPathAsync(entry, entry.LocalInstallerPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                continue;
            }

            UpdateCatalogEntryIcon(entry, iconPath);
        }
    }

    private void UpdateCatalogEntryIcon(PackageCatalogEntry entry, string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return;
        }

        for (var index = 0; index < PackageCatalogResults.Count; index++)
        {
            var current = PackageCatalogResults[index];
            if (current.Source != entry.Source ||
                !current.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase) ||
                !IsCatalogVersionMatch(current.Version, entry.Version))
            {
                continue;
            }

            PackageCatalogResults[index] = current with { CachedIconPath = iconPath };
        }

        if (SelectedCatalogEntry is not null &&
            SelectedCatalogEntry.Source == entry.Source &&
            SelectedCatalogEntry.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase) &&
            IsCatalogVersionMatch(SelectedCatalogEntry.Version, entry.Version))
        {
            SelectedCatalogEntry = SelectedCatalogEntry with { CachedIconPath = iconPath };
        }

        if (CatalogEntryDetails is not null &&
            CatalogEntryDetails.Source == entry.Source &&
            CatalogEntryDetails.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase) &&
            IsCatalogVersionMatch(CatalogEntryDetails.Version, entry.Version))
        {
            CatalogEntryDetails = CatalogEntryDetails with { CachedIconPath = iconPath };
        }
    }

    private async Task SaveCatalogProfileFromCurrentStateAsync(
        PackageCatalogEntry entry,
        PackageCatalogDownloadResult downloadResult,
        CatalogProfileConfidence confidence)
    {
        if (string.IsNullOrWhiteSpace(entry.PackageId))
        {
            return;
        }

        var installerPath = !string.IsNullOrWhiteSpace(downloadResult.InstallerPath)
            ? downloadResult.InstallerPath
            : SetupFilePath;
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var detectionReady = DetectionRuleType != IntuneDetectionRuleType.None;
        var installHasPlaceholder = ContainsCatalogTemplatePlaceholder(InstallCommand);
        var uninstallHasPlaceholder = ContainsCatalogTemplatePlaceholder(UninstallCommand);
        if (installHasPlaceholder || uninstallHasPlaceholder || !detectionReady)
        {
            confidence = CatalogProfileConfidence.ManualReview;
        }

        var iconPath = await _packageCatalogService.ResolveCachedIconPathAsync(entry, installerPath);
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            UpdateCatalogEntryIcon(entry, iconPath);
        }

        var resolvedSha = string.IsNullOrWhiteSpace(downloadResult.InstallerSha256)
            ? ComputeFileSha256(installerPath)
            : downloadResult.InstallerSha256;

        var profile = new CatalogPackageProfile
        {
            Source = entry.Source,
            PackageId = entry.PackageId,
            Name = entry.Name,
            Version = string.IsNullOrWhiteSpace(entry.Version) ? entry.BuildVersion : entry.Version,
            BuildVersion = entry.BuildVersion,
            InstallerPath = installerPath,
            InstallerSha256 = resolvedSha,
            InstallerType = InstallerType,
            InstallCommand = InstallCommand,
            UninstallCommand = UninstallCommand,
            DetectionRuleType = DetectionRuleType,
            IntuneRules = BuildIntuneRules(),
            SilentSwitchesVerified = SilentSwitchesVerified,
            HashVerifiedBySource = downloadResult.HashVerifiedBySource,
            VendorSigned = downloadResult.VendorSigned,
            SilentSwitchProbeDetected = InstallerParameterProbeDetected || SuggestionUsedKnowledgeCache,
            DetectionReady = detectionReady,
            Confidence = confidence,
            IconPath = iconPath,
            LastPreparedAtUtc = now,
            LastVerifiedAtUtc = confidence == CatalogProfileConfidence.Verified ? now : null
        };

        await _packageProfileStoreService.SaveProfileAsync(profile);
    }

    private async Task<bool> TryApplyPreparedCatalogProfileAsync(PackageCatalogEntry entry)
    {
        await ReloadCatalogProfilesAsync();
        var profile = _catalogProfiles
            .Where(candidate =>
                candidate.Source == entry.Source &&
                candidate.PackageId.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase) &&
                IsCatalogVersionMatch(candidate.Version, entry.Version))
            .OrderByDescending(candidate => candidate.LastVerifiedAtUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(candidate => candidate.LastPreparedAtUtc)
            .FirstOrDefault();

        if (profile is null || string.IsNullOrWhiteSpace(profile.InstallerPath) || !File.Exists(profile.InstallerPath))
        {
            return false;
        }

        await SelectSetupFileAsync(profile.InstallerPath);

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ProfileName = $"{entry.PackageId}-profile";
        }

        if (!string.IsNullOrWhiteSpace(profile.InstallCommand))
        {
            InstallCommand = profile.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(profile.UninstallCommand))
        {
            UninstallCommand = profile.UninstallCommand;
        }

        if (profile.InstallerType != InstallerType.Unknown)
        {
            InstallerType = profile.InstallerType;
        }

        if (profile.IntuneRules.DetectionRule.RuleType != IntuneDetectionRuleType.None)
        {
            ApplyIntuneRules(profile.IntuneRules);
        }

        SilentSwitchesVerified = profile.SilentSwitchesVerified;
        InstallerParameterProbeDetected = profile.SilentSwitchProbeDetected;
        SuggestionUsedKnowledgeCache = profile.Confidence == CatalogProfileConfidence.Verified;

        _activeCatalogSelectionContext = new CatalogSelectionContext(
            profile.Source,
            profile.PackageId,
            profile.Version,
            profile.InstallerSha256,
            profile.InstallerPath);

        return true;
    }

    private async Task PromoteActiveCatalogProfileAsVerifiedAsync()
    {
        if (_activeCatalogSelectionContext is null ||
            string.IsNullOrWhiteSpace(_activeCatalogSelectionContext.InstallerPath) ||
            !string.Equals(_activeCatalogSelectionContext.InstallerPath, SetupFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _packageProfileStoreService.PromoteProfileAsync(
            _activeCatalogSelectionContext.Source,
            _activeCatalogSelectionContext.PackageId,
            _activeCatalogSelectionContext.Version,
            _activeCatalogSelectionContext.InstallerSha256);

        await ReloadCatalogProfilesAsync();
        RefreshCatalogEntriesFromProfiles();
    }

    private static bool IsCatalogVersionMatch(string left, string right)
    {
        return NormalizeVersionForNotification(left)
            .Equals(NormalizeVersionForNotification(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string CoalescePath(string? left, string right)
    {
        if (!string.IsNullOrWhiteSpace(left) && File.Exists(left))
        {
            return left;
        }

        return right;
    }

    private static string ComputeFileSha256(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void OpenCatalogHomepage()
    {
        var url = (CatalogEntryDetails ?? SelectedCatalogEntry)?.HomepageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus(
                OperationState.Error,
                T("Vm.Status.CatalogLinkOpenFailedTitle"),
                ex.Message);
        }
    }

    private bool CanPackage()
    {
        return !IsBusy;
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

        if (!IsConfigurationValid)
        {
            UpdateValidation();
            var blockingReason = ValidationErrors.FirstOrDefault() ?? NextStepHint;
            SetStatus(OperationState.Error, T("Vm.Status.PackagingFailedTitle"), blockingReason);
            AppendLog($"Packaging blocked: {blockingReason}");
            return;
        }

        IsBusy = true;
        HasPackagingRun = true;
        ResultOutputPath = string.Empty;
        ResultMetadataPath = string.Empty;
        ResultChecklistPath = string.Empty;
        IntunePortalChecklist = string.Empty;
        ClearLogs();
        SetPackagingProgress(T("Vm.Progress.PreparingStep"), T("Vm.Progress.PreparingDetail"), 0);

        SetStatus(
            OperationState.Running,
            T("Vm.Status.PackagingInProgressTitle"),
            T("Vm.Status.PackagingInProgressMessage"));
        AppendLog("Packaging started.");

        try
        {
            await PersistSettingsAsync();
            SetPackagingProgress(T("Vm.Progress.PreflightStep"), T("Vm.Progress.PreflightDetail"), 8);

            PreflightResult preflight;
            if (CanReusePreflightForPackaging())
            {
                var lastPreflight = _lastPreflightCompletedAtUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";
                AppendLog($"Reusing preflight results from {lastPreflight} to speed up packaging.");
                preflight = new PreflightResult
                {
                    Checks = PreflightChecks.ToList()
                };
            }
            else
            {
                preflight = await RunPreflightCoreAsync();
            }

            if (preflight.HasErrors)
            {
                SetStatus(
                    OperationState.Error,
                    T("Vm.Status.PreflightFailedTitle"),
                    T("Vm.Status.PreflightResolveBeforePackaging"));
                SetPackagingProgress(T("Vm.Progress.PreflightFailedStep"), T("Vm.Progress.PreflightFailedDetail"), 100);
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
                TrySaveVerifiedInstallerKnowledge();
                await PromoteActiveCatalogProfileAsVerifiedAsync();
                SetStatus(
                    OperationState.Success,
                    T("Vm.Status.PackageCreatedTitle"),
                    TF("Vm.Status.PackageCreatedMessage", result.OutputPackagePath ?? string.Empty));
                SetPackagingProgress(T("Vm.Progress.CompletedStep"), T("Vm.Progress.CompletedDetail"), 100);
                AppendLog("Packaging completed successfully.");
            }
            else
            {
                SetStatus(OperationState.Error, T("Vm.Status.PackagingFailedTitle"), result.Message);
                SetPackagingProgress(T("Vm.Progress.FailedStep"), result.Message, 100);
                AppendLog($"Packaging failed: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(OperationState.Error, T("Vm.Status.UnexpectedErrorTitle"), ex.Message);
            SetPackagingProgress(T("Vm.Progress.UnexpectedErrorStep"), ex.Message, 100);
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
            SetStatus(
                OperationState.Error,
                T("Vm.Status.ProfileNameRequiredTitle"),
                T("Vm.Status.ProfileNameRequiredMessage"));
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

        SetStatus(
            OperationState.Idle,
            T("Vm.Status.ProfileSavedTitle"),
            TF("Vm.Status.ProfileSavedMessage", resolvedName));
    }

    private async Task LoadProfileAsync()
    {
        var nameToLoad = !string.IsNullOrWhiteSpace(SelectedProfileName)
            ? SelectedProfileName
            : ProfileName;

        if (string.IsNullOrWhiteSpace(nameToLoad))
        {
            SetStatus(
                OperationState.Error,
                T("Vm.Status.NoProfileSelectedTitle"),
                T("Vm.Status.NoProfileSelectedMessage"));
            return;
        }

        var profile = await _profileService.GetProfileAsync(nameToLoad);
        if (profile is null)
        {
            SetStatus(
                OperationState.Error,
                T("Vm.Status.ProfileMissingTitle"),
                TF("Vm.Status.ProfileMissingMessage", nameToLoad));
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
        SetStatus(
            OperationState.Idle,
            T("Vm.Status.ProfileLoadedTitle"),
            TF("Vm.Status.ProfileLoadedMessage", profile.Name));
    }

    private async Task DeleteProfileAsync()
    {
        var nameToDelete = !string.IsNullOrWhiteSpace(SelectedProfileName)
            ? SelectedProfileName
            : ProfileName;

        if (string.IsNullOrWhiteSpace(nameToDelete))
        {
            SetStatus(
                OperationState.Error,
                T("Vm.Status.NoProfileSelectedTitle"),
                T("Vm.Status.NoProfileSelectedMessage"));
            return;
        }

        var deleted = await _profileService.DeleteProfileAsync(nameToDelete);
        if (!deleted)
        {
            SetStatus(
                OperationState.Error,
                T("Vm.Status.ProfileMissingTitle"),
                TF("Vm.Status.ProfileMissingMessage", nameToDelete));
            return;
        }

        if (string.Equals(ProfileName, nameToDelete, StringComparison.OrdinalIgnoreCase))
        {
            ProfileName = string.Empty;
        }

        if (string.Equals(SelectedProfileName, nameToDelete, StringComparison.OrdinalIgnoreCase))
        {
            SelectedProfileName = null;
        }

        await RefreshProfileListAsync();

        SetStatus(
            OperationState.Idle,
            T("Vm.Status.ProfileDeletedTitle"),
            TF("Vm.Status.ProfileDeletedMessage", nameToDelete));
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
            SuggestionUsedKnowledgeCache = false;
            InstallerParameterProbeDetected = false;
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
            PreflightSummary = T("Vm.Preflight.DefaultSummary");
            PreflightChecks.Clear();
            PackagingProgressPercentage = 0;
            IsPackagingProgressIndeterminate = false;
            PackagingProgressStep = T("Vm.Progress.ReadyStep");
            PackagingProgressDetail = T("Vm.Progress.ReadyDetail");
            ProfileName = string.Empty;
            SelectedProfileName = null;
            OperationState = OperationState.Idle;
            StatusTitle = T("Vm.Status.ReadyTitle");
            StatusMessage = T("Vm.Status.ConfigurationResetMessage");
            ClearLogs();
            _activeCatalogSelectionContext = null;
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
            InstallerParameterProbeDetected = false;
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
            InstallerParameterProbeDetected = false;
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
                summaryParts.Add(TF("Vm.Msi.ProductFormat", metadata.ProductName));
            }

            if (!string.IsNullOrWhiteSpace(metadata.ProductVersion))
            {
                summaryParts.Add(TF("Vm.Msi.VersionFormat", metadata.ProductVersion));
            }

            if (!string.IsNullOrWhiteSpace(metadata.ProductCode))
            {
                summaryParts.Add(TF("Vm.Msi.CodeFormat", metadata.ProductCode));
            }

            if (!string.IsNullOrWhiteSpace(metadata.InspectionWarning))
            {
                summaryParts.Add(TF("Vm.Msi.WarningFormat", metadata.InspectionWarning));
            }

            MsiMetadataSummary = summaryParts.Count == 0
                ? T("Vm.Msi.MetadataAvailable")
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
            EnableSilentAppUpdates = EnableSilentAppUpdates,
            StoreShowAdvancedDetails = ShowStoreAdvancedDetails,
            LastKnownLatestVersion = LatestVersion,
            LastUpdateCheckUtc = _lastUpdateCheckCompletedAtUtc,
            UiLanguage = _localizationService.CurrentLanguageCode,
            UiTheme = _themeService.CurrentThemeCode,
            UiDensity = _densityService.CurrentDensityCode
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
        SuggestionUsedKnowledgeCache = suggestion.UsedKnowledgeCache;
        InstallerParameterProbeDetected = suggestion.ParameterProbeDetected;

        if (overwriteCommands)
        {
            InstallCommand = suggestion.InstallCommand;
            UninstallCommand = suggestion.UninstallCommand;
        }

        if (overwriteRules)
        {
            ApplyIntuneRules(suggestion.SuggestedRules);
        }

        RefreshSwitchVerificationStatus();
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
        foreach (var issue in validation.Issues)
        {
            ValidationErrors.Add(LocalizeWithFallback(issue.Key, issue.Message));
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

        _lastPreflightCompletedAtUtc = null;
        HasPreflightRun = false;
        PreflightSummary = T("Vm.Preflight.ConfigChanged");
        PreflightChecks.Clear();
    }

    private bool CanReusePreflightForPackaging()
    {
        if (!HasPreflightRun || !_lastPreflightCompletedAtUtc.HasValue || PreflightChecks.Count == 0)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _lastPreflightCompletedAtUtc.Value <= PreflightReuseWindow;
    }

    private void TrySaveVerifiedInstallerKnowledge()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SetupFilePath) || !File.Exists(SetupFilePath))
            {
                return;
            }

            var rules = BuildIntuneRules();
            _installerCommandService.SaveVerifiedKnowledge(
                SetupFilePath,
                InstallerType,
                InstallCommand,
                UninstallCommand,
                rules);
        }
        catch
        {
            // Non-blocking cache persistence.
        }
    }

    private void SetStatus(OperationState state, string title, string message)
    {
        OperationState = state;
        StatusTitle = title;
        StatusMessage = message;
    }

    private string T(string key)
    {
        return _localizationService.Translate(key);
    }

    private string LocalizeWithFallback(string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }

        var translated = T(key);
        return string.Equals(translated, key, StringComparison.Ordinal)
            ? fallback
            : translated;
    }

    private PreflightCheck LocalizePreflightCheck(PreflightCheck check)
    {
        return check with
        {
            Title = LocalizeWithFallback(check.TitleKey, check.Title),
            Message = LocalizeWithFallback(check.MessageKey, check.Message)
        };
    }

    private string TF(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, T(key), args);
    }

    private void HandleLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(InstallerTypeDisplay));
        OnPropertyChanged(nameof(DetectionSelectionHint));
        OnPropertyChanged(nameof(DetectionRuleTypeGuidance));
        OnPropertyChanged(nameof(DetectionFileOperatorGuidance));
        OnPropertyChanged(nameof(DetectionRegistryOperatorGuidance));
        OnPropertyChanged(nameof(DetectionRegistryValueNameLabel));
        OnPropertyChanged(nameof(ReadinessSummary));
        OnPropertyChanged(nameof(NextStepHint));
        OnPropertyChanged(nameof(CurrentVersionDisplay));
        OnPropertyChanged(nameof(LatestVersionDisplay));
        OnPropertyChanged(nameof(UpdateNotificationText));

        if (_latestUpdateInfo is null)
        {
            UpdateChangelog = T("Vm.Update.ChangelogDefault");
            UpdateStatus = T("Vm.Update.NotChecked");
        }

        if (!HasPreflightRun)
        {
            PreflightSummary = T("Vm.Preflight.DefaultSummary");
        }

        if (!HasPackagingRun)
        {
            PackagingProgressStep = T("Vm.Progress.ReadyStep");
            PackagingProgressDetail = T("Vm.Progress.ReadyDetail");
        }

        if (!IsPackageCatalogBusy &&
            PackageCatalogResults.Count == 0 &&
            string.IsNullOrWhiteSpace(PackageCatalogSearchTerm))
        {
            PackageCatalogStatus = T("Vm.Store.Ready");
        }
        else if (PackageCatalogResults.Count > 0)
        {
            RefreshCatalogEntriesFromProfiles();
        }

        RefreshSwitchVerificationStatus();
    }

    private void RefreshSwitchVerificationStatus()
    {
        if (InstallerType != InstallerType.Exe)
        {
            SwitchVerificationStatus = string.Empty;
            return;
        }

        if (SuggestionUsedKnowledgeCache && SilentSwitchesVerified)
        {
            SwitchVerificationStatus = T("Vm.SwitchStatus.CacheVerified");
            return;
        }

        if (SilentSwitchesVerified)
        {
            SwitchVerificationStatus = T("Vm.SwitchStatus.ManualVerified");
            return;
        }

        SwitchVerificationStatus = T("Vm.SwitchStatus.NotVerified");
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

    private static bool IsVersionGreaterForNotification(string candidateVersion, string currentVersion)
    {
        return CompareVersionsForNotification(candidateVersion, currentVersion) > 0;
    }

    private static int CompareVersionsForNotification(string leftVersion, string rightVersion)
    {
        var leftNormalized = NormalizeVersionForNotification(leftVersion);
        var rightNormalized = NormalizeVersionForNotification(rightVersion);

        if (Version.TryParse(leftNormalized, out var left) &&
            Version.TryParse(rightNormalized, out var right))
        {
            return left.CompareTo(right);
        }

        return string.Compare(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersionForNotification(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var normalized = value.Trim();

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex > 0)
        {
            normalized = normalized[..plusIndex];
        }

        var dashIndex = normalized.IndexOf('-');
        if (dashIndex > 0)
        {
            normalized = normalized[..dashIndex];
        }

        return normalized;
    }

    private sealed record CatalogSelectionContext(
        PackageCatalogSource Source,
        string PackageId,
        string Version,
        string InstallerSha256,
        string InstallerPath);

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

    private static DateTimeOffset? ResolveCurrentBuildTimestampUtc()
    {
        try
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            {
                return null;
            }

            return File.GetLastWriteTimeUtc(location);
        }
        catch
        {
            return null;
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







