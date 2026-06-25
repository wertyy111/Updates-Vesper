using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BlurredBackgroundEffect = BlurredBackground.WPF.BlurredBackground;
using Microsoft.Win32;
using VesperLauncher.Core;
using VesperLauncher.Launcher;
using VesperLauncher.Platform;
using Open.Nat;

namespace VesperLauncher;

public partial class MainWindow : Window
{
    private static readonly IPlatformService PlatformService = PlatformServiceFactory.CreateCurrent();

    private enum BackgroundSceneKind
    {
        Unknown,
        Morning,
        Day,
        Evening,
        Night
    }

    private enum GlassThemeTone
    {
        Dark,
        Light
    }

    private enum SidePanelSection
    {
        None,
        Account,
        Settings,
        Skin,
        Background,
        Mods,
        Friends
    }

    private enum SkinModelPreference
    {
        Auto,
        Classic,
        Slim
    }

    private enum AccountEntryMode
    {
        None,
        Login,
        Register,
        Guest
    }

    private const double DefaultOuterCornerRadius = 12d;
    private const double DefaultInnerCornerRadius = 11d;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int MaxSavedUsernames = 20;
    private const int MaxAccountRecentUsernames = 3;
    private const int MaxFriends = 80;
    private const int MinAccountPasswordLength = 6;
    private const int MaxCloudAvatarBytes = 240 * 1024;
    private const int PasswordHashIterations = 120_000;
    private const string VesperNetServiceName = "VesperNetService";
    private const string VesperNetHealthUrl = "http://127.0.0.1:37851/health";
    private const string VesperNetOverlayHostAttachUrl = "http://127.0.0.1:37851/overlay/host/attach";
    private const string VesperNetOverlayGuestConnectUrl = "http://127.0.0.1:37851/overlay/guest/connect";
    private const string VesperNetOverlayClearUrl = "http://127.0.0.1:37851/overlay/clear";
    private const string VesperRelayTransportMode = "cfws";
    private const string DefaultBackgroundPresetId = "default";
    private const string DefaultSkinModelPreferenceId = "auto";
    private const string FirstRunMarkerFileName = "first-run.marker";
    private const string AllModsCategoryFilter = "Все";
    private static readonly string[] RecommendedModsCategoryOrder =
    [
        AllModsCategoryFilter,
        "Моды",
        "Шейдеры",
        "Ресурспаки",
        "Сборки"
    ];
    private static readonly string[] RecommendedModBadgePalette =
    [
        "#3B355E",
        "#214863",
        "#27503A",
        "#5A3A57",
        "#5C4422",
        "#334E6F",
        "#60403A",
        "#2F4E58"
    ];
    private const double DefaultSettingsPanelWidth = 370d;
    private const double AccountSettingsPanelWidth = 560d;
    private const double LauncherSettingsPanelWidth = 600d;
    private const double BackgroundSettingsPanelWidth = 560d;
    private const double SkinSettingsPanelWidth = 560d;
    private const double ModsSettingsPanelWidth = 560d;
    private const double FriendsSettingsPanelWidth = 560d;
    private const int InitialRecommendedModIconPrepareCount = 12;
    private const string DefaultJavaExecutable = "javaw";
    private const string LauncherSettingsTabLauncher = "launcher";
    private const string LauncherSettingsTabJava = "java";
    private const string LauncherSettingsTabVesper = "vesper";
    private const string LauncherSettingsTabLaunch = "launch";
    private const string LauncherSettingsTabLanguage = "language";
    private const string LauncherSettingsTabGlass = "glass";
    private const string AutomaticMinecraftLanguageCode = "auto";
    private const string DefaultMinecraftLanguageCode = "ru_ru";
    private const string LoginFormPlacementLeftId = "left";
    private const string LoginFormPlacementCenterId = "center";
    private const string LoginFormPlacementRightId = "right";
    private const string DefaultLoginFormPlacementId = LoginFormPlacementCenterId;
    private const string LauncherDirectoryViewCurrentId = "current";
    private const string LauncherDirectoryViewSharedId = "shared";
    private const string DefaultLauncherDirectoryViewId = LauncherDirectoryViewCurrentId;
    private const int DefaultMemoryMb = 4096;
    private const int MinimumLauncherMemoryMb = 2048;
    private const int MaximumLauncherMemoryMb = 12288;
    private const double SkinPreviewBaseYaw = 0d;
    private const double SkinPreviewSwingAmplitude = 0d;
    private const double SkinPreviewSwingStep = 0d;
    private static readonly TimeSpan VisibleFriendsRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HiddenFriendsRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PresenceHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RecentLanPortRetentionInterval = TimeSpan.FromSeconds(45);
    private static readonly DateTimeOffset EarlyPlayersAchievementCutoffUtc = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan VersionListLoadTimeout = TimeSpan.FromSeconds(12);
    private static readonly Thickness RoundedWindowContentMargin = new(1);
    private static readonly Thickness MaximizedWindowContentMargin = new(0);
    private static readonly Regex UsernameRegex = new(@"^[A-Za-z0-9_\-\.]{3,32}$", RegexOptions.Compiled);
    private static readonly Regex MinecraftVersionRegex = new(@"\d+\.\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly IComparer<string> MinecraftBaseVersionComparer =
        Comparer<string>.Create(CompareMinecraftBaseVersionIds);
    private static readonly Regex BackgroundTimeRangeRegex = new(
        @"(?<!\d)(?<startHour>\d{1,2})(?::(?<startMinute>\d{1,2}))?\s*[-_]\s*(?<endHour>\d{1,2})(?::(?<endMinute>\d{1,2}))?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LanPortRegex = new(
        @"(?:Started serving on|Local game hosted on port|Открыт локальный сервер на порту)\s+(?<port>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MultiplayerConnectRegex = new(
        @"Connecting to\s+(?<host>[^,\s]+),\s*(?<port>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyList<SkinModelOption> SkinModelOptions =
    [
        new SkinModelOption(DefaultSkinModelPreferenceId, "Авто (как в файле)", SkinModelPreference.Auto),
        new SkinModelOption("classic", "Steve (широкие руки)", SkinModelPreference.Classic),
        new SkinModelOption("slim", "Alex (узкие руки)", SkinModelPreference.Slim)
    ];
    private static readonly IReadOnlyList<LauncherLanguageOption> MinecraftLanguageOptions =
    [
        new LauncherLanguageOption(AutomaticMinecraftLanguageCode, "Авто"),
        new LauncherLanguageOption("ru_ru", "Русский"),
        new LauncherLanguageOption("en_us", "English (US)")
    ];
    private static readonly IReadOnlyList<LoginFormPlacementOption> LoginFormPlacementOptions =
    [
        new LoginFormPlacementOption(LoginFormPlacementLeftId, "Слева"),
        new LoginFormPlacementOption(LoginFormPlacementCenterId, "Посередине"),
        new LoginFormPlacementOption(LoginFormPlacementRightId, "Справа")
    ];
    private static readonly IReadOnlyList<LauncherDirectoryViewOption> LauncherDirectoryViewOptions =
    [
        new LauncherDirectoryViewOption(LauncherDirectoryViewCurrentId, "Отдельные папки по версиям"),
        new LauncherDirectoryViewOption(LauncherDirectoryViewSharedId, "Не использовать отдельные папки")
    ];
    private static readonly IReadOnlyList<LauncherJavaRuntimeOption> LauncherJavaRuntimeOptions =
    [
        new LauncherJavaRuntimeOption(true, "Рекомендуемая (системная Java)"),
        new LauncherJavaRuntimeOption(false, "Своя Java / JRE")
    ];
    private readonly MinecraftLauncherService _launcherService = new();
    private readonly VersionStateMachine _versionStateMachine = new();
    private readonly Logger _launcherLogger = new("launcher");
    private readonly string _launcherLogPath;
    private readonly string _savedUsernamesPath;
    private readonly string _friendsPath;
    private readonly string _uiStatePath;
    private readonly string _accountStatePath;
    private readonly string _guestIdentityStatePath;
    private readonly string _accountSyncConfigPath;
    private readonly string _modFavoritesPath;
    private readonly string _installedCatalogModsPath;
    private readonly List<string> _savedUsernames = [];
    private readonly List<string> _friends = [];
    private readonly List<CloudFriendListItem> _friendEntries = [];
    private readonly List<CloudIncomingFriendRequestItem> _incomingFriendRequests = [];
    private readonly List<string> _outgoingFriendRequests = [];
    private readonly Dictionary<string, string> _installedCatalogModPaths = new(StringComparer.OrdinalIgnoreCase);
    private LauncherAccountState? _accountState;
    private GuestIdentityState? _guestIdentityState;
    private IReadOnlyList<SkinFileEntry> _availableSkinFiles = [];
    private string? _selectedSkinFilePath;
    private bool _selectedSkinIsSlim;
    private SkinModelPreference _skinModelPreference = SkinModelPreference.Auto;
    private bool _isRefreshingSkinFiles;
    private bool _isRefreshingSkinModelSelection;
    private double _skinPreviewSwingPhase;
    private SidePanelSection _activeSidePanelSection;
    private bool _isRefreshingSavedUsernames;
    private bool _isRefreshingFriendsList;
    private bool _isRefreshingIncomingFriendRequestsList;
    private bool _isRefreshingVersionSelection;
    private bool _isRefreshingQuickVersionSelection;
    private bool _isRefreshingCloudFriends;
    private readonly MediaPlayer _clickSoundPlayer = new();
    private bool _clickSoundLoaded;
    private bool _startupInitializationStarted;
    private readonly DispatcherTimer _backgroundRotationTimer = new();
    private readonly DispatcherTimer _skinPreviewTimer = new();
    private readonly DispatcherTimer _cloudFriendsRefreshTimer = new();
    private readonly DispatcherTimer _presenceHeartbeatTimer = new();
    private readonly DispatcherTimer _vesperNetStatusTimer = new();
    private readonly SemaphoreSlim _publishedJoinEndpointLock = new(1, 1);
    private int _isInternetJoinEndpointPromotionInProgress;
    private IReadOnlyList<MinecraftVersionEntry> _availableVersions = [];
    private IReadOnlyList<VersionSelectionEntry> _availableVersionChoices = [];
    private IReadOnlyList<RecommendedModCatalogItem> _recommendedModCatalog = [];
    private IReadOnlyList<RecommendedModCatalogItem> _filteredRecommendedModCatalog = [];
    private readonly HashSet<string> _favoriteModProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedModsCategoryFilter = AllModsCategoryFilter;
    private VersionSelectionEntry? _selectedVersionChoice;
    private MinecraftVersionEntry? _selectedVersion;
    private bool _isBusy;
    private Process? _gameProcess;
    private readonly ProcessMonitor _gameProcessMonitor = new();
    private string? _runningGameInstanceDirectory;
    private string? _runningGameVersionId;
    private string? _pendingDirectConnectServerAddress;
    private int? _pendingDirectConnectServerPort;
    private string? _pendingDirectConnectLabel;
    private VesperRelaySessionInfo? _activeRelaySession;
    private DateTimeOffset _activeRelaySessionExpiresAtUtc = DateTimeOffset.MinValue;
    private int? _activeRelayLanPort;
    private int? _lastDetectedLanPort;
    private DateTimeOffset _lastDetectedLanPortAtUtc = DateTimeOffset.MinValue;
    private readonly HashSet<string> _activeRelayHostConnections = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VesperGuestRelayTunnel> _activeGuestRelayTunnels = [];
    private int _isRelayHostPollInProgress;
    private PublishedJoinEndpoint? _publishedJoinEndpoint;
    private DateTimeOffset _publishedJoinEndpointExpiresAtUtc = DateTimeOffset.MinValue;
    private int? _publishedJoinLocalPort;
    private NatDevice? _publishedJoinNatDevice;
    private Mapping? _publishedJoinNatMapping;
    private string? _appliedBackgroundImagePath;
    private GlassThemeTone? _appliedGlassThemeTone;
    private AxisAngleRotation3D? _skinPreviewRotation;
    private GeometryModel3D? _skinPreviewHeadModel;
    private GeometryModel3D? _skinPreviewHatModel;
    private AccountEntryMode _accountEntryMode;
    private LauncherUiState _uiState = new();
    private bool _isEditingIncognitoNickname;
    private bool _isRefreshingRecommendedModCatalog;
    private bool _isRecommendedModCatalogLoadQueued;
    private bool _settingsPanelDefaultsCached;
    private bool _isApplyingLauncherSettingsState;
    private string _activeLauncherSettingsTabId = LauncherSettingsTabLaunch;
    private Brush? _settingsPanelDefaultBackground;
    private Brush? _settingsPanelDefaultBorderBrush;
    private HwndSource? _windowSource;
    private int _modsCatalogLoadingSpinnerFrame;
    private bool _modsCatalogLoadingSpinnerRenderingSubscribed;
    private TimeSpan _modsCatalogLoadingSpinnerLastRenderTime;
    private bool _isInSizeMove;
    private bool _resizePerformanceModeActive;
    private BitmapScalingMode _backgroundPhotoScalingMode = BitmapScalingMode.HighQuality;
    private static readonly HttpClient AccountSyncHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient CloudAssetHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient ModIconHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient VesperNetControlHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private int _recommendedModIconLoadGeneration;
    private string? _recommendedModCatalogContextKey;

    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
    private readonly HashSet<RecommendedCatalogContentKind> _loadedRecommendedCatalogKinds = [];

    private sealed record VersionSelectionEntry(
        string Key,
        string DisplayName,
        string BaseVersionId,
        MinecraftVersionEntry? Version,
        ModLoaderKind? AutoInstallLoaderKind,
        IReadOnlyList<ModLoaderKind>? AutoInstallLoaderKinds = null,
        string? AvailabilityNote = null);

    private sealed record BackgroundSlotCandidate(
        FileInfo File,
        int StartMinuteOfDay,
        int EndMinuteOfDay,
        int Priority);

    private sealed record SkinFileEntry(string FileName, string FullPath)
    {
        public override string ToString() => FileName;
    }

    private sealed record InstalledModEntry(string FileName, string FullPath)
    {
        public string DisplayName => Path.GetFileNameWithoutExtension(FileName);
        public override string ToString() => FileName;
    }

    private sealed record SkinModelOption(string Id, string DisplayName, SkinModelPreference Preference)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record LauncherLanguageOption(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record LoginFormPlacementOption(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record LauncherDirectoryViewOption(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record LauncherJavaRuntimeOption(bool UseSystemJava, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed class LauncherUiState
    {
        public string? SelectedSkinFileName { get; init; }
        public string? BackgroundPresetId { get; init; } = DefaultBackgroundPresetId;
        public string? SkinModelPreferenceId { get; init; } = DefaultSkinModelPreferenceId;
        public string? LastLaunchedVersionId { get; init; }
        public bool UseSystemJava { get; init; } = true;
        public string? JavaExecutablePath { get; init; } = DefaultJavaExecutable;
        public int MemoryMb { get; init; } = DefaultMemoryMb;
        public string? ExtraJvmArgs { get; init; } = string.Empty;
        public bool ShowJvmArgs { get; init; }
        public bool? AutoOptimizeMemory { get; init; } = true;
        public string? MinecraftLanguageCode { get; init; } = AutomaticMinecraftLanguageCode;
        public string? LoginFormPlacementId { get; init; } = DefaultLoginFormPlacementId;
        public string? LauncherDirectoryViewId { get; init; } = DefaultLauncherDirectoryViewId;
        public bool AutoMinimizeOnLaunch { get; init; }
        public bool RestoreLauncherAfterGameExit { get; init; } = true;
        public bool ClickSoundEnabled { get; init; } = true;
    }

    private sealed class LauncherAccountState
    {
        public string Username { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string PasswordSalt { get; init; } = string.Empty;
        public string PasswordAlgorithm { get; init; } = "PBKDF2-SHA256";
        public int PasswordIterations { get; init; } = PasswordHashIterations;
        public string CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
        public string? CloudSyncedAtUtc { get; init; }
        public string? AvatarFileName { get; init; }
        public string? AccessToken { get; init; }
        public string? AccessTokenExpiresAtUtc { get; init; }
        public string? LastAuthenticatedAtUtc { get; init; }
    }

    private sealed class GuestIdentityState
    {
        public string Username { get; init; } = string.Empty;
        public string CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    }

    private sealed class AccountSyncConfig
    {
        public string? RegisterUrl { get; init; }
        public string? LoginUrl { get; init; }
        public string? CredentialInfoUrl { get; init; }
        public string? ProfileAvatarUrl { get; init; }
        public string? PresencePingUrl { get; init; }
        public string? FriendsUrl { get; init; }
        public string? FriendRequestUrl { get; init; }
        public string? FriendRespondUrl { get; init; }
        public string? FriendRemoveUrl { get; init; }
        public string? MeUrl { get; init; }
        public string? LogoutUrl { get; init; }
        public string? AuthorizationHeaderName { get; init; }
        public string? AuthorizationHeaderValue { get; init; }
    }

    private sealed class CloudAuthResponse
    {
        public bool Ok { get; init; }
        public CloudAuthUser? User { get; init; }
        public CloudAvatarPayload? Avatar { get; init; }
        public string? AccessToken { get; init; }
        public string? ExpiresAtUtc { get; init; }
        public string? Error { get; init; }
        public string? Details { get; init; }
        public string? PasswordSalt { get; init; }
        public string? PasswordAlgorithm { get; init; }
        public int? PasswordIterations { get; init; }
    }

    private sealed class CloudAuthUser
    {
        public string? Username { get; init; }
    }

    private sealed class CloudAvatarPayload
    {
        public string? ContentType { get; init; }
        public string? ImageBase64 { get; init; }
        public string? ImageUrl { get; init; }
        public long? ByteLength { get; init; }
        public string? UpdatedAtUtc { get; init; }
        public string? StorageProvider { get; init; }
    }

    private sealed record CloudAuthRequestResult(
        bool Success,
        string? ErrorMessage,
        string? AccessToken,
        string? ExpiresAtUtc,
        string? Username);

    private sealed class CloudFriendsResponse
    {
        public bool Ok { get; init; }
        public List<CloudFriendUser>? Friends { get; init; }
        public List<CloudIncomingFriendRequestResponse>? IncomingRequests { get; init; }
        public List<CloudFriendUser>? OutgoingRequests { get; init; }
        public string? Error { get; init; }
        public string? Details { get; init; }
    }

    private sealed class CloudFriendUser
    {
        public string? Username { get; init; }
        public CloudAvatarPayload? Avatar { get; init; }
        public bool? IsOnline { get; init; }
        public string? LastSeenAtUtc { get; init; }
        public string? ActivityKind { get; init; }
        public string? ActivityName { get; init; }
        public string? VersionId { get; init; }
        public string? JoinHost { get; init; }
        public int? JoinPort { get; init; }
        public string? RelayRoomId { get; init; }
        public string? RelayTransportMode { get; init; }
        public bool? IsJoinable { get; init; }
    }

    private sealed class CloudIncomingFriendRequestResponse
    {
        public long Id { get; init; }
        public CloudFriendUser? User { get; init; }
        public string? CreatedAtUtc { get; init; }
    }

    private sealed record CloudFriendListItem(
        string Username,
        BitmapSource? AvatarImage,
        string? AvatarFilePath,
        string AvatarPlaceholder,
        bool IsOnline,
        string PresenceText,
        string ActivityText,
        string? VersionText,
        string? JoinAddressText,
        string? JoinHost,
        int? JoinPort,
        string? RelayRoomId,
        string? RelayTransportMode,
        bool HasJoinEndpoint,
        bool ShowConnectButton,
        bool CanConnect,
        string? ConnectTooltip,
        string? PreferredVersionId)
    {
        public override string ToString() => Username;
    }

    private sealed record DetectedServerEndpoint(string Host, int Port);

    private sealed record GameActivitySnapshot(
        string ActivityKind,
        string? ActivityName,
        string? VersionId,
        string? JoinHost,
        int? JoinPort,
        string? RelayRoomId,
        string? RelayTransportMode,
        bool IsJoinable);

    private sealed record PublishedJoinEndpoint(
        string Host,
        int Port,
        bool IsInternetEndpoint);

    private sealed record LanAddressCandidate(
        string Host,
        bool IsVpnLike,
        bool IsPrivateLan,
        bool HasGateway,
        long Speed);

    private sealed record CloudIncomingFriendRequestItem(
        long RequestId,
        string Username,
        string? CreatedAtUtc,
        BitmapSource? AvatarImage,
        string? AvatarFilePath,
        string AvatarPlaceholder,
        string SubtitleText)
    {
        public override string ToString() => Username;
    }

    private sealed record CloudFriendOperationResult(bool Success, string? ErrorMessage);
    private sealed record IncognitoNicknameCheckResult(bool Success, bool IsAvailable, string? ErrorMessage, string? ExistingUsername);
    private sealed record VesperNetDiagnosticState(
        bool IsInstalled,
        bool IsReachable,
        bool OverlayConnected,
        string? VirtualIp,
        string? TransportMode,
        string StatusText);

    private sealed class VesperNetHealthResponse
    {
        public bool Ok { get; init; }
        public string? ServiceName { get; init; }
        public string? Version { get; init; }
        public string? StartedAtUtc { get; init; }
        public bool AdapterInstalled { get; init; }
        public bool OverlayConnected { get; init; }
        public string? TransportMode { get; init; }
        public string? VirtualIp { get; init; }
        public string? Note { get; init; }
    }

    private sealed class VesperNetOverlayConnectResponse
    {
        public bool Ok { get; init; }
        public string? LocalIp { get; init; }
        public string? PeerIp { get; init; }
        public string? TransportMode { get; init; }
        public string? Error { get; init; }
        public string? Details { get; init; }
    }

    private VesperNetDiagnosticState _vesperNetDiagnosticState =
        new(false, false, false, null, null, "VesperNet: проверка состояния...");

    public MainWindow()
    {
        InitializeComponent();
        _launcherLogPath = _launcherLogger.LogFilePath;
        CacheSettingsPanelDefaults();
        _savedUsernamesPath = GetLauncherDataFilePath("saved-usernames.json");
        _friendsPath = GetLauncherDataFilePath("friends.json");
        _uiStatePath = GetLauncherDataFilePath("launcher-ui-state.json");
        _accountStatePath = GetLauncherDataFilePath("account-state.json");
        _guestIdentityStatePath = GetLauncherDataFilePath("guest-identity.json");
        _accountSyncConfigPath = GetLauncherDataFilePath("account-sync.json");
        _modFavoritesPath = GetLauncherDataFilePath("mod-favorites.json");
        _installedCatalogModsPath = GetLauncherDataFilePath("installed-catalog-mods.json");
        EnsureFirstRunCleanState();
        EnsureSharedSkinRegistryConfig();
        EnsureSharedAccountSyncConfig();
        Loaded += MainWindow_OnLoaded;
        Closed += MainWindow_OnClosed;
        _gameProcessMonitor.ProcessExited += GameProcessMonitor_OnProcessExited;
        SizeChanged += MainWindow_OnSizeChanged;
        StateChanged += MainWindow_OnStateChanged;
        _backgroundRotationTimer.Interval = TimeSpan.FromMinutes(1);
        _backgroundRotationTimer.Tick += BackgroundRotationTimer_OnTick;
        _skinPreviewTimer.Interval = TimeSpan.FromMilliseconds(40);
        _skinPreviewTimer.Tick += SkinPreviewTimer_OnTick;
        _cloudFriendsRefreshTimer.Interval = HiddenFriendsRefreshInterval;
        _cloudFriendsRefreshTimer.Tick += CloudFriendsRefreshTimer_OnTick;
        _presenceHeartbeatTimer.Interval = PresenceHeartbeatInterval;
        _presenceHeartbeatTimer.Tick += PresenceHeartbeatTimer_OnTick;
        _vesperNetStatusTimer.Interval = TimeSpan.FromSeconds(4);
        _vesperNetStatusTimer.Tick += VesperNetStatusTimer_OnTick;
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(PlayClickSound), handledEventsToo: true);
        LoadSavedUsernames();
        LoadFriends();
        LoadUiState();
        LoadAccountState();
        LoadGuestIdentityState();
        LoadFavoriteMods();
        LoadInstalledCatalogMods();
        InitializeSkinModelComboBox();
        InitializeMinecraftLanguageComboBox();
        InitializeLauncherLoginFormPlacementComboBox();
        InitializeLauncherDirectoryViewComboBox();
        InitializeLauncherJavaRuntimeComboBox();
        ApplyPersistedLauncherSettingsToControls();
        ShowLauncherSettingsTab(_activeLauncherSettingsTabId);
        if (HasAuthenticatedCloudSession())
        {
            UsernameTextBox.Text = _accountState!.Username;
        }
        else if (HasIncognitoIdentity())
        {
            UsernameTextBox.Text = _guestIdentityState!.Username;
        }
        else if (HasRegisteredAccount())
        {
            UsernameTextBox.Text = _accountState!.Username;
        }
        else
        {
            UsernameTextBox.Text = _savedUsernames.FirstOrDefault() ?? string.Empty;
        }

        ApplyAccountUiState();
        ApplyLauncherSettingsUiState();
        ApplyAccountShellPlacement();
        UpdateLauncherOverviewPresentation();
        UpdateMemoryPresentation();
        UpdateVesperNetStatusText();
    }

    private bool HasRegisteredAccount()
    {
        return _accountState is not null &&
               !string.IsNullOrWhiteSpace(_accountState.Username) &&
               (!string.IsNullOrWhiteSpace(_accountState.PasswordHash) ||
                !string.IsNullOrWhiteSpace(_accountState.AccessToken));
    }

    private bool HasAuthenticatedCloudSession()
    {
        return HasRegisteredAccount() &&
               !string.IsNullOrWhiteSpace(_accountState?.AccessToken);
    }

    private bool HasIncognitoIdentity()
    {
        return false;
    }

    private bool IsIncognitoOnlyMode()
    {
        return HasIncognitoIdentity() && !HasAuthenticatedCloudSession();
    }

    private bool CanAccessFriendsFeature()
    {
        return !IsIncognitoOnlyMode();
    }

    private bool CanManageCloudFriends()
    {
        return HasAuthenticatedCloudSession();
    }

    private bool HasEarlyPlayersAchievement()
    {
        if (IsIncognitoOnlyMode() || !HasRegisteredAccount())
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(_accountState?.CreatedAtUtc, out var createdAtUtc))
        {
            return false;
        }

        return createdAtUtc < EarlyPlayersAchievementCutoffUtc;
    }

    private bool HasLaunchIdentity()
    {
        return HasAuthenticatedCloudSession() || HasIncognitoIdentity();
    }

    private string? GetActiveNickname()
    {
        if (HasAuthenticatedCloudSession())
        {
            return _accountState!.Username;
        }

        if (HasIncognitoIdentity())
        {
            return _guestIdentityState!.Username;
        }

        if (HasRegisteredAccount())
        {
            return _accountState!.Username;
        }

        return null;
    }

    private string? GetLaunchNickname()
    {
        if (HasAuthenticatedCloudSession())
        {
            return _accountState!.Username;
        }

        if (HasIncognitoIdentity())
        {
            return _guestIdentityState!.Username;
        }

        return null;
    }

    private void ResetAccountSessionState()
    {
        if (_accountState is null)
        {
            return;
        }

        _accountState = new LauncherAccountState
        {
            Username = _accountState.Username,
            PasswordHash = _accountState.PasswordHash,
            PasswordSalt = _accountState.PasswordSalt,
            PasswordAlgorithm = _accountState.PasswordAlgorithm,
            PasswordIterations = _accountState.PasswordIterations,
            CreatedAtUtc = _accountState.CreatedAtUtc,
            CloudSyncedAtUtc = _accountState.CloudSyncedAtUtc,
            AvatarFileName = _accountState.AvatarFileName,
            AccessToken = null,
            AccessTokenExpiresAtUtc = null,
            LastAuthenticatedAtUtc = null
        };

        SaveAccountState();
        ClearCloudFriendsSnapshot();
        SavedUsernamesPanel.Visibility = Visibility.Collapsed;
        ApplyAccountUiState();
        RefreshFriendsSection();
    }

    private void ClearAuthenticatedCloudSession(string statusMessage)
    {
        ResetAccountSessionState();
        SetStatus(statusMessage);
    }

    private bool HandleUnauthorizedCloudResponse(HttpStatusCode statusCode, string? responseBody)
    {
        if (statusCode != HttpStatusCode.Unauthorized)
        {
            return false;
        }

        var resolvedError = ExtractCloudErrorMessage(
            responseBody,
            "Сессия аккаунта больше не действительна.");
        var statusMessage = string.Equals(
            resolvedError,
            "Сессия аккаунта истекла. Войди заново.",
            StringComparison.Ordinal)
            ? resolvedError
            : $"{resolvedError} Войди заново.";
        ClearAuthenticatedCloudSession(statusMessage);
        return true;
    }

    private void LoadAccountState()
    {
        _accountState = null;
        if (!File.Exists(_accountStatePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_accountStatePath);
            var loaded = JsonSerializer.Deserialize<LauncherAccountState>(json);
            if (loaded is null)
            {
                return;
            }

            var normalizedUsername = NormalizeMinecraftUsername(loaded.Username);
            if (!UsernameRegex.IsMatch(normalizedUsername))
            {
                return;
            }

            _accountState = new LauncherAccountState
            {
                Username = normalizedUsername,
                PasswordHash = loaded.PasswordHash ?? string.Empty,
                PasswordSalt = loaded.PasswordSalt ?? string.Empty,
                PasswordAlgorithm = string.IsNullOrWhiteSpace(loaded.PasswordAlgorithm)
                    ? "PBKDF2-SHA256"
                    : loaded.PasswordAlgorithm,
                PasswordIterations = loaded.PasswordIterations > 0
                    ? loaded.PasswordIterations
                    : PasswordHashIterations,
                CreatedAtUtc = string.IsNullOrWhiteSpace(loaded.CreatedAtUtc)
                    ? DateTimeOffset.UtcNow.ToString("O")
                    : loaded.CreatedAtUtc,
                CloudSyncedAtUtc = loaded.CloudSyncedAtUtc,
                AvatarFileName = loaded.AvatarFileName,
                AccessToken = loaded.AccessToken,
                AccessTokenExpiresAtUtc = loaded.AccessTokenExpiresAtUtc,
                LastAuthenticatedAtUtc = loaded.LastAuthenticatedAtUtc
            };
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки аккаунта");
        }
    }

    private void LoadGuestIdentityState()
    {
        _guestIdentityState = null;
        try
        {
            if (File.Exists(_guestIdentityStatePath))
            {
                File.Delete(_guestIdentityStatePath);
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка удаления устаревшего инкогнито-профиля");
        }
    }

    private void SaveAccountState()
    {
        if (_accountState is null || !UsernameRegex.IsMatch(NormalizeMinecraftUsername(_accountState.Username)))
        {
            TryDeleteFile(_accountStatePath);
            return;
        }

        try
        {
            var accountDirectory = Path.GetDirectoryName(_accountStatePath);
            if (!string.IsNullOrWhiteSpace(accountDirectory))
            {
                Directory.CreateDirectory(accountDirectory);
            }

            var persistedState = new LauncherAccountState
            {
                Username = NormalizeMinecraftUsername(_accountState.Username),
                PasswordHash = string.Empty,
                PasswordSalt = string.Empty,
                PasswordAlgorithm = "PBKDF2-SHA256",
                PasswordIterations = PasswordHashIterations,
                CreatedAtUtc = _accountState.CreatedAtUtc,
                CloudSyncedAtUtc = _accountState.CloudSyncedAtUtc,
                AvatarFileName = _accountState.AvatarFileName,
                AccessToken = _accountState.AccessToken,
                AccessTokenExpiresAtUtc = _accountState.AccessTokenExpiresAtUtc,
                LastAuthenticatedAtUtc = _accountState.LastAuthenticatedAtUtc
            };

            var json = JsonSerializer.Serialize(persistedState, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_accountStatePath, json);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка сохранения аккаунта");
        }
    }

    private void SaveGuestIdentityState()
    {
        ClearGuestIdentityState();
    }

    private void ClearGuestIdentityState()
    {
        _guestIdentityState = null;
        _isEditingIncognitoNickname = false;

        try
        {
            if (File.Exists(_guestIdentityStatePath))
            {
                File.Delete(_guestIdentityStatePath);
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка удаления инкогнито-профиля");
        }
    }

    private void ApplyAccountUiState()
    {
        var hasAccount = HasRegisteredAccount();
        var activeNickname = GetActiveNickname();
        UsernameTextBox.IsReadOnly = true;

        if (!string.IsNullOrWhiteSpace(activeNickname))
        {
            UsernameTextBox.Text = activeNickname;
        }
        else if (hasAccount)
        {
            UsernameTextBox.Text = _accountState!.Username;
        }
        else if (string.IsNullOrWhiteSpace(UsernameTextBox.Text))
        {
            UsernameTextBox.Text = string.Empty;
        }

        SavedUsernamesPanel.Visibility = Visibility.Collapsed;
        RefreshSavedUsernamesList(activeNickname);
        UpdateCurrentNicknameDisplay();
        RefreshAccountSection();
        RefreshFriendNotificationsPopup();
        UpdateLaunchButtonIdleState();
    }

    private void MarkAccountAsCloudSyncedNow()
    {
        if (!HasRegisteredAccount())
        {
            return;
        }

        _accountState = new LauncherAccountState
        {
            Username = _accountState!.Username,
            PasswordHash = _accountState.PasswordHash,
            PasswordSalt = _accountState.PasswordSalt,
            PasswordAlgorithm = _accountState.PasswordAlgorithm,
            PasswordIterations = _accountState.PasswordIterations,
            CreatedAtUtc = _accountState.CreatedAtUtc,
            CloudSyncedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            AvatarFileName = _accountState.AvatarFileName,
            AccessToken = _accountState.AccessToken,
            AccessTokenExpiresAtUtc = _accountState.AccessTokenExpiresAtUtc,
            LastAuthenticatedAtUtc = _accountState.LastAuthenticatedAtUtc
        };
        SaveAccountState();
    }

    private void UpdateCurrentNicknameDisplay()
    {
        var nicknameText = GetActiveNickname() ?? "Создайте аккаунт";

        if (CurrentNicknameDisplay is not null)
        {
            CurrentNicknameDisplay.Text = nicknameText;
        }

        if (LeftNicknameDisplay is not null)
        {
            LeftNicknameDisplay.Text = nicknameText;
        }
    }

    private void SetAccountEntryMode(AccountEntryMode mode, bool clearPassword = true)
    {
        _accountEntryMode = mode;

        if (clearPassword && AccountPasswordBox is not null && mode != AccountEntryMode.None)
        {
            AccountPasswordBox.Password = string.Empty;
        }

        ApplyAccountEntryModeUi();
    }

    private void ApplyAccountEntryModeUi()
    {
        if (LoginAccountButton is null ||
            CreateAccountButton is null ||
            EnterIncognitoButton is null ||
            AccountAuthFormBorder is null ||
            AccountAuthTitleTextBlock is null ||
            AccountAuthSubtitleTextBlock is null ||
            AccountNicknameLabelTextBlock is null ||
            AccountPasswordFieldPanel is null ||
            AccountAuthPrimaryButton is null ||
            AccountModeHintTextBlock is null)
        {
            return;
        }

        var effectiveMode = _accountEntryMode switch
        {
            AccountEntryMode.Register => AccountEntryMode.Register,
            _ => AccountEntryMode.Login
        };

        void ApplyModeButtonStyle(Button button, bool selected)
        {
            button.SetResourceReference(FrameworkElement.StyleProperty,
                selected ? "AccountModeTabButtonSelectedStyle" : "AccountModeTabButtonStyle");
        }

        ApplyModeButtonStyle(LoginAccountButton, effectiveMode == AccountEntryMode.Login);
        ApplyModeButtonStyle(CreateAccountButton, effectiveMode == AccountEntryMode.Register);
        EnterIncognitoButton.Visibility = Visibility.Collapsed;

        AccountAuthFormBorder.Visibility = Visibility.Visible;
        AccountPasswordFieldPanel.Visibility = Visibility.Visible;

        switch (effectiveMode)
        {
            case AccountEntryMode.Login:
                AccountModeHintTextBlock.Text = "Вход в аккаунт Vesper.";
                AccountAuthTitleTextBlock.Text = "Вход";
                AccountAuthSubtitleTextBlock.Text = "Используй свой ник и пароль.";
                AccountNicknameLabelTextBlock.Text = "Ник аккаунта";
                AccountAuthPrimaryButton.Content = "Войти";
                break;
            case AccountEntryMode.Register:
                AccountModeHintTextBlock.Text = "Создание нового аккаунта Vesper.";
                AccountAuthTitleTextBlock.Text = "Регистрация";
                AccountAuthSubtitleTextBlock.Text = "Придумай ник и пароль для нового аккаунта.";
                AccountNicknameLabelTextBlock.Text = "Ник аккаунта";
                AccountAuthPrimaryButton.Content = "Зарегистрироваться";
                break;
            default:
                AccountModeHintTextBlock.Text = "Вход в аккаунт Vesper.";
                AccountAuthTitleTextBlock.Text = "Войти";
                AccountAuthSubtitleTextBlock.Text = "Используй свой ник и пароль.";
                AccountNicknameLabelTextBlock.Text = "Ник аккаунта";
                AccountAuthPrimaryButton.Content = "Войти";
                break;
        }

        var hasAccount = HasAuthenticatedCloudSession();
        var canEdit = !_isBusy && !hasAccount;
        LoginAccountButton.IsEnabled = canEdit;
        CreateAccountButton.IsEnabled = canEdit;
        AccountAuthPrimaryButton.IsEnabled = canEdit;
        AccountPasswordBox.IsEnabled = canEdit;
    }

    private void RefreshAccountSection()
    {
        var hasAccount = HasAuthenticatedCloudSession();
        var hasStoredProfile = HasRegisteredAccount();
        var hasGuestIdentity = HasIncognitoIdentity();
        if (hasAccount)
        {
            var cloudStatus = string.IsNullOrWhiteSpace(_accountState!.CloudSyncedAtUtc)
                ? "Облако: не синхронизирован"
                : $"Облако: синхронизирован ({_accountState.CloudSyncedAtUtc})";
            var sessionStatus = string.IsNullOrWhiteSpace(_accountState.AccessTokenExpiresAtUtc)
                ? "Сессия: локальная"
                : $"Сессия до: {_accountState.AccessTokenExpiresAtUtc}";
            AccountStateTextBlock.Text =
                $"Аккаунт: {_accountState.Username}{Environment.NewLine}{cloudStatus}{Environment.NewLine}{sessionStatus}";
            _accountEntryMode = AccountEntryMode.None;
            AccountCreatePanel.Visibility = Visibility.Collapsed;
            AccountPlayerPanel.Visibility = Visibility.Visible;
            PlayerMenuNicknameTextBlock.Text = _accountState.Username;
            AccountNicknameTextBox.Text = _accountState.Username;
            AccountNicknameTextBox.IsReadOnly = true;
            AccountPasswordBox.IsEnabled = false;
            AccountPasswordBox.Password = "********";
            LoginAccountButton.IsEnabled = false;
            CreateAccountButton.IsEnabled = false;
            CreateAccountButton.Content = "Регистрация";
            RefreshAccountAvatarPreview();
            RefreshAchievementSection();
            RefreshFriendsAccessUi();
            return;
        }

        if (hasGuestIdentity && !_isEditingIncognitoNickname)
        {
            AccountStateTextBlock.Text = $"Инкогнито: {_guestIdentityState!.Username}";
            _accountEntryMode = AccountEntryMode.None;
            AccountCreatePanel.Visibility = Visibility.Collapsed;
            AccountPlayerPanel.Visibility = Visibility.Visible;
            PlayerMenuNicknameTextBlock.Text = _guestIdentityState.Username;
            AccountNicknameTextBox.Text = _guestIdentityState.Username;
            AccountNicknameTextBox.IsReadOnly = false;
            AccountPasswordBox.IsEnabled = false;
            AccountPasswordBox.Password = string.Empty;
            LoginAccountButton.IsEnabled = true;
            CreateAccountButton.IsEnabled = true;
            EnterIncognitoButton.IsEnabled = false;
            CreateAccountButton.Content = "Регистрация";
            ChangeAvatarButton.Content = "Сменить ник";
            LogoutAccountButton.Content = "Выйти из инкогнито";
            RefreshAccountAvatarPreview();
            RefreshAchievementSection();
            RefreshFriendsAccessUi();
            return;
        }

        var inactiveSessionMessage = hasStoredProfile &&
                                     string.IsNullOrWhiteSpace(_accountState!.LastAuthenticatedAtUtc)
            ? "Вы вышли из аккаунта. Войди снова."
            : "Сессия истекла. Войди снова.";
        AccountStateTextBlock.Text = hasStoredProfile
            ? $"Аккаунт: {_accountState!.Username}{Environment.NewLine}{inactiveSessionMessage}"
            : hasGuestIdentity
                ? "Укажи ник и нажми Инкогнито, чтобы играть без регистрации."
            : "Войди или зарегистрируйся перед первым запуском.";
        AccountCreatePanel.Visibility = Visibility.Visible;
        AccountPlayerPanel.Visibility = Visibility.Collapsed;
        PlayerMenuNicknameTextBlock.Text = "—";
        if (_isEditingIncognitoNickname)
        {
            _accountEntryMode = AccountEntryMode.Guest;
        }
        else if (hasStoredProfile)
        {
            _accountEntryMode = AccountEntryMode.Login;
        }
        else if (_accountEntryMode == AccountEntryMode.None)
        {
            _accountEntryMode = AccountEntryMode.Login;
        }
        if (hasStoredProfile)
        {
            AccountNicknameTextBox.Text = _accountState!.Username;
        }
        else if (hasGuestIdentity)
        {
            AccountNicknameTextBox.Text = _guestIdentityState!.Username;
        }
        else if (string.IsNullOrWhiteSpace(AccountNicknameTextBox.Text))
        {
            AccountNicknameTextBox.Text = NormalizeMinecraftUsername(UsernameTextBox.Text.Trim());
        }

        AccountNicknameTextBox.IsReadOnly = false;
        AccountNicknameTextBox.IsEnabled = !_isBusy;
        AccountNicknameTextBox.Focusable = true;
        AccountPasswordBox.IsEnabled = true;
        AccountPasswordBox.Password = string.Empty;
        LoginAccountButton.IsEnabled = true;
        CreateAccountButton.IsEnabled = true;
        EnterIncognitoButton.IsEnabled = false;
        CreateAccountButton.Content = "Регистрация";
        ChangeAvatarButton.Content = "Сменить аватар";
        LogoutAccountButton.Content = "Выйти из аккаунта";
        ApplyAccountEntryModeUi();
        RefreshAccountAvatarPreview();
        RefreshAchievementSection();
        RefreshFriendsAccessUi();
    }

    private void FocusAccountNicknameEditorIfNeeded()
    {
        if (AccountCreatePanel is null ||
            AccountNicknameTextBox is null ||
            _activeSidePanelSection != SidePanelSection.Account ||
            AccountCreatePanel.Visibility != Visibility.Visible ||
            _isBusy)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_activeSidePanelSection != SidePanelSection.Account ||
                AccountCreatePanel.Visibility != Visibility.Visible ||
                _isBusy)
            {
                return;
            }

            AccountNicknameTextBox.IsEnabled = true;
            AccountNicknameTextBox.IsReadOnly = false;
            AccountNicknameTextBox.Focusable = true;
            AccountNicknameTextBox.CaretIndex = AccountNicknameTextBox.Text?.Length ?? 0;
            AccountNicknameTextBox.Focus();
            Keyboard.Focus(AccountNicknameTextBox);
        }, DispatcherPriority.Input);
    }

    private void RefreshAchievementSection()
    {
        if (EarlyPlayersAchievementButton is null ||
            EarlyPlayersAchievementPlaceholderButton is null)
        {
            return;
        }

        var hasAchievement = HasEarlyPlayersAchievement();
        EarlyPlayersAchievementButton.Visibility = hasAchievement
            ? Visibility.Visible
            : Visibility.Collapsed;
        EarlyPlayersAchievementPlaceholderButton.Visibility = hasAchievement
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (EarlyPlayersAchievementDescriptionTextBlock is not null)
        {
            EarlyPlayersAchievementDescriptionTextBlock.Text =
                "Выдано игрокам с Vesper-аккаунтом, созданным до 2027 года.";
        }

        if (EarlyPlayersAchievementPlaceholderTitleTextBlock is null ||
            EarlyPlayersAchievementPlaceholderDescriptionTextBlock is null)
        {
            return;
        }

        if (IsIncognitoOnlyMode())
        {
            EarlyPlayersAchievementPlaceholderTitleTextBlock.Text = "Недоступно в инкогнито";
            EarlyPlayersAchievementPlaceholderDescriptionTextBlock.Text =
                "Эта награда выдаётся только обычным Vesper-аккаунтам и не доступна для инкогнито.";
            return;
        }

        if (HasRegisteredAccount())
        {
            EarlyPlayersAchievementPlaceholderTitleTextBlock.Text = "Награда недоступна";
            EarlyPlayersAchievementPlaceholderDescriptionTextBlock.Text =
                "Награда выдаётся только Vesper-аккаунтам, созданным до 2027 года.";
            return;
        }

        EarlyPlayersAchievementPlaceholderTitleTextBlock.Text = "Свободный слот";
        EarlyPlayersAchievementPlaceholderDescriptionTextBlock.Text =
            "Здесь поместится следующая ачивка игрока.";
    }

    private void RefreshFriendsAccessUi()
    {
        var canAccessFriends = CanAccessFriendsFeature();
        var canManageCloudFriends = CanManageCloudFriends();

        if (FriendsButton is not null)
        {
            FriendsButton.IsEnabled = !_isBusy && canAccessFriends;
            FriendsButton.Opacity = canAccessFriends ? 1.0 : 0.55;
        }

        if (FriendNotificationsButton is not null)
        {
            FriendNotificationsButton.IsEnabled = !_isBusy && canAccessFriends;
            FriendNotificationsButton.Opacity = canAccessFriends ? 1.0 : 0.55;
        }

        if (FriendNicknameTextBox is not null)
        {
            FriendNicknameTextBox.IsEnabled = !_isBusy && canManageCloudFriends;
        }

        if (FriendsListBox is not null)
        {
            FriendsListBox.IsEnabled = !_isBusy && canManageCloudFriends;
        }

        if (IncomingFriendRequestsListBox is not null)
        {
            IncomingFriendRequestsListBox.IsEnabled = !_isBusy && canManageCloudFriends;
        }

        var hasSelectedIncomingFriendRequest = IncomingFriendRequestsListBox?.SelectedItem is not null;

        if (AcceptFriendRequestButton is not null)
        {
            AcceptFriendRequestButton.IsEnabled = !_isBusy &&
                                                  canManageCloudFriends &&
                                                  hasSelectedIncomingFriendRequest;
        }

        if (DeclineFriendRequestButton is not null)
        {
            DeclineFriendRequestButton.IsEnabled = !_isBusy &&
                                                   canManageCloudFriends &&
                                                   hasSelectedIncomingFriendRequest;
        }

        if (!canAccessFriends && FriendNotificationsPopup is not null)
        {
            FriendNotificationsPopup.IsOpen = false;
        }
    }

    private static string GetLegacyLauncherAppDataDirectory(bool ensureExists = true)
    {
        return LauncherDataPaths.GetLegacyWindowsDataDirectory(ensureExists);
    }

    private static string GetPreferredLauncherDataDirectory()
    {
        return LauncherDataPaths.GetPreferredDataDirectory();
    }

    private static string GetLauncherDataFilePath(string fileName)
    {
        return LauncherDataPaths.GetDataFilePath(fileName);
    }

    private static void TryMigrateFilesToLauncherDirectory(
        string sourceDirectory,
        string destinationDirectory,
        string searchPattern = "*.*")
    {
        LauncherDataPaths.MigrateDirectoryContents(sourceDirectory, destinationDirectory, searchPattern);
    }

    private static string GetLegacyAccountAvatarsDirectory(bool ensureExists = true)
    {
        var path = Path.Combine(
            GetLegacyLauncherAppDataDirectory(ensureExists),
            "avatars");
        if (ensureExists)
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    private static string GetInstallLocalAccountAvatarsDirectory()
    {
        var path = Path.Combine(
            LauncherDataPaths.GetInstallLocalDataDirectory(),
            "avatars");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetAccountAvatarsDirectory()
    {
        var path = Path.Combine(
            GetPreferredLauncherDataDirectory(),
            "avatars");
        Directory.CreateDirectory(path);
        TryMigrateFilesToLauncherDirectory(GetLegacyAccountAvatarsDirectory(ensureExists: false), path);
        return path;
    }

    private static string GetCloudAvatarCacheDirectory()
    {
        var path = Path.Combine(
            GetPreferredLauncherDataDirectory(),
            "cloud-avatar-cache");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetModIconsDirectory()
    {
        var path = Path.Combine(
            GetPreferredLauncherDataDirectory(),
            "mod-icons");
        Directory.CreateDirectory(path);
        return path;
    }

    private string? ResolveAccountAvatarPath(LauncherAccountState? state)
    {
        if (state is null || string.IsNullOrWhiteSpace(state.AvatarFileName))
        {
            return null;
        }

        var avatarFileName = Path.GetFileName(state.AvatarFileName);
        if (string.IsNullOrWhiteSpace(avatarFileName))
        {
            return null;
        }

        var currentAvatarPath = Path.Combine(GetAccountAvatarsDirectory(), avatarFileName);
        if (File.Exists(currentAvatarPath))
        {
            return currentAvatarPath;
        }

        var legacyAvatarPath = Path.Combine(GetLegacyAccountAvatarsDirectory(ensureExists: false), avatarFileName);
        if (!File.Exists(legacyAvatarPath))
        {
            return null;
        }

        try
        {
            File.Copy(legacyAvatarPath, currentAvatarPath, overwrite: false);
            return currentAvatarPath;
        }
        catch
        {
            return legacyAvatarPath;
        }
    }

    private static string BuildAvatarPlaceholderText(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "AV";
        }

        var letters = username
            .Where(char.IsLetterOrDigit)
            .Take(2)
            .ToArray();

        return letters.Length == 0
            ? "AV"
            : new string(letters).ToUpperInvariant();
    }

    private void RefreshAvatarPreview(
        Image? imageControl,
        TextBlock? placeholderTextBlock,
        LauncherAccountState? state,
        string? fallbackUsername,
        int decodePixelWidth)
    {
        if (imageControl is null || placeholderTextBlock is null)
        {
            return;
        }

        imageControl.Source = null;
        placeholderTextBlock.Text = BuildAvatarPlaceholderText(state?.Username ?? fallbackUsername);
        placeholderTextBlock.Visibility = Visibility.Visible;

        var avatarPath = ResolveAccountAvatarPath(state);
        if (string.IsNullOrWhiteSpace(avatarPath))
        {
            return;
        }

        try
        {
            imageControl.Source = LoadBitmapFromFile(avatarPath, decodePixelWidth);
            placeholderTextBlock.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки аватара");
        }
    }

    private void RefreshAccountAvatarPreview()
    {
        RefreshAvatarPreview(
            AccountAvatarImage,
            AccountAvatarPlaceholderText,
            HasAuthenticatedCloudSession() ? _accountState : null,
            HasIncognitoIdentity() ? _guestIdentityState?.Username : null,
            decodePixelWidth: 192);
    }

    private void RefreshFriendsProfileAvatarPreview()
    {
        RefreshAvatarPreview(
            FriendsProfileAvatarImage,
            FriendsProfileAvatarPlaceholderText,
            HasAuthenticatedCloudSession() ? _accountState : null,
            HasIncognitoIdentity() ? _guestIdentityState?.Username : null,
            decodePixelWidth: 160);
        UpdateFriendsProfileAvatarClip();
    }

    private void UpdateFriendsProfileAvatarClip()
    {
        if (FriendsProfileAvatarBorder is null || FriendsProfileAvatarImage is null)
        {
            return;
        }

        var width = FriendsProfileAvatarBorder.ActualWidth;
        var height = FriendsProfileAvatarBorder.ActualHeight;

        if (width <= 0d || height <= 0d)
        {
            width = FriendsProfileAvatarBorder.Width;
            height = FriendsProfileAvatarBorder.Height;
        }

        if (width <= 0d || height <= 0d)
        {
            return;
        }

        var radius = Math.Max(8d, Math.Min(width, height) * 0.17d);
        FriendsProfileAvatarImage.Clip = new RectangleGeometry(new Rect(0d, 0d, width, height), radius, radius);
    }

    private static BitmapImage? LoadBitmapFromBytes(byte[] bytes, int? decodePixelWidth)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
        {
            bitmap.DecodePixelWidth = decodePixelWidth.Value;
        }

        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapImage? TryLoadBitmapFromBase64(string? imageBase64, int? decodePixelWidth)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return null;
        }

        try
        {
            return LoadBitmapFromBytes(Convert.FromBase64String(imageBase64), decodePixelWidth);
        }
        catch
        {
            return null;
        }
    }

    private static string GetAvatarFileExtension(string? contentType) =>
        contentType?.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".png"
        };

    private static string BuildCloudAvatarCacheFileName(string username, CloudAvatarPayload? avatar)
    {
        var normalizedUsername = Regex.Replace(username.ToLowerInvariant(), @"[^a-z0-9_\-]+", "_");
        var imageBase64Hash = string.IsNullOrWhiteSpace(avatar?.ImageBase64)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(avatar.ImageBase64))).ToLowerInvariant();
        var cacheStampSource = $"{avatar?.UpdatedAtUtc}|{avatar?.ImageUrl}|{avatar?.ByteLength}|{avatar?.ContentType}|{imageBase64Hash}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(cacheStampSource));
        var cacheStamp = Convert.ToHexString(hashBytes)[..12].ToLowerInvariant();
        return $"{normalizedUsername}-cloud-avatar-{cacheStamp}{GetAvatarFileExtension(avatar?.ContentType)}";
    }

    private static string? TryResolveCachedCloudAvatarPath(string username, CloudAvatarPayload? avatar)
    {
        if (avatar is null)
        {
            return null;
        }

        try
        {
            var cacheDirectory = GetCloudAvatarCacheDirectory();
            var cachePath = Path.Combine(cacheDirectory, BuildCloudAvatarCacheFileName(username, avatar));
            return File.Exists(cachePath)
                ? cachePath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource? TryLoadCloudAvatarBitmap(string username, CloudAvatarPayload? avatar, int decodePixelWidth)
    {
        var fromBase64 = TryLoadBitmapFromBase64(avatar?.ImageBase64, decodePixelWidth);
        if (fromBase64 is not null)
        {
            return fromBase64;
        }

        var cachePath = TryResolveCachedCloudAvatarPath(username, avatar);
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return null;
        }

        try
        {
            return LoadBitmapFromFile(cachePath, decodePixelWidth);
        }
        catch
        {
            return null;
        }
    }

    private string? TryResolveCloudAvatarFilePath(string username, CloudAvatarPayload? avatar)
    {
        if (avatar is null)
        {
            return null;
        }

        var cachedPath = TryResolveCachedCloudAvatarPath(username, avatar);
        if (!string.IsNullOrWhiteSpace(cachedPath))
        {
            return cachedPath;
        }

        if (string.IsNullOrWhiteSpace(avatar.ImageBase64))
        {
            return Uri.TryCreate(avatar.ImageUrl, UriKind.Absolute, out var imageUri) &&
                (imageUri.Scheme == Uri.UriSchemeHttps || imageUri.Scheme == Uri.UriSchemeHttp)
                ? imageUri.ToString()
                : null;
        }

        try
        {
            var avatarBytes = Convert.FromBase64String(avatar.ImageBase64);
            return avatarBytes.Length > 0
                ? SaveCloudAvatarBytesToCache(username, avatar, avatarBytes)
                : null;
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка кеширования base64-аватара друга");
            return Uri.TryCreate(avatar.ImageUrl, UriKind.Absolute, out var imageUri) &&
                (imageUri.Scheme == Uri.UriSchemeHttps || imageUri.Scheme == Uri.UriSchemeHttp)
                ? imageUri.ToString()
                : null;
        }
    }

    private static string BuildPresenceText(bool isOnline, string? lastSeenAtUtc)
    {
        if (isOnline)
        {
            return "В сети";
        }

        if (DateTimeOffset.TryParse(lastSeenAtUtc, out var lastSeen))
        {
            return $"Был в сети {lastSeen.ToLocalTime():dd.MM HH:mm}";
        }

        return "Не в сети";
    }

    private static string BuildRequestSubtitleText(string? createdAtUtc)
    {
        if (!DateTimeOffset.TryParse(createdAtUtc, out var createdAt))
        {
            return "Новая заявка";
        }

        return $"Пришло {createdAt.ToLocalTime():dd.MM HH:mm}";
    }

    private static string BuildFriendActivityText(string? activityKind, string? activityName)
    {
        var normalizedKind = string.IsNullOrWhiteSpace(activityKind)
            ? "launcher"
            : activityKind.Trim().ToLowerInvariant();
        var normalizedName = string.IsNullOrWhiteSpace(activityName)
            ? null
            : activityName.Trim();

        return normalizedKind switch
        {
            "lan_host" => $"Мир: {normalizedName ?? "Локальный мир"}",
            "singleplayer" => $"Мир: {normalizedName ?? "Одиночный мир"}",
            "multiplayer" => $"Сервер: {normalizedName ?? "Сетевой сервер"}",
            "in_game" => normalizedName ?? "В игре",
            _ => "В лаунчере"
        };
    }

    private static string? BuildFriendVersionText(string? versionId)
    {
        return string.IsNullOrWhiteSpace(versionId)
            ? null
            : $"Версия: {versionId.Trim()}";
    }

    private static bool HasRelayEndpoint(string? relayRoomId, string? relayTransportMode)
    {
        return !string.IsNullOrWhiteSpace(relayRoomId) &&
               (string.IsNullOrWhiteSpace(relayTransportMode) ||
                string.Equals(relayTransportMode?.Trim(), VesperRelayTransportMode, StringComparison.OrdinalIgnoreCase) ||
                IsVesperNetOverlayTransportMode(relayTransportMode));
    }

    private static bool IsVesperNetOverlayTransportMode(string? relayTransportMode)
    {
        return !string.IsNullOrWhiteSpace(relayTransportMode) &&
               relayTransportMode.Trim().Contains("overlay", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAssumeOverlayForLegacyRelayState(
        string? joinHost,
        int? joinPort,
        string? relayRoomId,
        string? relayTransportMode,
        bool isJoinable)
    {
        if (!HasRelayEndpoint(relayRoomId, relayTransportMode) ||
            !string.IsNullOrWhiteSpace(relayTransportMode))
        {
            return false;
        }

        if (!CanReachJoinHost(joinHost, joinPort, isJoinable))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(joinHost))
        {
            return true;
        }

        if (!IPAddress.TryParse(joinHost.Trim(), out var parsedAddress))
        {
            return false;
        }

        return !IsPublicInternetAddress(parsedAddress);
    }

    private static string? BuildJoinAddressText(
        string? joinHost,
        int? joinPort,
        string? relayRoomId,
        string? relayTransportMode,
        bool isJoinable,
        bool canConnect)
    {
        if (!isJoinable)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(joinHost) || joinPort is not int port || port <= 0)
        {
            if (IsVesperNetOverlayTransportMode(relayTransportMode))
            {
                return "VesperNet Overlay";
            }

            return HasRelayEndpoint(relayRoomId, relayTransportMode) ? "Vesper Relay" : null;
        }

        var normalizedHost = joinHost.Trim();
        if (IPAddress.TryParse(normalizedHost, out var parsedAddress))
        {
            if (IsPublicInternetAddress(parsedAddress))
            {
                return $"Интернет: {normalizedHost}:{port}";
            }

            if (IsPrivateLanAddress(parsedAddress))
            {
                return canConnect
                    ? $"Сеть: {normalizedHost}:{port}"
                    : "Только локальная сеть";
            }
        }

        var addressLabel = canConnect ? "Интернет" : "Адрес";
        return $"{addressLabel}: {normalizedHost}:{port}";
    }

    private static bool CanReachJoinHost(string? joinHost, int? joinPort, bool isJoinable)
    {
        if (!isJoinable || string.IsNullOrWhiteSpace(joinHost) || joinPort is not int port || port <= 0 || port > 65535)
        {
            return false;
        }

        var normalizedHost = joinHost.Trim();
        if (!IPAddress.TryParse(normalizedHost, out var parsedAddress))
        {
            return true;
        }

        if (IsPublicInternetAddress(parsedAddress))
        {
            return true;
        }

        return IsPrivateLanAddress(parsedAddress) && IsReachablePrivateLanAddress(parsedAddress);
    }

    private static string? BuildConnectTooltip(
        bool isOnline,
        string? joinHost,
        int? joinPort,
        string? relayRoomId,
        string? relayTransportMode,
        bool isJoinable,
        bool canConnect)
    {
        if (!isOnline)
        {
            return null;
        }

        if (!isJoinable)
        {
            return "Друг сейчас в игре, но мир ещё не открыт для подключения.";
        }

        if ((string.IsNullOrWhiteSpace(joinHost) || joinPort is not int port || port <= 0 || port > 65535) &&
            HasRelayEndpoint(relayRoomId, relayTransportMode))
        {
            return "Подключиться через Vesper Relay.";
        }

        if (string.IsNullOrWhiteSpace(joinHost) || joinPort is not int portValue || portValue <= 0 || portValue > 65535)
        {
            return "Друг сейчас в игре, но мир ещё не открыт для подключения.";
        }

        if (canConnect)
        {
            return $"Подключиться к {joinHost.Trim()}:{portValue}";
        }

        var normalizedHost = joinHost.Trim();
        if (IPAddress.TryParse(normalizedHost, out var parsedAddress) && IsPrivateLanAddress(parsedAddress))
        {
            return HasRelayEndpoint(relayRoomId, relayTransportMode)
                ? "У друга мир открыт только локально. Лаунчер попробует подключиться через Vesper Relay."
                : "У друга мир открыт только в локальной сети. Для подключения через интернет ему нужен UPnP или ручное открытие порта на роутере.";
        }

        return "Лаунчер пока не смог подтвердить внешний адрес мира друга.";
    }

    private static bool IsReachablePrivateLanAddress(IPAddress targetAddress)
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicastAddress in adapter.GetIPProperties().UnicastAddresses)
                {
                    var localAddress = unicastAddress.Address;
                    if (localAddress.AddressFamily != AddressFamily.InterNetwork ||
                        !IsPrivateLanAddress(localAddress))
                    {
                        continue;
                    }

                    var mask = unicastAddress.IPv4Mask;
                    if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (AreAddressesOnSameIpv4Subnet(localAddress, targetAddress, mask))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool AreAddressesOnSameIpv4Subnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (leftBytes.Length != 4 || rightBytes.Length != 4 || maskBytes.Length != 4)
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if ((leftBytes[index] & maskBytes[index]) != (rightBytes[index] & maskBytes[index]))
            {
                return false;
            }
        }

        return true;
    }

    private CloudFriendListItem CreateFriendListItem(
        string username,
        CloudAvatarPayload? avatar,
        bool isOnline,
        string? lastSeenAtUtc,
        string? activityKind,
        string? activityName,
        string? versionId,
        string? joinHost,
        int? joinPort,
        string? relayRoomId,
        string? relayTransportMode,
        bool isJoinable)
    {
        var normalizedUsername = NormalizeMinecraftUsername(username);
        var hasDirectJoinEndpoint = isJoinable &&
                                    !string.IsNullOrWhiteSpace(joinHost) &&
                                    joinPort is int validPort &&
                                    validPort > 0 &&
                                    validPort <= 65535;
        var hasRelayEndpoint = isJoinable && HasRelayEndpoint(relayRoomId, relayTransportMode);
        var hasJoinEndpoint = hasDirectJoinEndpoint || hasRelayEndpoint;
        var showConnectButton = isOnline && hasJoinEndpoint;
        var canConnect = CanReachJoinHost(joinHost, joinPort, isJoinable) || hasRelayEndpoint;
        return new CloudFriendListItem(
            normalizedUsername,
            TryLoadCloudAvatarBitmap(normalizedUsername, avatar, decodePixelWidth: 88),
            TryResolveCloudAvatarFilePath(normalizedUsername, avatar),
            BuildAvatarPlaceholderText(normalizedUsername),
            isOnline,
            BuildPresenceText(isOnline, lastSeenAtUtc),
            BuildFriendActivityText(activityKind, activityName),
            BuildFriendVersionText(versionId),
            BuildJoinAddressText(joinHost, joinPort, relayRoomId, relayTransportMode, isJoinable, canConnect),
            string.IsNullOrWhiteSpace(joinHost) ? null : joinHost.Trim(),
            joinPort,
            string.IsNullOrWhiteSpace(relayRoomId) ? null : relayRoomId.Trim(),
            string.IsNullOrWhiteSpace(relayTransportMode) ? null : relayTransportMode.Trim(),
            hasJoinEndpoint,
            showConnectButton,
            canConnect,
            BuildConnectTooltip(isOnline, joinHost, joinPort, relayRoomId, relayTransportMode, isJoinable, canConnect),
            string.IsNullOrWhiteSpace(versionId) ? null : versionId.Trim());
    }

    private CloudIncomingFriendRequestItem CreateIncomingRequestItem(
        long requestId,
        string username,
        string? createdAtUtc,
        CloudAvatarPayload? avatar)
    {
        var normalizedUsername = NormalizeMinecraftUsername(username);
        return new CloudIncomingFriendRequestItem(
            requestId,
            normalizedUsername,
            createdAtUtc,
            TryLoadCloudAvatarBitmap(normalizedUsername, avatar, decodePixelWidth: 72),
            TryResolveCloudAvatarFilePath(normalizedUsername, avatar),
            BuildAvatarPlaceholderText(normalizedUsername),
            BuildRequestSubtitleText(createdAtUtc));
    }

    private void RebuildFriendEntriesFromSavedFriends()
    {
        _friendEntries.Clear();
        _friendEntries.AddRange(_friends.Select(friend => CreateFriendListItem(
            friend,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false)));
    }

    private void UpdateFriendNotificationsBadge()
    {
        if (FriendNotificationsBadgeBorder is null || FriendNotificationsBadgeTextBlock is null)
        {
            return;
        }

        var pendingCount = _incomingFriendRequests.Count;
        FriendNotificationsBadgeBorder.Visibility = pendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        FriendNotificationsBadgeTextBlock.Text = pendingCount > 99
            ? "99+"
            : pendingCount.ToString();
    }

    private static string BuildAvatarFileName(string username) =>
        $"{Regex.Replace(username.ToLowerInvariant(), @"[^a-z0-9_\-]+", "_")}-avatar.png";

    private string SaveCloudAvatarBytesToCache(string username, CloudAvatarPayload avatar, byte[] avatarBytes)
    {
        var cacheFileName = BuildCloudAvatarCacheFileName(username, avatar);
        var cacheDirectory = GetCloudAvatarCacheDirectory();
        var searchPattern = $"{Regex.Replace(username.ToLowerInvariant(), @"[^a-z0-9_\-]+", "_")}-cloud-avatar-*";

        foreach (var oldAvatarPath in Directory.EnumerateFiles(cacheDirectory, searchPattern))
        {
            if (string.Equals(oldAvatarPath, Path.Combine(cacheDirectory, cacheFileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(oldAvatarPath);
        }

        var targetPath = Path.Combine(cacheDirectory, cacheFileName);
        File.WriteAllBytes(targetPath, avatarBytes);
        return targetPath;
    }

    private static async Task<byte[]?> DownloadCloudAvatarBytesAsync(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        try
        {
            using var response = await CloudAssetHttp.GetAsync(imageUrl);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var avatarBytes = await response.Content.ReadAsByteArrayAsync();
            return avatarBytes.Length > 0
                ? avatarBytes
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> GetCloudAvatarBytesAsync(CloudAvatarPayload? avatar)
    {
        if (!string.IsNullOrWhiteSpace(avatar?.ImageBase64))
        {
            try
            {
                return Convert.FromBase64String(avatar.ImageBase64);
            }
            catch
            {
                return null;
            }
        }

        return await DownloadCloudAvatarBytesAsync(avatar?.ImageUrl);
    }

    private async Task PreloadCloudAvatarCachesAsync(IEnumerable<(string Username, CloudAvatarPayload? Avatar)> entries)
    {
        foreach (var (username, avatar) in entries)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                avatar is null ||
                string.IsNullOrWhiteSpace(avatar.ImageUrl) ||
                !string.IsNullOrWhiteSpace(avatar.ImageBase64) ||
                !string.IsNullOrWhiteSpace(TryResolveCachedCloudAvatarPath(username, avatar)))
            {
                continue;
            }

            var avatarBytes = await GetCloudAvatarBytesAsync(avatar);
            if (avatarBytes is null || avatarBytes.Length == 0)
            {
                continue;
            }

            try
            {
                SaveCloudAvatarBytesToCache(username, avatar, avatarBytes);
            }
            catch (Exception ex)
            {
                TryWriteErrorToLog(ex, "Ошибка кеширования облачного аватара");
            }
        }
    }

    private static byte[] ConvertImageFileToPngBytes(string sourcePath, int targetSize)
    {
        var requestedSize = Math.Max(64, targetSize);
        var bitmap = LoadBitmapFromFile(sourcePath, decodePixelWidth: Math.Max(requestedSize, 256));

        foreach (var size in new[] { requestedSize, 192, 160, 128, 96 })
        {
            if (size > requestedSize)
            {
                continue;
            }

            var squareBitmap = CreateSquareIconBitmap(bitmap, size);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(squareBitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            var bytes = stream.ToArray();
            if (bytes.Length <= MaxCloudAvatarBytes || size == 96)
            {
                return bytes;
            }
        }

        throw new InvalidOperationException("Не удалось подготовить аватар подходящего размера.");
    }

    private string SaveAvatarBytesToLocalProfile(string username, byte[] avatarBytes)
    {
        var avatarFileName = BuildAvatarFileName(username);
        var avatarsDirectory = GetAccountAvatarsDirectory();
        var searchPattern = $"{Regex.Replace(username.ToLowerInvariant(), @"[^a-z0-9_\-]+", "_")}-avatar.*";

        foreach (var oldAvatarPath in Directory.EnumerateFiles(avatarsDirectory, searchPattern))
        {
            if (string.Equals(oldAvatarPath, Path.Combine(avatarsDirectory, avatarFileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(oldAvatarPath);
        }

        try
        {
            File.WriteAllBytes(Path.Combine(avatarsDirectory, avatarFileName), avatarBytes);
            return avatarFileName;
        }
        catch when (!string.Equals(
                         Path.GetFullPath(avatarsDirectory).TrimEnd(Path.DirectorySeparatorChar),
                         Path.GetFullPath(GetInstallLocalAccountAvatarsDirectory()).TrimEnd(Path.DirectorySeparatorChar),
                         StringComparison.OrdinalIgnoreCase))
        {
            var fallbackDirectory = GetInstallLocalAccountAvatarsDirectory();
            foreach (var oldAvatarPath in Directory.EnumerateFiles(fallbackDirectory, searchPattern))
            {
                if (string.Equals(oldAvatarPath, Path.Combine(fallbackDirectory, avatarFileName), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteFile(oldAvatarPath);
            }

            File.WriteAllBytes(Path.Combine(fallbackDirectory, avatarFileName), avatarBytes);
            return avatarFileName;
        }
    }

    private void UpdateAccountAvatarFileName(string? avatarFileName)
    {
        if (_accountState is null)
        {
            return;
        }

        _accountState = new LauncherAccountState
        {
            Username = _accountState.Username,
            PasswordHash = _accountState.PasswordHash,
            PasswordSalt = _accountState.PasswordSalt,
            PasswordAlgorithm = _accountState.PasswordAlgorithm,
            PasswordIterations = _accountState.PasswordIterations,
            CreatedAtUtc = _accountState.CreatedAtUtc,
            CloudSyncedAtUtc = _accountState.CloudSyncedAtUtc,
            AvatarFileName = avatarFileName,
            AccessToken = _accountState.AccessToken,
            AccessTokenExpiresAtUtc = _accountState.AccessTokenExpiresAtUtc,
            LastAuthenticatedAtUtc = _accountState.LastAuthenticatedAtUtc
        };

        SaveAccountState();
    }

    private async Task<CloudFriendOperationResult> UploadAvatarToCloudAsync(byte[] avatarBytes)
    {
        try
        {
            var requestUrl = ResolveProfileAvatarUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Post, requestUrl, out var request, out _, out var errorMessage))
            {
                return new CloudFriendOperationResult(false, errorMessage);
            }

            using (request)
            {
                request!.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        contentType = "image/png",
                        imageBase64 = Convert.ToBase64String(avatarBytes)
                    }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await AccountSyncHttp.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                    {
                        return new CloudFriendOperationResult(false, "Сессия аккаунта истекла. Войди снова.");
                    }

                    var fallbackMessage = $"Не удалось сохранить аватар в облаке. HTTP {(int)response.StatusCode}.";
                    return new CloudFriendOperationResult(false, ExtractCloudErrorMessage(responseBody, fallbackMessage));
                }
            }

            return new CloudFriendOperationResult(true, null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка синхронизации аватара с облаком");
            return new CloudFriendOperationResult(false, "Не удалось сохранить аватар в облаке.");
        }
    }

    private async Task TrySyncCloudProfileAsync(bool uploadLocalAvatarIfMissing = true)
    {
        try
        {
            var profileUrl = ResolveMeUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Get, profileUrl, out var request, out _, out _))
            {
                return;
            }

            using (request)
            {
                using var response = await AccountSyncHttp.SendAsync(request!);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                    {
                        return;
                    }

                    return;
                }

                var profileResponse = JsonSerializer.Deserialize<CloudAuthResponse>(responseBody, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var cloudAvatar = profileResponse?.Avatar;
                var avatarBytes = await GetCloudAvatarBytesAsync(cloudAvatar);
                if (avatarBytes is not null && avatarBytes.Length > 0)
                {
                    var avatarFileName = SaveAvatarBytesToLocalProfile(_accountState!.Username, avatarBytes);
                    if (!string.Equals(_accountState?.AvatarFileName, avatarFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateAccountAvatarFileName(avatarFileName);
                    }

                    RefreshAccountAvatarPreview();
                    RefreshFriendsProfileAvatarPreview();
                    return;
                }

                if (!uploadLocalAvatarIfMissing)
                {
                    return;
                }

                var localAvatarPath = ResolveAccountAvatarPath(_accountState);
                if (string.IsNullOrWhiteSpace(localAvatarPath) || !File.Exists(localAvatarPath))
                {
                    return;
                }

                var avatarBytesToUpload = ConvertImageFileToPngBytes(localAvatarPath, 256);
                await UploadAvatarToCloudAsync(avatarBytesToUpload);
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка синхронизации облачного профиля");
        }
    }

    private static (string Hash, string Salt) CreatePasswordHash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            saltBytes,
            PasswordHashIterations,
            HashAlgorithmName.SHA256,
            32);
        return (Convert.ToHexString(hashBytes), Convert.ToHexString(saltBytes));
    }

    private static string CreatePasswordHash(string password, string saltHex, int iterations)
    {
        var saltBytes = Convert.FromHexString(saltHex);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            saltBytes,
            iterations,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToHexString(hashBytes);
    }

    private static string? BuildAuthEndpointUrl(string? configuredUrl, string? registerUrl, string endpointSuffix)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl.Trim();
        }

        if (string.IsNullOrWhiteSpace(registerUrl))
        {
            return null;
        }

        const string registerSuffix = "/api/v1/auth/register";
        var normalizedRegisterUrl = registerUrl.Trim();
        if (!normalizedRegisterUrl.EndsWith(registerSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalizedRegisterUrl[..^registerSuffix.Length] + endpointSuffix;
    }

    private static string? ResolveRegisterUrl(AccountSyncConfig? config) =>
        string.IsNullOrWhiteSpace(config?.RegisterUrl) ? null : config.RegisterUrl.Trim();

    private static string? ResolveLoginUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.LoginUrl, config?.RegisterUrl, "/api/v1/auth/login");

    private static string? ResolveCredentialInfoUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.CredentialInfoUrl, config?.RegisterUrl, "/api/v1/auth/credential-info");

    private static string? ResolveMeUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.MeUrl, config?.RegisterUrl, "/api/v1/auth/me");

    private static string? ResolveProfileAvatarUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.ProfileAvatarUrl, config?.RegisterUrl, "/api/v1/profile/avatar");

    private static string? ResolvePresencePingUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.PresencePingUrl, config?.RegisterUrl, "/api/v1/presence/ping");

    private static string? ResolveFriendsUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.FriendsUrl, config?.RegisterUrl, "/api/v1/friends");

    private static string? ResolveFriendRequestUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.FriendRequestUrl, config?.RegisterUrl, "/api/v1/friends/request");

    private static string? ResolveFriendRespondUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.FriendRespondUrl, config?.RegisterUrl, "/api/v1/friends/respond");

    private static string? ResolveFriendRemoveUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.FriendRemoveUrl, config?.RegisterUrl, "/api/v1/friends/remove");

    private static string? ResolveLogoutUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(config?.LogoutUrl, config?.RegisterUrl, "/api/v1/auth/logout");

    private static string? ResolveRelayEnsureSessionUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(null, config?.RegisterUrl, "/api/v1/relay/session/ensure");

    private static string? ResolveRelayCloseSessionUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(null, config?.RegisterUrl, "/api/v1/relay/session/close");

    private static string? ResolveRelayConnectUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(null, config?.RegisterUrl, "/api/v1/relay/connect");

    private static string? ResolveRelayHostPendingUrl(AccountSyncConfig? config) =>
        BuildAuthEndpointUrl(null, config?.RegisterUrl, "/api/v1/relay/host/pending");

    private static void ApplyConfiguredHeaders(
        HttpRequestMessage request,
        AccountSyncConfig config,
        bool allowAuthorizationOverride = true)
    {
        if (string.IsNullOrWhiteSpace(config.AuthorizationHeaderValue))
        {
            return;
        }

        var headerName = string.IsNullOrWhiteSpace(config.AuthorizationHeaderName)
            ? "Authorization"
            : config.AuthorizationHeaderName!;
        if (!allowAuthorizationOverride &&
            string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, config.AuthorizationHeaderValue);
    }

    private static string ExtractCloudErrorMessage(string? responseBody, string fallbackMessage)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return fallbackMessage;
        }

        try
        {
            var response = JsonSerializer.Deserialize<CloudAuthResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (response is { Details: { } internalDetails } &&
                !string.IsNullOrWhiteSpace(internalDetails) &&
                string.Equals(response.Error, "Internal server error", StringComparison.OrdinalIgnoreCase))
            {
                return LocalizeCloudErrorMessage(internalDetails, fallbackMessage);
            }

            if (response is { Error: { } error } && !string.IsNullOrWhiteSpace(error))
            {
                return LocalizeCloudErrorMessage(error, fallbackMessage);
            }

            if (response is { Details: { } details } && !string.IsNullOrWhiteSpace(details))
            {
                return LocalizeCloudErrorMessage(details, fallbackMessage);
            }
        }
        catch
        {
            // ignore malformed JSON and fall back to raw response text
        }

        return LocalizeCloudErrorMessage(responseBody, fallbackMessage);
    }

    private static string LocalizeCloudErrorMessage(string? rawMessage, string fallbackMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return fallbackMessage;
        }

        var normalizedMessage = rawMessage.Trim();
        if ((normalizedMessage.Contains("GitHub read failed for nicknames/by-name/", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.Contains("GitHub read failed for nicknames/by-id/", StringComparison.OrdinalIgnoreCase)) &&
            (normalizedMessage.EndsWith(": 403", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.EndsWith(": 404", StringComparison.OrdinalIgnoreCase)))
        {
            return "Аккаунт не найден.";
        }

        if ((normalizedMessage.Contains("GitHub read failed for avatars/by-username/", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.Contains("GitHub write failed for avatars/by-username/", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.Contains("GitHub delete failed for avatars/by-username/", StringComparison.OrdinalIgnoreCase)) &&
            (normalizedMessage.EndsWith(": 401", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.EndsWith(": 403", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.EndsWith(": 404", StringComparison.OrdinalIgnoreCase) ||
             normalizedMessage.EndsWith(": 422", StringComparison.OrdinalIgnoreCase)))
        {
            return "Не удалось сохранить аватар в GitHub-облаке. Проверь GitHub token и доступ к приватному репозиторию.";
        }

        return normalizedMessage switch
        {
            "Username already exists" => "Такой аккаунт уже существует. Попробуй войти.",
            "Invalid credentials" => "Неверный ник или пароль.",
            "Invalid username" => "Некорректный ник аккаунта.",
            "Account not found" => "Аккаунт не найден.",
            "Avatar image is required" => "Нужно выбрать изображение для аватара.",
            "Unsupported avatar format" => "Формат аватара не поддерживается.",
            "Invalid avatar payload" => "Не удалось обработать изображение аватара.",
            "Avatar is too large" => "Аватар слишком большой. Выбери изображение поменьше.",
            "Already friends" => "Вы уже в друзьях.",
            "Friend request already sent" => "Заявка этому игроку уже отправлена.",
            "Incoming friend request already exists" => "У тебя уже есть входящая заявка от этого игрока.",
            "Cannot add yourself as a friend" => "Нельзя добавить самого себя в друзья.",
            "Friend request not found" => "Заявка в друзья не найдена.",
            "Friend not found" => "Друг не найден.",
            "Cannot remove yourself" => "Нельзя удалить самого себя.",
            "Invalid friend request action" => "Некорректное действие для заявки в друзья.",
            "Unsupported password algorithm" => "Сервер вернул неподдерживаемый формат пароля.",
            "Password verification must be done client-side for this account" => "Обнови лаунчер, чтобы войти в этот аккаунт.",
            "Missing bearer token" => "Не удалось подтвердить сессию аккаунта.",
            "Invalid token" => "Сессия аккаунта больше не действительна.",
            "Token expired" => "Сессия аккаунта истекла. Войди заново.",
            _ => normalizedMessage
        };
    }

    private LauncherAccountState BuildAuthenticatedAccountState(
        string username,
        string password,
        string? accessToken,
        string? accessTokenExpiresAtUtc,
        LauncherAccountState? previousState = null)
    {
        var (passwordHash, passwordSalt) = CreatePasswordHash(password);
        var now = DateTimeOffset.UtcNow.ToString("O");
        return new LauncherAccountState
        {
            Username = username,
            PasswordHash = passwordHash,
            PasswordSalt = passwordSalt,
            PasswordAlgorithm = "PBKDF2-SHA256",
            PasswordIterations = PasswordHashIterations,
            CreatedAtUtc = previousState?.CreatedAtUtc ?? now,
            CloudSyncedAtUtc = now,
            AvatarFileName = previousState?.AvatarFileName,
            AccessToken = accessToken,
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
            LastAuthenticatedAtUtc = now
        };
    }

    private async Task<CloudAuthRequestResult> RegisterCloudAccountAsync(string username, string password)
    {
        try
        {
            var config = LoadAccountSyncConfig();
            var registerUrl = ResolveRegisterUrl(config);
            if (string.IsNullOrWhiteSpace(registerUrl) || config is null)
            {
                return new CloudAuthRequestResult(false, "Не настроен адрес регистрации аккаунта.", null, null, null);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, registerUrl);
            ApplyConfiguredHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var (passwordHash, passwordSalt) = CreatePasswordHash(password);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    username,
                    passwordHash,
                    passwordSalt,
                    passwordAlgorithm = "PBKDF2-SHA256",
                    passwordIterations = PasswordHashIterations
                }),
                Encoding.UTF8,
                "application/json");

            using var response = await AccountSyncHttp.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var fallbackMessage = response.StatusCode == HttpStatusCode.Conflict
                    ? "Такой аккаунт уже существует. Попробуй войти."
                    : $"Не удалось зарегистрировать аккаунт. HTTP {(int)response.StatusCode}.";
                return new CloudAuthRequestResult(
                    false,
                    ExtractCloudErrorMessage(responseBody, fallbackMessage),
                    null,
                    null,
                    null);
            }

            var authResponse = JsonSerializer.Deserialize<CloudAuthResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            TryWriteLauncherDiagnosticLog($"Account login succeeded for '{username}'.");
            return new CloudAuthRequestResult(
                true,
                null,
                authResponse?.AccessToken,
                authResponse?.ExpiresAtUtc,
                authResponse?.User?.Username ?? username);
        }
        catch (TaskCanceledException ex)
        {
            TryWriteErrorToLog(ex, "Ошибка регистрации аккаунта");
            return new CloudAuthRequestResult(
                false,
                "Сервер регистрации отвечает слишком долго. Проверь интернет или попробуй ещё раз чуть позже.",
                null,
                null,
                null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка регистрации аккаунта");
            return new CloudAuthRequestResult(false, "Не удалось зарегистрировать аккаунт в облаке.", null, null, null);
        }
    }

    private async Task<CloudAuthResponse?> TryGetCloudCredentialInfoAsync(string username, AccountSyncConfig config)
    {
        var credentialInfoUrl = ResolveCredentialInfoUrl(config);
        if (string.IsNullOrWhiteSpace(credentialInfoUrl))
        {
            return null;
        }

        try
        {
            var requestUrl = $"{credentialInfoUrl}?username={Uri.EscapeDataString(username)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            ApplyConfiguredHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await AccountSyncHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<CloudAuthResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки параметров входа аккаунта");
            return null;
        }
    }

    private async Task<IncognitoNicknameCheckResult> CheckIncognitoNicknameAvailabilityAsync(string username)
    {
        try
        {
            var config = LoadAccountSyncConfig();
            var credentialInfoUrl = ResolveCredentialInfoUrl(config);
            if (string.IsNullOrWhiteSpace(credentialInfoUrl) || config is null)
            {
                return new IncognitoNicknameCheckResult(
                    false,
                    false,
                    "Не удалось проверить ник инкогнито: не настроен адрес Vesper API.",
                    null);
            }

            var requestUrl = $"{credentialInfoUrl}?username={Uri.EscapeDataString(username)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            ApplyConfiguredHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await AccountSyncHttp.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new IncognitoNicknameCheckResult(true, true, null, null);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return new IncognitoNicknameCheckResult(false, false, "Такой ник не подходит для Vesper.", null);
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new IncognitoNicknameCheckResult(
                    false,
                    false,
                    $"Не удалось проверить ник. HTTP {(int)response.StatusCode}.",
                    null);
            }

            var authResponse = JsonSerializer.Deserialize<CloudAuthResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var existingUsername = string.IsNullOrWhiteSpace(authResponse?.User?.Username)
                ? username
                : NormalizeMinecraftUsername(authResponse!.User!.Username!);
            return new IncognitoNicknameCheckResult(true, false, null, existingUsername);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка проверки ника инкогнито");
            return new IncognitoNicknameCheckResult(
                false,
                false,
                "Не удалось проверить, свободен ли этот ник в Vesper.",
                null);
        }
    }

    private async Task<CloudAuthRequestResult> LoginCloudAccountAsync(string username, string password)
    {
        try
        {
            var config = LoadAccountSyncConfig();
            var loginUrl = ResolveLoginUrl(config);
            if (string.IsNullOrWhiteSpace(loginUrl) || config is null)
            {
                return new CloudAuthRequestResult(false, "Не настроен адрес входа в аккаунт.", null, null, null);
            }

            TryWriteLauncherDiagnosticLog($"Account login started for '{username}' via {loginUrl}.");
            using var request = new HttpRequestMessage(HttpMethod.Post, loginUrl);
            ApplyConfiguredHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            object payload = new
            {
                username,
                password
            };
            var credentialInfo = await TryGetCloudCredentialInfoAsync(username, config);
            if (credentialInfo?.Ok == true &&
                string.Equals(credentialInfo.PasswordAlgorithm, "PBKDF2-SHA256", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(credentialInfo.PasswordSalt) &&
                credentialInfo.PasswordIterations is > 0)
            {
                try
                {
                    payload = new
                    {
                        username,
                        passwordHash = CreatePasswordHash(
                            password,
                            credentialInfo.PasswordSalt!,
                            credentialInfo.PasswordIterations.Value)
                    };
                }
                catch (Exception ex)
                {
                    TryWriteErrorToLog(ex, "Ошибка подготовки локального хеша для входа");
                }
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await AccountSyncHttp.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                TryWriteLauncherDiagnosticLog($"Account login failed for '{username}'. HTTP {(int)response.StatusCode}: {responseBody}");
                var fallbackMessage = response.StatusCode == HttpStatusCode.Unauthorized
                    ? "Неверный ник или пароль."
                    : $"Не удалось войти в аккаунт. HTTP {(int)response.StatusCode}.";
                return new CloudAuthRequestResult(
                    false,
                    ExtractCloudErrorMessage(responseBody, fallbackMessage),
                    null,
                    null,
                    null);
            }

            var authResponse = JsonSerializer.Deserialize<CloudAuthResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return new CloudAuthRequestResult(
                true,
                null,
                authResponse?.AccessToken,
                authResponse?.ExpiresAtUtc,
                authResponse?.User?.Username ?? username);
        }
        catch (TaskCanceledException ex)
        {
            TryWriteErrorToLog(ex, "Ошибка входа в аккаунт");
            return new CloudAuthRequestResult(
                false,
                "Сервер входа отвечает слишком долго. Проверь интернет или попробуй ещё раз чуть позже.",
                null,
                null,
                null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка входа в аккаунт");
            return new CloudAuthRequestResult(false, "Не удалось выполнить вход в аккаунт.", null, null, null);
        }
    }

    private async Task TryLogoutCloudSessionAsync(LauncherAccountState? accountState)
    {
        if (accountState is null || string.IsNullOrWhiteSpace(accountState.AccessToken))
        {
            return;
        }

        try
        {
            var config = LoadAccountSyncConfig();
            var logoutUrl = ResolveLogoutUrl(config);
            if (string.IsNullOrWhiteSpace(logoutUrl) || config is null)
            {
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, logoutUrl);
            ApplyConfiguredHeaders(request, config, allowAuthorizationOverride: false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountState.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var _ = await AccountSyncHttp.SendAsync(request);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка выхода из облачной сессии");
        }
    }

    private bool TryCreateAuthorizedCloudRequest(
        HttpMethod method,
        string? url,
        out HttpRequestMessage? request,
        out AccountSyncConfig? config,
        out string? errorMessage)
    {
        request = null;
        config = null;
        errorMessage = null;

        if (!HasAuthenticatedCloudSession())
        {
            errorMessage = "Сначала войди в аккаунт.";
            return false;
        }

        config = LoadAccountSyncConfig();
        if (config is null || string.IsNullOrWhiteSpace(url))
        {
            errorMessage = "Не настроен облачный API друзей.";
            return false;
        }

        request = new HttpRequestMessage(method, url);
        ApplyConfiguredHeaders(request, config, allowAuthorizationOverride: false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accountState!.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return true;
    }

    private void ApplyCloudFriendsSnapshot(
        IEnumerable<CloudFriendListItem> friendEntries,
        IEnumerable<CloudIncomingFriendRequestItem> incomingRequests,
        IEnumerable<string> outgoingRequests)
    {
        _friends.Clear();
        _friendEntries.Clear();

        var knownFriends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var friendEntry in friendEntries)
        {
            if (string.IsNullOrWhiteSpace(friendEntry.Username))
            {
                continue;
            }

            var normalizedFriend = NormalizeMinecraftUsername(friendEntry.Username);
            if (!UsernameRegex.IsMatch(normalizedFriend) || !knownFriends.Add(normalizedFriend))
            {
                continue;
            }

            _friends.Add(normalizedFriend);
            _friendEntries.Add(friendEntry with
            {
                Username = normalizedFriend,
                AvatarPlaceholder = BuildAvatarPlaceholderText(normalizedFriend)
            });

            if (_friends.Count >= MaxFriends)
            {
                break;
            }
        }

        _incomingFriendRequests.Clear();
        _incomingFriendRequests.AddRange(incomingRequests
            .Where(request => !string.IsNullOrWhiteSpace(request.Username))
            .GroupBy(request => request.Username, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxFriends));

        _outgoingFriendRequests.Clear();
        _outgoingFriendRequests.AddRange(outgoingRequests
            .Where(friend => !string.IsNullOrWhiteSpace(friend))
            .Select(NormalizeMinecraftUsername)
            .Where(friend => UsernameRegex.IsMatch(friend))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxFriends));

        SaveFriendsToDisk();
    }

    private void ClearCloudFriendsSnapshot()
    {
        _friends.Clear();
        _friendEntries.Clear();
        _incomingFriendRequests.Clear();
        _outgoingFriendRequests.Clear();
    }

    private void RefreshIncomingFriendRequestsList()
    {
        _isRefreshingIncomingFriendRequestsList = true;
        try
        {
            IncomingFriendRequestsListBox.ItemsSource = null;
            IncomingFriendRequestsListBox.ItemsSource = _incomingFriendRequests.ToList();
            IncomingFriendRequestsListBox.SelectedItem = null;
            AcceptFriendRequestButton.IsEnabled = false;
            DeclineFriendRequestButton.IsEnabled = false;
            UpdateFriendNotificationsBadge();
            RefreshFriendNotificationsPopup();
        }
        finally
        {
            _isRefreshingIncomingFriendRequestsList = false;
        }

        UpdateFriendsSectionResponsiveLayout();
    }

    private void UpdateFriendsCloudStatusText(string? customText = null)
    {
        if (FriendsCloudStatusTextBlock is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(customText))
        {
            FriendsCloudStatusTextBlock.Text = customText;
            return;
        }

        FriendsCloudStatusTextBlock.Text =
            $"Друзей: {_friends.Count} · входящих: {_incomingFriendRequests.Count} · исходящих: {_outgoingFriendRequests.Count}";
    }

    private void UpdateVesperNetStatusText(string? customText = null)
    {
        if (VesperNetStatusTextBlock is null)
        {
            return;
        }

        VesperNetStatusTextBlock.Text = string.IsNullOrWhiteSpace(customText)
            ? _vesperNetDiagnosticState.StatusText
            : customText;
    }

    private async Task RefreshVesperNetStatusAsync(bool logErrors = false)
    {
        if (!PlatformService.Features.SupportsVesperNetService)
        {
            _vesperNetDiagnosticState = new VesperNetDiagnosticState(
                false,
                false,
                false,
                null,
                null,
                "VesperNet: недоступен на этой платформе. Используется обычное подключение.");
            UpdateVesperNetStatusText();
            return;
        }

        try
        {
            var isInstalled = IsVesperNetServiceInstalled();
            if (!isInstalled)
            {
                _vesperNetDiagnosticState = new VesperNetDiagnosticState(
                    false,
                    false,
                    false,
                    null,
                    null,
                    "VesperNet: служба не установлена. Пока используется обычное прямое подключение.");
                UpdateVesperNetStatusText();
                return;
            }

            var health = await QueryVesperNetHealthAsync();
            if (health is null || !health.Ok)
            {
                _vesperNetDiagnosticState = new VesperNetDiagnosticState(
                    true,
                    false,
                    false,
                    null,
                    null,
                    "VesperNet: служба установлена, но не отвечает. Оверлей-сеть ещё не активна.");
                UpdateVesperNetStatusText();
                return;
            }

            var versionSuffix = string.IsNullOrWhiteSpace(health.Version)
                ? string.Empty
                : $" · v{health.Version}";
            var transportSuffix = string.IsNullOrWhiteSpace(health.TransportMode)
                ? string.Empty
                : $" · режим: {health.TransportMode}";
            var virtualIpSuffix = string.IsNullOrWhiteSpace(health.VirtualIp)
                ? string.Empty
                : $" · IP: {health.VirtualIp}";
            var statusText = health.OverlayConnected
                ? $"VesperNet: сеть активна{transportSuffix}{virtualIpSuffix}{versionSuffix}"
                : $"VesperNet: служба отвечает, но overlay ещё не поднят{versionSuffix}";

            _vesperNetDiagnosticState = new VesperNetDiagnosticState(
                true,
                true,
                health.OverlayConnected,
                health.VirtualIp,
                health.TransportMode,
                statusText);
            UpdateVesperNetStatusText();
        }
        catch (Exception ex)
        {
            if (logErrors)
            {
                TryWriteErrorToLog(ex, "Ошибка диагностики VesperNet");
            }

            _vesperNetDiagnosticState = new VesperNetDiagnosticState(
                IsVesperNetServiceInstalled(),
                false,
                false,
                null,
                null,
                "VesperNet: не удалось получить состояние службы.");
            UpdateVesperNetStatusText();
        }
    }

    private static bool IsVesperNetServiceInstalled()
    {
        if (!PlatformService.Features.SupportsVesperNetService)
        {
            return false;
        }

        try
        {
            using var serviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{VesperNetServiceName}");
            return serviceKey is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<VesperNetHealthResponse?> QueryVesperNetHealthAsync()
    {
        try
        {
            using var response = await AccountSyncHttp.GetAsync(VesperNetHealthUrl);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            return JsonSerializer.Deserialize<VesperNetHealthResponse>(responseBody);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<VesperNetOverlayConnectResponse?> PostVesperNetOverlayRequestAsync(
        string requestUrl,
        object payload)
    {
        try
        {
            using var response = await VesperNetControlHttp.PostAsync(
                requestUrl,
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            return JsonSerializer.Deserialize<VesperNetOverlayConnectResponse>(responseBody);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> ClearVesperNetOverlayAsync(bool resetToHostIp = true)
    {
        try
        {
            using var response = await VesperNetControlHttp.PostAsync(
                VesperNetOverlayClearUrl,
                new StringContent(
                    JsonSerializer.Serialize(new { resetToHostIp }),
                    Encoding.UTF8,
                    "application/json"));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanUseVesperNetOverlayLocallyAsync()
    {
        if (!PlatformService.Features.SupportsVesperNetService)
        {
            return false;
        }

        var health = await QueryVesperNetHealthAsync();
        return health is not null &&
               health.Ok &&
               health.AdapterInstalled;
    }

    private static async Task<VesperNetOverlayConnectResponse?> AttachVesperNetHostPeerAsync(
        string accessToken,
        string webSocketUrl,
        string connectionId)
    {
        return await PostVesperNetOverlayRequestAsync(
            VesperNetOverlayHostAttachUrl,
            new
            {
                accessToken,
                webSocketUrl,
                connectionId
            });
    }

    private static async Task<VesperNetOverlayConnectResponse?> ConnectVesperNetGuestPeerAsync(
        string accessToken,
        string webSocketUrl,
        string connectionId)
    {
        return await PostVesperNetOverlayRequestAsync(
            VesperNetOverlayGuestConnectUrl,
            new
            {
                accessToken,
                webSocketUrl,
                connectionId
            });
    }

    private string? GetCurrentCloudAccessToken()
    {
        return HasAuthenticatedCloudSession() ? _accountState?.AccessToken?.Trim() : null;
    }

    private async Task<VesperRelaySessionInfo?> EnsureActiveRelaySessionAsync(int localLanPort)
    {
        if (localLanPort <= 0 || localLanPort > 65535)
        {
            return null;
        }

        var accessToken = GetCurrentCloudAccessToken();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var config = LoadAccountSyncConfig();
        var requestUrl = ResolveRelayEnsureSessionUrl(config);
        if (string.IsNullOrWhiteSpace(requestUrl))
        {
            return null;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (_activeRelaySession is not null &&
            _activeRelayLanPort == localLanPort &&
            nowUtc < _activeRelaySessionExpiresAtUtc)
        {
            TryWriteLauncherDiagnosticLog($"VesperNet: reuse active relay session room={_activeRelaySession.RoomId}, mode={_activeRelaySession.TransportMode}, port={localLanPort}");
            return _activeRelaySession;
        }

        try
        {
            var relaySession = await VesperFriendRelay.EnsureHostSessionAsync(
                AccountSyncHttp,
                requestUrl,
                accessToken).ConfigureAwait(false);
            if (relaySession is null)
            {
                return null;
            }

            var advertisedTransportMode = relaySession.TransportMode;

            _activeRelaySession = new VesperRelaySessionInfo(relaySession.RoomId, advertisedTransportMode);
            _activeRelayLanPort = localLanPort;
            _activeRelaySessionExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20);
            TryWriteLauncherDiagnosticLog(
                $"VesperNet: host session ready room={relaySession.RoomId}, advertisedMode={advertisedTransportMode}, lanPort={localLanPort}");
            return _activeRelaySession;
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка активации Vesper Relay");
            return null;
        }
    }

    private async Task CloseActiveRelaySessionAsync()
    {
        var accessToken = GetCurrentCloudAccessToken();
        var config = LoadAccountSyncConfig();
        var requestUrl = ResolveRelayCloseSessionUrl(config);

        _activeRelaySession = null;
        _activeRelayLanPort = null;
        _activeRelaySessionExpiresAtUtc = DateTimeOffset.MinValue;
        _lastDetectedLanPort = null;
        _lastDetectedLanPortAtUtc = DateTimeOffset.MinValue;

        lock (_activeRelayHostConnections)
        {
            _activeRelayHostConnections.Clear();
        }

        await ClearVesperNetOverlayAsync();
        TryWriteLauncherDiagnosticLog("VesperNet: active host relay session cleared.");

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(requestUrl))
        {
            return;
        }

        try
        {
            await VesperFriendRelay.CloseHostSessionAsync(AccountSyncHttp, requestUrl, accessToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка закрытия Vesper Relay");
        }
    }

    private async Task PollPendingRelayConnectionsAsync(int localLanPort)
    {
        if (localLanPort <= 0 || localLanPort > 65535 || _activeRelaySession is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _isRelayHostPollInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var accessToken = GetCurrentCloudAccessToken();
            var config = LoadAccountSyncConfig();
            var requestUrl = ResolveRelayHostPendingUrl(config);
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(requestUrl))
            {
                return;
            }

            var pendingConnections = await VesperFriendRelay.GetPendingConnectionsAsync(
                AccountSyncHttp,
                requestUrl,
                accessToken).ConfigureAwait(false);
            if (pendingConnections.Count > 0)
            {
                TryWriteLauncherDiagnosticLog(
                    $"VesperNet: pending host connections={pendingConnections.Count}, mode={_activeRelaySession.TransportMode}, lanPort={localLanPort}");
            }

            foreach (var pendingConnection in pendingConnections)
            {
                var shouldStart = false;
                lock (_activeRelayHostConnections)
                {
                    shouldStart = _activeRelayHostConnections.Add(pendingConnection.ConnectionId);
                }

                if (!shouldStart)
                {
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (IsVesperNetOverlayTransportMode(_activeRelaySession?.TransportMode))
                        {
                            TryWriteLauncherDiagnosticLog(
                                $"VesperNet: host attach start connectionId={pendingConnection.ConnectionId}, guest={pendingConnection.GuestUsername ?? "unknown"}");
                            var overlayResponse = await AttachVesperNetHostPeerAsync(
                                accessToken,
                                pendingConnection.WebSocketUrl,
                                pendingConnection.ConnectionId).ConfigureAwait(false);
                            if (overlayResponse is null || !overlayResponse.Ok)
                            {
                                throw new InvalidOperationException("VesperNet Overlay host attach did not complete.");
                            }

                            TryWriteLauncherDiagnosticLog(
                                $"VesperNet: host attach done connectionId={pendingConnection.ConnectionId}, peerIp={overlayResponse.PeerIp}, localIp={overlayResponse.LocalIp}");
                        }
                        else
                        {
                            TryWriteLauncherDiagnosticLog(
                                $"Vesper Relay: host TCP attach start connectionId={pendingConnection.ConnectionId}, guest={pendingConnection.GuestUsername ?? "unknown"}");
                            await VesperFriendRelay.AttachHostConnectionAsync(
                                accessToken,
                                pendingConnection.WebSocketUrl,
                                localLanPort).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        TryWriteErrorToLog(
                            ex,
                            $"Ошибка relay-подключения для гостя {pendingConnection.GuestUsername ?? pendingConnection.ConnectionId}");
                    }
                    finally
                    {
                        lock (_activeRelayHostConnections)
                        {
                            _activeRelayHostConnections.Remove(pendingConnection.ConnectionId);
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка опроса Vesper Relay");
        }
        finally
        {
            Interlocked.Exchange(ref _isRelayHostPollInProgress, 0);
        }
    }

    private void TrackGuestRelayTunnel(VesperGuestRelayTunnel tunnel)
    {
        _activeGuestRelayTunnels.RemoveAll(existing => existing.Completion.IsCompleted);
        _activeGuestRelayTunnels.Add(tunnel);

        _ = tunnel.Completion.ContinueWith(
            async _ =>
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => _activeGuestRelayTunnels.Remove(tunnel));
                    await tunnel.DisposeAsync();
                }
                catch
                {
                    // Ignore cleanup races.
                }
            },
            TaskScheduler.Default);
    }

    private async Task CloseAllGuestRelayTunnelsAsync()
    {
        var tunnels = _activeGuestRelayTunnels.ToArray();
        _activeGuestRelayTunnels.Clear();
        foreach (var tunnel in tunnels)
        {
            try
            {
                await tunnel.DisposeAsync();
            }
            catch
            {
                // Ignore guest tunnel cleanup failures.
            }
        }

        await ClearVesperNetOverlayAsync();
        TryWriteLauncherDiagnosticLog("VesperNet: guest relay tunnels and overlay state cleared.");
    }

    private async Task<CloudFriendOperationResult> RefreshCloudFriendsAsync(bool showStatusOnSuccess = false)
    {
        if (_isRefreshingCloudFriends)
        {
            return new CloudFriendOperationResult(true, null);
        }

        if (!HasAuthenticatedCloudSession())
        {
            ClearCloudFriendsSnapshot();
            RefreshFriendsList();
            RefreshIncomingFriendRequestsList();
            UpdateFriendsCloudStatusText("Войди в аккаунт, чтобы пользоваться облачными друзьями.");
            return new CloudFriendOperationResult(false, "Сначала войди в аккаунт.");
        }

        try
        {
            _isRefreshingCloudFriends = true;
            var friendsUrl = ResolveFriendsUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Get, friendsUrl, out var request, out _, out var errorMessage))
            {
                ClearCloudFriendsSnapshot();
                RefreshFriendsList();
                RefreshIncomingFriendRequestsList();
                UpdateFriendsCloudStatusText(errorMessage);
                return new CloudFriendOperationResult(false, errorMessage);
            }

            using var safeRequest = request!;
            using var response = await AccountSyncHttp.SendAsync(safeRequest);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                {
                    var sessionError = "Сессия аккаунта истекла. Войди снова.";
                    UpdateFriendsCloudStatusText(sessionError);
                    return new CloudFriendOperationResult(false, sessionError);
                }

                var fallbackMessage = $"Не удалось загрузить друзей. HTTP {(int)response.StatusCode}.";
                var resolvedError = ExtractCloudErrorMessage(responseBody, fallbackMessage);
                UpdateFriendsCloudStatusText(resolvedError);
                return new CloudFriendOperationResult(false, resolvedError);
            }

            var friendsResponse = JsonSerializer.Deserialize<CloudFriendsResponse>(responseBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var remoteFriends = friendsResponse?.Friends ?? [];
            var remoteIncomingRequests = friendsResponse?.IncomingRequests ?? [];
            var remoteOutgoingRequests = friendsResponse?.OutgoingRequests ?? [];

            await PreloadCloudAvatarCachesAsync(
                remoteFriends
                    .Where(friend => !string.IsNullOrWhiteSpace(friend.Username))
                    .Select(friend => (NormalizeMinecraftUsername(friend.Username!), friend.Avatar))
                    .Concat(remoteIncomingRequests
                        .Where(requestItem => requestItem.User is not null && !string.IsNullOrWhiteSpace(requestItem.User.Username))
                        .Select(requestItem => (NormalizeMinecraftUsername(requestItem.User!.Username!), requestItem.User.Avatar)))
                    .Concat(remoteOutgoingRequests
                        .Where(friend => !string.IsNullOrWhiteSpace(friend.Username))
                        .Select(friend => (NormalizeMinecraftUsername(friend.Username!), friend.Avatar))));

            var friends = friendsResponse?.Friends?
                .Where(friend => !string.IsNullOrWhiteSpace(friend.Username))
                .Select(friend => CreateFriendListItem(
                    friend.Username!,
                    friend.Avatar,
                    friend.IsOnline == true,
                    friend.LastSeenAtUtc,
                    friend.ActivityKind,
                    friend.ActivityName,
                    friend.VersionId,
                    friend.JoinHost,
                    friend.JoinPort,
                    friend.RelayRoomId,
                    friend.RelayTransportMode,
                    friend.IsJoinable == true))
                .ToList() ?? [];
            var incomingRequests = friendsResponse?.IncomingRequests?
                .Where(requestItem => requestItem.User is not null && !string.IsNullOrWhiteSpace(requestItem.User.Username))
                .Select(requestItem => CreateIncomingRequestItem(
                    requestItem.Id,
                    requestItem.User!.Username!,
                    requestItem.CreatedAtUtc,
                    requestItem.User.Avatar))
                .ToList() ?? [];
            var outgoingRequests = friendsResponse?.OutgoingRequests?
                .Select(friend => friend.Username)
                .OfType<string>()
                .ToList() ?? [];

            ApplyCloudFriendsSnapshot(friends, incomingRequests, outgoingRequests);
            RefreshFriendsList();
            RefreshIncomingFriendRequestsList();
            UpdateFriendsCloudStatusText();
            if (showStatusOnSuccess)
            {
                SetStatus("Список друзей обновлен.");
            }

            return new CloudFriendOperationResult(true, null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки друзей");
            var errorMessage = "Не удалось загрузить облачных друзей.";
            UpdateFriendsCloudStatusText(errorMessage);
            return new CloudFriendOperationResult(false, errorMessage);
        }
        finally
        {
            _isRefreshingCloudFriends = false;
        }
    }

    private async Task<CloudFriendOperationResult> SendFriendRequestAsync(string targetUsername)
    {
        try
        {
            var requestUrl = ResolveFriendRequestUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Post, requestUrl, out var request, out _, out var errorMessage))
            {
                return new CloudFriendOperationResult(false, errorMessage);
            }

            using (request)
            {
                request!.Content = new StringContent(
                    JsonSerializer.Serialize(new { username = targetUsername }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await AccountSyncHttp.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                    {
                        return new CloudFriendOperationResult(false, "Сессия аккаунта истекла. Войди снова.");
                    }

                    var fallbackMessage = $"Не удалось отправить заявку в друзья. HTTP {(int)response.StatusCode}.";
                    return new CloudFriendOperationResult(false, ExtractCloudErrorMessage(responseBody, fallbackMessage));
                }
            }

            return new CloudFriendOperationResult(true, null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка отправки заявки в друзья");
            return new CloudFriendOperationResult(false, "Не удалось отправить заявку в друзья.");
        }
    }

    private async Task<CloudFriendOperationResult> RespondFriendRequestAsync(long requestId, string action)
    {
        try
        {
            var requestUrl = ResolveFriendRespondUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Post, requestUrl, out var request, out _, out var errorMessage))
            {
                return new CloudFriendOperationResult(false, errorMessage);
            }

            using (request)
            {
                request!.Content = new StringContent(
                    JsonSerializer.Serialize(new { requestId, action }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await AccountSyncHttp.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                    {
                        return new CloudFriendOperationResult(false, "Сессия аккаунта истекла. Войди снова.");
                    }

                    var fallbackMessage = $"Не удалось обработать заявку в друзья. HTTP {(int)response.StatusCode}.";
                    return new CloudFriendOperationResult(false, ExtractCloudErrorMessage(responseBody, fallbackMessage));
                }
            }

            return new CloudFriendOperationResult(true, null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка обработки заявки в друзья");
            return new CloudFriendOperationResult(false, "Не удалось обработать заявку в друзья.");
        }
    }

    private async Task<CloudFriendOperationResult> RemoveFriendAsync(string targetUsername)
    {
        try
        {
            var requestUrl = ResolveFriendRemoveUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Post, requestUrl, out var request, out _, out var errorMessage))
            {
                return new CloudFriendOperationResult(false, errorMessage);
            }

            using (request)
            {
                request!.Content = new StringContent(
                    JsonSerializer.Serialize(new { username = targetUsername }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await AccountSyncHttp.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                    {
                        return new CloudFriendOperationResult(false, "Сессия аккаунта истекла. Войди снова.");
                    }

                    var fallbackMessage = $"Не удалось удалить друга. HTTP {(int)response.StatusCode}.";
                    return new CloudFriendOperationResult(false, ExtractCloudErrorMessage(responseBody, fallbackMessage));
                }
            }

            return new CloudFriendOperationResult(true, null);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка удаления друга");
            return new CloudFriendOperationResult(false, "Не удалось удалить друга.");
        }
    }

    private async Task<GameActivitySnapshot> BuildCurrentGameActivitySnapshotAsync()
    {
        if (!_gameProcessMonitor.IsRunning ||
            string.IsNullOrWhiteSpace(_runningGameInstanceDirectory))
        {
            _lastDetectedLanPort = null;
            _lastDetectedLanPortAtUtc = DateTimeOffset.MinValue;
            _ = CloseActiveRelaySessionAsync();
            return new GameActivitySnapshot("launcher", null, _runningGameVersionId, null, null, null, null, false);
        }

        var instanceDirectory = _runningGameInstanceDirectory!;
        var activityName = TryResolveActiveWorldName(instanceDirectory);
        var latestLogText = TryReadLatestLogTail(instanceDirectory);
        var lanPort = TryParseLanPort(latestLogText);
        if (lanPort is int openedLanPort)
        {
            _lastDetectedLanPort = openedLanPort;
            _lastDetectedLanPortAtUtc = DateTimeOffset.UtcNow;
            return await BuildLanHostActivitySnapshotAsync(activityName, openedLanPort);
        }

        if (TryGetRecentLanPort(out var recentLanPort))
        {
            TryWriteLauncherDiagnosticLog(
                $"VesperNet: reusing recent LAN port {recentLanPort} while latest.log has not reported it yet.");
            return await BuildLanHostActivitySnapshotAsync(activityName, recentLanPort);
        }

        _lastDetectedLanPort = null;
        _lastDetectedLanPortAtUtc = DateTimeOffset.MinValue;
        _ = CloseActiveRelaySessionAsync();

        if (!string.IsNullOrWhiteSpace(activityName))
        {
            return new GameActivitySnapshot("singleplayer", activityName, _runningGameVersionId, null, null, null, null, false);
        }

        var multiplayerEndpoint = TryParseMultiplayerEndpoint(latestLogText);
        if (multiplayerEndpoint is not null)
        {
            return new GameActivitySnapshot(
                "multiplayer",
                $"{multiplayerEndpoint.Host}:{multiplayerEndpoint.Port}",
                _runningGameVersionId,
                null,
                null,
                null,
                null,
                false);
        }

        return new GameActivitySnapshot("in_game", null, _runningGameVersionId, null, null, null, null, false);
    }

    private async Task<GameActivitySnapshot> BuildLanHostActivitySnapshotAsync(string? activityName, int localLanPort)
    {
        var joinEndpoint = await ResolvePublishedJoinEndpointAsync(localLanPort);
        var relaySession = await EnsureActiveRelaySessionAsync(localLanPort);
        return new GameActivitySnapshot(
            "lan_host",
            activityName ?? "Локальный мир",
            _runningGameVersionId,
            joinEndpoint?.Host,
            joinEndpoint?.Port,
            relaySession?.RoomId,
            relaySession?.TransportMode,
            joinEndpoint is not null || relaySession is not null);
    }

    private bool TryGetRecentLanPort(out int lanPort)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (_lastDetectedLanPort is int cachedLanPort &&
            nowUtc - _lastDetectedLanPortAtUtc <= RecentLanPortRetentionInterval &&
            (_activeRelaySession is not null || _publishedJoinEndpoint is not null))
        {
            lanPort = cachedLanPort;
            return true;
        }

        lanPort = 0;
        return false;
    }

    private static string? TryReadLatestLogTail(string instanceDirectory, int maxBytes = 196_608)
    {
        try
        {
            var latestLogPath = Path.Combine(instanceDirectory, "logs", "latest.log");
            if (!File.Exists(latestLogPath))
            {
                return null;
            }

            using var stream = new FileStream(
                latestLogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes)
            {
                stream.Seek(-maxBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static int? TryParseLanPort(string? latestLogText)
    {
        if (string.IsNullOrWhiteSpace(latestLogText))
        {
            return null;
        }

        var matches = LanPortRegex.Matches(latestLogText);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            if (!match.Success ||
                !int.TryParse(match.Groups["port"].Value, out var port) ||
                port <= 0 ||
                port > 65535)
            {
                continue;
            }

            return port;
        }

        return null;
    }

    private static DetectedServerEndpoint? TryParseMultiplayerEndpoint(string? latestLogText)
    {
        if (string.IsNullOrWhiteSpace(latestLogText))
        {
            return null;
        }

        var matches = MultiplayerConnectRegex.Matches(latestLogText);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            if (!match.Success ||
                !int.TryParse(match.Groups["port"].Value, out var port) ||
                port <= 0 ||
                port > 65535)
            {
                continue;
            }

            var host = match.Groups["host"].Value.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            return new DetectedServerEndpoint(host, port);
        }

        return null;
    }

    private static string? TryResolveActiveWorldName(string instanceDirectory)
    {
        try
        {
            var savesDirectory = Path.Combine(instanceDirectory, "saves");
            if (!Directory.Exists(savesDirectory))
            {
                return null;
            }

            var recentWorld = Directory.EnumerateDirectories(savesDirectory)
                .Select(path => new DirectoryInfo(path))
                .Where(directory => File.Exists(Path.Combine(directory.FullName, "level.dat")))
                .Select(directory => new
                {
                    directory.Name,
                    LastActivityUtc = GetWorldActivityTimestampUtc(directory.FullName)
                })
                .Where(entry => DateTimeOffset.UtcNow - entry.LastActivityUtc <= TimeSpan.FromMinutes(3))
                .OrderByDescending(entry => entry.LastActivityUtc)
                .FirstOrDefault();

            return recentWorld?.Name;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset GetWorldActivityTimestampUtc(string worldDirectory)
    {
        var latestTimestamp = DateTimeOffset.MinValue;
        void ConsiderFile(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var fileTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (fileTimestamp > latestTimestamp)
            {
                latestTimestamp = fileTimestamp;
            }
        }

        try
        {
            var directoryTimestamp = new DateTimeOffset(Directory.GetLastWriteTimeUtc(worldDirectory), TimeSpan.Zero);
            if (directoryTimestamp > latestTimestamp)
            {
                latestTimestamp = directoryTimestamp;
            }

            ConsiderFile(Path.Combine(worldDirectory, "session.lock"));
            ConsiderFile(Path.Combine(worldDirectory, "level.dat"));
            ConsiderFile(Path.Combine(worldDirectory, "level.dat_old"));
        }
        catch
        {
            // ignore
        }

        return latestTimestamp == DateTimeOffset.MinValue
            ? DateTimeOffset.UtcNow - TimeSpan.FromDays(365)
            : latestTimestamp;
    }

    private static string? ResolvePreferredLanAddress()
    {
        try
        {
            LanAddressCandidate? fallbackCandidate = null;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var isVpnLikeAdapter = IsVpnLikeNetworkInterface(adapter);
                var hasGateway = false;
                try
                {
                    hasGateway = adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                        gateway?.Address is IPAddress address &&
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address) &&
                        !IsLinkLocalAddress(address));
                }
                catch
                {
                    hasGateway = false;
                }

                foreach (var unicastAddress in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicastAddress.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address) ||
                        IsLinkLocalAddress(address))
                    {
                        continue;
                    }

                    var candidate = new LanAddressCandidate(
                        address.ToString(),
                        isVpnLikeAdapter,
                        IsPrivateLanAddress(address),
                        hasGateway,
                        adapter.Speed);

                    if (candidate.IsVpnLike)
                    {
                        return candidate.Host;
                    }

                    if (candidate.IsPrivateLan)
                    {
                        fallbackCandidate = ChooseBetterLanCandidate(fallbackCandidate, candidate);
                        continue;
                    }

                    fallbackCandidate ??= candidate;
                }
            }

            return fallbackCandidate?.Host;
        }
        catch
        {
            return null;
        }
    }

    private static LanAddressCandidate ChooseBetterLanCandidate(
        LanAddressCandidate? current,
        LanAddressCandidate candidate)
    {
        if (current is null)
        {
            return candidate;
        }

        if (candidate.HasGateway != current.HasGateway)
        {
            return candidate.HasGateway ? candidate : current;
        }

        if (candidate.Speed != current.Speed)
        {
            return candidate.Speed > current.Speed ? candidate : current;
        }

        return current;
    }

    private static bool IsVpnLikeNetworkInterface(NetworkInterface adapter)
    {
        var name = $"{adapter.Name} {adapter.Description}";
        return name.Contains("radmin", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("hamachi", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("zerotier", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("openvpn", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vpn", StringComparison.OrdinalIgnoreCase) ||
               adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
    }

    private static bool IsLinkLocalAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               bytes[0] == 169 &&
               bytes[1] == 254;
    }

    private async Task<PublishedJoinEndpoint?> ResolvePublishedJoinEndpointAsync(int localLanPort)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        if (_publishedJoinEndpoint is not null &&
            _publishedJoinLocalPort == localLanPort &&
            nowUtc < _publishedJoinEndpointExpiresAtUtc)
        {
            return _publishedJoinEndpoint;
        }

        await _publishedJoinEndpointLock.WaitAsync();
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (_publishedJoinEndpoint is not null &&
                _publishedJoinLocalPort == localLanPort &&
                nowUtc < _publishedJoinEndpointExpiresAtUtc)
            {
                return _publishedJoinEndpoint;
            }

            PublishedJoinEndpoint? resolvedEndpoint = null;
            var lanHost = ResolvePreferredLanAddress();
            if (!string.IsNullOrWhiteSpace(lanHost))
            {
                resolvedEndpoint = new PublishedJoinEndpoint(lanHost.Trim(), localLanPort, false);
                _ = TryPromotePublishedJoinEndpointToInternetAsync(localLanPort);
            }
            else
            {
                resolvedEndpoint = await TryResolveInternetJoinEndpointAsync(localLanPort);
            }

            _publishedJoinLocalPort = localLanPort;
            _publishedJoinEndpoint = resolvedEndpoint;
            _publishedJoinEndpointExpiresAtUtc = nowUtc.Add(
                resolvedEndpoint?.IsInternetEndpoint == true
                    ? TimeSpan.FromMinutes(2)
                    : TimeSpan.FromSeconds(25));

            return resolvedEndpoint;
        }
        finally
        {
            _publishedJoinEndpointLock.Release();
        }
    }

    private async Task TryPromotePublishedJoinEndpointToInternetAsync(int localLanPort)
    {
        if (Interlocked.Exchange(ref _isInternetJoinEndpointPromotionInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var internetEndpoint = await TryResolveInternetJoinEndpointAsync(localLanPort);
            if (internetEndpoint is null)
            {
                return;
            }

            await _publishedJoinEndpointLock.WaitAsync();
            try
            {
                if (_publishedJoinLocalPort != localLanPort)
                {
                    return;
                }

                _publishedJoinEndpoint = internetEndpoint;
                _publishedJoinEndpointExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2);
            }
            finally
            {
                _publishedJoinEndpointLock.Release();
            }

            if (HasAuthenticatedCloudSession())
            {
                await Dispatcher.InvokeAsync(() => _ = PingPresenceAsync());
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка публикации интернет-подключения к миру");
        }
        finally
        {
            Interlocked.Exchange(ref _isInternetJoinEndpointPromotionInProgress, 0);
        }
    }

    private async Task<PublishedJoinEndpoint?> TryResolveInternetJoinEndpointAsync(int localLanPort)
    {
        if (localLanPort <= 0 || localLanPort > 65535)
        {
            return null;
        }

        try
        {
            var natDevice = await DiscoverNatDeviceAsync();
            if (natDevice is null)
            {
                return null;
            }

            if (_publishedJoinNatMapping is not null &&
                _publishedJoinNatMapping.PrivatePort != localLanPort)
            {
                await TryDeleteNatMappingAsync(natDevice, _publishedJoinNatMapping);
                _publishedJoinNatMapping = null;
            }

            var mapping = await EnsureNatMappingAsync(natDevice, localLanPort);
            if (mapping is null)
            {
                return null;
            }

            var externalAddress = await natDevice.GetExternalIPAsync();
            if (externalAddress is null || !IsPublicInternetAddress(externalAddress))
            {
                return null;
            }

            _publishedJoinNatDevice = natDevice;
            _publishedJoinNatMapping = mapping;
            return new PublishedJoinEndpoint(
                externalAddress.ToString(),
                mapping.PublicPort,
                true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<NatDevice?> DiscoverNatDeviceAsync()
    {
        var discoverer = new NatDiscoverer();

        try
        {
            using var upnpCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, upnpCts);
        }
        catch
        {
            try
            {
                using var pmpCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await discoverer.DiscoverDeviceAsync(PortMapper.Pmp, pmpCts);
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task<Mapping?> EnsureNatMappingAsync(NatDevice natDevice, int localLanPort)
    {
        if (_publishedJoinNatMapping is not null &&
            _publishedJoinNatMapping.PrivatePort == localLanPort)
        {
            try
            {
                await natDevice.CreatePortMapAsync(_publishedJoinNatMapping);
                return _publishedJoinNatMapping;
            }
            catch
            {
                // Fall back to trying a fresh mapping below.
            }
        }

        var candidateExternalPorts = new List<int> { localLanPort };
        while (candidateExternalPorts.Count < 4)
        {
            var randomPort = Random.Shared.Next(20000, 55000);
            if (!candidateExternalPorts.Contains(randomPort))
            {
                candidateExternalPorts.Add(randomPort);
            }
        }

        foreach (var externalPort in candidateExternalPorts)
        {
            var mapping = new Mapping(
                Protocol.Tcp,
                localLanPort,
                externalPort,
                600,
                "Vesper Launcher World");
            try
            {
                await natDevice.CreatePortMapAsync(mapping);
                return mapping;
            }
            catch
            {
                // Try the next candidate port.
            }
        }

        return null;
    }

    private async Task ReleasePublishedJoinEndpointAsync()
    {
        await _publishedJoinEndpointLock.WaitAsync();
        try
        {
            _publishedJoinEndpoint = null;
            _publishedJoinEndpointExpiresAtUtc = DateTimeOffset.MinValue;
            _publishedJoinLocalPort = null;

            if (_publishedJoinNatDevice is not null && _publishedJoinNatMapping is not null)
            {
                await TryDeleteNatMappingAsync(_publishedJoinNatDevice, _publishedJoinNatMapping);
            }

            _publishedJoinNatMapping = null;
            _publishedJoinNatDevice = null;
        }
        finally
        {
            _publishedJoinEndpointLock.Release();
        }

        await CloseActiveRelaySessionAsync();
    }

    private static async Task TryDeleteNatMappingAsync(NatDevice natDevice, Mapping mapping)
    {
        try
        {
            await natDevice.DeletePortMapAsync(mapping);
        }
        catch
        {
            // Ignore cleanup failures, the mapping will expire on router side.
        }
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsPublicInternetAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !IsPrivateLanAddress(address) &&
               bytes[0] != 127 &&
               bytes[0] != 169 &&
               !(bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
    }

    private async Task PingPresenceAsync()
    {
        try
        {
            var requestUrl = ResolvePresencePingUrl(LoadAccountSyncConfig());
            if (!TryCreateAuthorizedCloudRequest(HttpMethod.Post, requestUrl, out var request, out _, out _))
            {
                return;
            }

            using (request)
            {
                var activitySnapshot = await BuildCurrentGameActivitySnapshotAsync();
                request!.Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        activityKind = activitySnapshot.ActivityKind,
                        activityName = activitySnapshot.ActivityName,
                        versionId = activitySnapshot.VersionId,
                        joinHost = activitySnapshot.JoinHost,
                        joinPort = activitySnapshot.JoinPort,
                        relayRoomId = activitySnapshot.RelayRoomId,
                        relayTransportMode = activitySnapshot.RelayTransportMode,
                        isJoinable = activitySnapshot.IsJoinable
                    }),
                    Encoding.UTF8,
                    "application/json");
                using var response = await AccountSyncHttp.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    if (HandleUnauthorizedCloudResponse(response.StatusCode, responseBody))
                    {
                        return;
                    }

                      TryWriteErrorToLog(
                          new HttpRequestException($"Presence ping failed: HTTP {(int)response.StatusCode}. {responseBody}"),
                          "Ошибка heartbeat присутствия");
                  }
                  else if (activitySnapshot.ActivityKind == "lan_host" &&
                           HasRelayEndpoint(activitySnapshot.RelayRoomId, activitySnapshot.RelayTransportMode) &&
                           _activeRelayLanPort is int relayLanPort)
                  {
                      await PollPendingRelayConnectionsAsync(relayLanPort);
                  }
              }
          }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка heartbeat присутствия");
        }
    }

    private async Task SyncPresenceAndFriendsAsync(bool showStatusOnSuccess = false)
    {
        if (!HasAuthenticatedCloudSession())
        {
            return;
        }

        await PingPresenceAsync();
        await RefreshCloudFriendsAsync(showStatusOnSuccess);
    }

    private async Task SubmitAccountActionForModeAsync(AccountEntryMode mode, string? username = null, string? password = null)
    {
        switch (mode)
        {
            case AccountEntryMode.Login:
                await SubmitLoginAccountAsync(username, password);
                break;
            case AccountEntryMode.Register:
                await SubmitCreateAccountAsync(username, password);
                break;
            case AccountEntryMode.Guest:
                ClearGuestIdentityState();
                SetAccountEntryMode(AccountEntryMode.Login);
                SetStatus("Гостевой режим отключен. Войди или зарегистрируйся.");
                break;
            default:
                SetStatus("Сначала выбери: вход или регистрация.");
                break;
        }
    }

    private async Task SubmitCreateAccountAsync(string? usernameOverride = null, string? passwordOverride = null)
    {
        if (HasAuthenticatedCloudSession())
        {
            RefreshAccountSection();
            return;
        }

        var enteredUsername = (usernameOverride ?? AccountNicknameTextBox.Text).Trim();
        var normalizedUsername = NormalizeMinecraftUsername(enteredUsername);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || !UsernameRegex.IsMatch(normalizedUsername))
        {
            ShowAccountNotice(
                "Ник должен быть 3-16 символов и содержать только A-Z, a-z, 0-9 или _.",
                "Некорректный ник");
            return;
        }

        var password = passwordOverride ?? AccountPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinAccountPasswordLength)
        {
            ShowAccountNotice(
                $"Пароль должен быть минимум {MinAccountPasswordLength} символов.",
                "Слабый пароль");
            return;
        }

        CloudAuthRequestResult registerResult;
        SetBusy(true, "Регистрирую аккаунт...");
        try
        {
            registerResult = await RegisterCloudAccountAsync(normalizedUsername, password);
        }
        finally
        {
            SetBusy(false);
        }
        if (!registerResult.Success)
        {
            ShowAccountNotice(
                registerResult.ErrorMessage ?? "Не удалось зарегистрировать аккаунт.",
                "Ошибка регистрации");
            SetStatus("Ошибка регистрации аккаунта.");
            return;
        }

        var resolvedUsername = string.IsNullOrWhiteSpace(registerResult.Username)
            ? normalizedUsername
            : NormalizeMinecraftUsername(registerResult.Username!);
        _accountState = BuildAuthenticatedAccountState(
            resolvedUsername,
            password,
            registerResult.AccessToken,
            registerResult.ExpiresAtUtc);
        ClearGuestIdentityState();
        SaveAccountState();
        UsernameTextBox.Text = resolvedUsername;
        AddOrPromoteSavedUsername(resolvedUsername);
        ApplyAccountUiState();
        HideSidePanel();
        SetStatus($"Аккаунт создан: {resolvedUsername}.");
        _ = SyncCloudAccountAfterAuthenticationAsync();
    }

    private async Task SubmitLoginAccountAsync(string? usernameOverride = null, string? passwordOverride = null)
    {
        if (HasAuthenticatedCloudSession())
        {
            RefreshAccountSection();
            return;
        }

        var enteredUsername = (usernameOverride ?? AccountNicknameTextBox.Text).Trim();
        var normalizedUsername = NormalizeMinecraftUsername(enteredUsername);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || !UsernameRegex.IsMatch(normalizedUsername))
        {
            ShowAccountNotice(
                "Ник должен быть 3-16 символов и содержать только A-Z, a-z, 0-9 или _.",
                "Некорректный ник");
            return;
        }

        var password = passwordOverride ?? AccountPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinAccountPasswordLength)
        {
            ShowAccountNotice(
                $"Пароль должен быть минимум {MinAccountPasswordLength} символов.",
                "Некорректный пароль");
            return;
        }

        CloudAuthRequestResult loginResult;
        SetBusy(true, "Выполняю вход...");
        try
        {
            loginResult = await LoginCloudAccountAsync(normalizedUsername, password);
        }
        finally
        {
            SetBusy(false);
        }
        if (!loginResult.Success)
        {
            ShowAccountNotice(
                loginResult.ErrorMessage ?? "Не удалось войти в аккаунт.",
                "Ошибка входа");
            SetStatus("Ошибка входа в аккаунт.");
            return;
        }

        var resolvedUsername = string.IsNullOrWhiteSpace(loginResult.Username)
            ? normalizedUsername
            : NormalizeMinecraftUsername(loginResult.Username!);
        _accountState = BuildAuthenticatedAccountState(
            resolvedUsername,
            password,
            loginResult.AccessToken,
            loginResult.ExpiresAtUtc);
        ClearGuestIdentityState();
        SaveAccountState();
        UsernameTextBox.Text = resolvedUsername;
        AddOrPromoteSavedUsername(resolvedUsername);
        ApplyAccountUiState();
        HideSidePanel();
        SetStatus($"Вход выполнен: {resolvedUsername}.");
        _ = SyncCloudAccountAfterAuthenticationAsync();
    }

    private void ShowAccountNotice(string message, string title)
    {
        if (App.IsPhotinoBackendHost)
        {
            SetStatus($"{title}: {message}");
            ApplyAccountUiState();
            return;
        }

        MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task SyncCloudAccountAfterAuthenticationAsync()
    {
        try
        {
            await TrySyncCloudProfileAsync();
            await SyncPresenceAndFriendsAsync();
            ApplyAccountUiState();
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка фоновой синхронизации аккаунта после входа");
        }
    }

    private async Task SubmitIncognitoAsync(string? usernameOverride = null)
    {
        if (HasAuthenticatedCloudSession())
        {
            RefreshAccountSection();
            return;
        }

        var enteredUsername = (usernameOverride ?? AccountNicknameTextBox.Text).Trim();
        var normalizedUsername = NormalizeMinecraftUsername(enteredUsername);
        if (string.IsNullOrWhiteSpace(normalizedUsername) || !UsernameRegex.IsMatch(normalizedUsername))
        {
            MessageBox.Show(
                this,
                "Ник должен быть 3-16 символов и содержать только A-Z, a-z, 0-9 или _.",
                "Некорректный ник",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Проверяю ник инкогнито...");
        var availabilityResult = await CheckIncognitoNicknameAvailabilityAsync(normalizedUsername);
        SetBusy(false);

        if (!availabilityResult.Success)
        {
            MessageBox.Show(
                this,
                availabilityResult.ErrorMessage ?? "Не удалось проверить ник инкогнито.",
                "Инкогнито",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("Не удалось включить инкогнито.");
            return;
        }

        if (!availabilityResult.IsAvailable)
        {
            var occupiedNickname = string.IsNullOrWhiteSpace(availabilityResult.ExistingUsername)
                ? normalizedUsername
                : availabilityResult.ExistingUsername;
            MessageBox.Show(
                this,
                $"Ник {occupiedNickname} уже занят аккаунтом Vesper. Выбери другой.",
                "Ник занят",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("Ник занят аккаунтом Vesper.");
            return;
        }

        _guestIdentityState = new GuestIdentityState
        {
            Username = normalizedUsername
        };
        SaveGuestIdentityState();
        _isEditingIncognitoNickname = false;

        UsernameTextBox.Text = normalizedUsername;
        AddOrPromoteSavedUsername(normalizedUsername);
        ClearCloudFriendsSnapshot();
        ApplyAccountUiState();
        HideSidePanel();
        SetStatus($"Инкогнито включено: {normalizedUsername}.");
    }

    private void CreateAccountButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HasAuthenticatedCloudSession())
        {
            RefreshAccountSection();
            return;
        }

        SetAccountEntryMode(AccountEntryMode.Register);
        FocusAccountNicknameEditorIfNeeded();
    }

    private void LoginAccountButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HasAuthenticatedCloudSession())
        {
            RefreshAccountSection();
            return;
        }

        SetAccountEntryMode(AccountEntryMode.Login);
        FocusAccountNicknameEditorIfNeeded();
    }

    private void EnterIncognitoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HasAuthenticatedCloudSession())
        {
            RefreshAccountSection();
            return;
        }

        SetAccountEntryMode(AccountEntryMode.Guest);
        FocusAccountNicknameEditorIfNeeded();
    }

    private async void AccountAuthPrimaryButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SubmitAccountActionForModeAsync(_accountEntryMode);
    }

    private async void ChangeAvatarButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HasIncognitoIdentity() && !HasAuthenticatedCloudSession())
        {
            _isEditingIncognitoNickname = true;
            AccountNicknameTextBox.Text = _guestIdentityState!.Username;
            AccountPasswordBox.Password = string.Empty;
            ShowSidePanelSection(SidePanelSection.Account);
            RefreshAccountSection();
            SetStatus("Укажи новый ник и снова нажми Инкогнито.");
            return;
        }

        if (!HasAuthenticatedCloudSession())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Выбери аватар",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var sourcePath = dialog.FileName;
            var avatarBytes = ConvertImageFileToPngBytes(sourcePath, 256);
            var avatarFileName = SaveAvatarBytesToLocalProfile(_accountState!.Username, avatarBytes);
            UpdateAccountAvatarFileName(avatarFileName);
            RefreshAccountSection();
            RefreshFriendsProfileAvatarPreview();
            ApplyAccountUiState();

            SetBusy(true, "Сохраняю аватар...");
            var uploadResult = await UploadAvatarToCloudAsync(avatarBytes);
            SetBusy(false);

            RefreshAccountSection();
            RefreshFriendsProfileAvatarPreview();

            if (!uploadResult.Success)
            {
                ShowAccountNotice(
                    $"Аватар обновлен локально, но не сохранился в облаке.{Environment.NewLine}{Environment.NewLine}{uploadResult.ErrorMessage}",
                    "Облако аватара");
                SetStatus("Аватар обновлен локально.");
                return;
            }

            await TrySyncCloudProfileAsync(uploadLocalAvatarIfMissing: false);
            await RefreshCloudFriendsAsync(showStatusOnSuccess: false);
            ApplyAccountUiState();
            SetStatus("Аватар обновлен и сохранен в облаке.");
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError(ex, "Ошибка аватара");
        }
    }

    private async void LogoutAccountButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (HasIncognitoIdentity() && !HasAuthenticatedCloudSession())
        {
            var guestConfirmResult = MessageBox.Show(
                this,
                "Выйти из режима инкогнито? Ник останется в истории, но активный инкогнито-профиль будет отключен.",
                "Выход из инкогнито",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (guestConfirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            ClearGuestIdentityState();
            UsernameTextBox.Text = HasRegisteredAccount()
                ? _accountState!.Username
                : _savedUsernames.FirstOrDefault() ?? string.Empty;
            ShowSidePanelSection(SidePanelSection.Account);
            ApplyAccountUiState();
            SetStatus("Инкогнито отключено.");
            return;
        }

        if (!HasAuthenticatedCloudSession())
        {
            return;
        }

        var confirmResult = MessageBox.Show(
            this,
            "Выйти из аккаунта? После выхода можно будет снова войти в этот или другой аккаунт.",
            "Выход из аккаунта",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "Выход из аккаунта...");
        await TryLogoutCloudSessionAsync(_accountState);
        ResetAccountSessionState();
        ShowSidePanelSection(SidePanelSection.Account);
        SetBusy(false);
        SetStatus("Вы вышли из аккаунта. Можно войти снова.");
    }

    private async Task<bool> TrySyncAccountAsync(LauncherAccountState accountState)
    {
        try
        {
            var config = LoadAccountSyncConfig();
            var registerUrl = ResolveRegisterUrl(config);
            if (string.IsNullOrWhiteSpace(registerUrl) || config is null)
            {
                return false;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, registerUrl);
            ApplyConfiguredHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var payload = new
            {
                username = accountState.Username,
                passwordHash = accountState.PasswordHash,
                passwordSalt = accountState.PasswordSalt,
                passwordAlgorithm = accountState.PasswordAlgorithm,
                passwordIterations = accountState.PasswordIterations,
                createdAtUtc = accountState.CreatedAtUtc
            };
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await AccountSyncHttp.SendAsync(request);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                return true;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Не удалось синхронизировать аккаунт. HTTP {(int)response.StatusCode}. Ответ: {responseBody}");
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка синхронизации аккаунта");
            return false;
        }
    }

    private async Task TryAutoSyncAccountIfNeededAsync()
    {
        if (!HasRegisteredAccount() || !string.IsNullOrWhiteSpace(_accountState!.CloudSyncedAtUtc))
        {
            return;
        }

        var syncResult = await TrySyncAccountAsync(_accountState);
        if (!syncResult)
        {
            return;
        }

        MarkAccountAsCloudSyncedNow();
        RefreshAccountSection();
        if (_activeSidePanelSection == SidePanelSection.Account)
        {
            SetStatus("Аккаунт синхронизирован с облаком.");
        }
    }

    private AccountSyncConfig? LoadAccountSyncConfig()
    {
        try
        {
            var localConfigPath = Path.Combine(AppContext.BaseDirectory, "account-sync.json");
            var configPath = File.Exists(_accountSyncConfigPath)
                ? _accountSyncConfigPath
                : localConfigPath;
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<AccountSyncConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки конфигурации account-sync");
            return null;
        }
    }

    private void UpdateLaunchButtonIdleState()
    {
        if (_isBusy)
        {
            return;
        }

        if (_gameProcessMonitor.IsRunning)
        {
            LaunchButtonText.Text = "Закрыть";
            return;
        }

        if (!HasLaunchIdentity())
        {
            LaunchButtonText.Text = "Войти";
            return;
        }

        LaunchButtonText.Text = ResolveSelectedVersionState().ButtonText;
    }

    private VersionState ResolveSelectedVersionState()
    {
        if (_selectedVersionChoice is null)
        {
            return VersionState.NotInstalled;
        }

        return _versionStateMachine.GetState(
            ResolveCurrentProfileDirectory(),
            _selectedVersionChoice.Version);
    }

    private VersionState ResolveVersionStateForChoice(VersionSelectionEntry choice)
    {
        return _versionStateMachine.GetState(
            ResolveCurrentProfileDirectory(),
            choice.Version);
    }

    private void EnsureFirstRunCleanState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var launcherDataDirectory = GetPreferredLauncherDataDirectory();
        var markerPath = Path.Combine(launcherDataDirectory, FirstRunMarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(launcherDataDirectory);
            TryDeleteFile(_savedUsernamesPath);
            TryDeleteFile(_friendsPath);
            TryDeleteFile(_uiStatePath);
            TryDeleteFile(Path.Combine(launcherDataDirectory, "msa-token.json"));
            TryDeleteFile(Path.Combine(launcherDataDirectory, "vesper-auth.log"));
            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка первичной очистки данных");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private void EnsureSharedSkinRegistryConfig()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "skin-sync.json");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var launcherDataDirectory = GetPreferredLauncherDataDirectory();
        var targetPath = Path.Combine(launcherDataDirectory, "skin-sync.json");
        try
        {
            Directory.CreateDirectory(launcherDataDirectory);
            if (!File.Exists(targetPath))
            {
                File.Copy(sourcePath, targetPath, overwrite: false);
                return;
            }

            var sourceContent = File.ReadAllText(sourcePath);
            var targetContent = File.ReadAllText(targetPath);
            if (!string.Equals(sourceContent, targetContent, StringComparison.Ordinal))
            {
                File.WriteAllText(targetPath, sourceContent);
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка копирования skin-sync.json");
        }
    }

    private void EnsureSharedAccountSyncConfig()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "account-sync.json");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            var appDataDirectory = Path.GetDirectoryName(_accountSyncConfigPath);
            if (!string.IsNullOrWhiteSpace(appDataDirectory))
            {
                Directory.CreateDirectory(appDataDirectory);
            }

            if (!File.Exists(_accountSyncConfigPath))
            {
                File.Copy(sourcePath, _accountSyncConfigPath, overwrite: false);
                return;
            }

            var sourceContent = File.ReadAllText(sourcePath);
            var targetContent = File.ReadAllText(_accountSyncConfigPath);
            if (!string.Equals(sourceContent, targetContent, StringComparison.Ordinal))
            {
                File.WriteAllText(_accountSyncConfigPath, sourceContent);
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка копирования account-sync.json");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            _windowSource = source;
            _windowSource.AddHook(WindowMessageHook);
        }

        UpdateWindowClip();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_startupInitializationStarted)
        {
            return;
        }

        _startupInitializationStarted = true;

        StartBackgroundStorageMaintenance();
        UpdateCloudSyncTimerIntervals();
        _backgroundRotationTimer.Start();
        _cloudFriendsRefreshTimer.Start();
        _presenceHeartbeatTimer.Start();
        _vesperNetStatusTimer.Start();
        UpdateWindowClip();
        ApplyLauncherSettingsUiState();
        UpdateCurrentNicknameDisplay();
        UpdateProfilePathLabel();
        RefreshBackgroundSection();
        TryApplyCustomBackgroundImage();

        await Dispatcher.Yield(DispatcherPriority.ContextIdle);

        TryApplyCustomLogoImage();
        TryLoadClickSound();
        InitializeSkinPreviewModel();
        RefreshSkinFiles(_uiState.SelectedSkinFileName);
        RefreshModsSection(refreshCatalog: false);
        RefreshFriendsSection(triggerBackgroundRefresh: false);

        var autoSyncTask = TryAutoSyncAccountIfNeededAsync();
        await LoadVersionsAsync();
        await autoSyncTask;
        await TrySyncCloudProfileAsync();
        await SyncPresenceAndFriendsAsync();
        await RefreshVesperNetStatusAsync();

        if (!HasAuthenticatedCloudSession())
        {
            ShowSidePanelSection(SidePanelSection.Account);
            SetStatus(HasRegisteredAccount()
                ? "Сессия истекла. Войди снова."
                : "Сначала войди или зарегистрируйся.");
        }
        else
        {
            UpdateLaunchButtonIdleState();
        }
    }

    private void StartBackgroundStorageMaintenance()
    {
        _ = Task.Run(() =>
        {
            try
            {
                _launcherService.RunStorageMaintenance();
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    TryWriteErrorToLog(ex, "Ошибка фоновой очистки лаунчера")));
            }
        });
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _gameProcessMonitor.ProcessExited -= GameProcessMonitor_OnProcessExited;
        _gameProcessMonitor.Dispose();
        _backgroundRotationTimer.Stop();
        _backgroundRotationTimer.Tick -= BackgroundRotationTimer_OnTick;
        _skinPreviewTimer.Stop();
        _skinPreviewTimer.Tick -= SkinPreviewTimer_OnTick;
        _cloudFriendsRefreshTimer.Stop();
        _cloudFriendsRefreshTimer.Tick -= CloudFriendsRefreshTimer_OnTick;
        _presenceHeartbeatTimer.Stop();
        _presenceHeartbeatTimer.Tick -= PresenceHeartbeatTimer_OnTick;
        _vesperNetStatusTimer.Stop();
        _vesperNetStatusTimer.Tick -= VesperNetStatusTimer_OnTick;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }

        _ = ReleasePublishedJoinEndpointAsync();
        _ = CloseAllGuestRelayTunnelsAsync();
    }

    private void GameProcessMonitor_OnProcessExited(object? sender, EventArgs e)
    {
        void ApplyExitedState()
        {
            _gameProcess = null;
            _runningGameInstanceDirectory = null;
            _runningGameVersionId = null;
            _ = ReleasePublishedJoinEndpointAsync();
            RestoreLauncherAfterGameExit();
            if (!_isBusy)
            {
                UpdateLaunchButtonIdleState();
            }
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyExitedState();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ApplyExitedState));
    }

    private async void CloudFriendsRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isBusy || _isRefreshingCloudFriends || !HasAuthenticatedCloudSession() || !IsFriendsSectionOpen())
        {
            return;
        }

        await RefreshCloudFriendsAsync();
    }

    private async void PresenceHeartbeatTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isBusy || !HasAuthenticatedCloudSession())
        {
            return;
        }

        await PingPresenceAsync();
    }

    private async void VesperNetStatusTimer_OnTick(object? sender, EventArgs e)
    {
        if (!PlatformService.Features.SupportsVesperNetService)
        {
            return;
        }

        await RefreshVesperNetStatusAsync();

        if (_activeRelaySession is not null &&
            _activeRelayLanPort is int relayLanPort &&
            relayLanPort > 0)
        {
            await PollPendingRelayConnectionsAsync(relayLanPort);
        }
    }

    private void MainWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInSizeMove)
        {
            if (!_resizePerformanceModeActive)
            {
                SuspendResizePerformanceMode();
                ClearWindowRegion();
            }

            UpdateWindowChromeLayout();
            UpdateLauncherSettingsResponsiveLayout();
            UpdateSkinSectionResponsiveLayout();
            UpdateFriendsSectionResponsiveLayout();
            return;
        }

        UpdateWindowClip();
        UpdateLauncherSettingsResponsiveLayout();
        UpdateSkinSectionResponsiveLayout();
        UpdateFriendsSectionResponsiveLayout();
    }

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        _isInSizeMove = false;
        ResumeResizePerformanceMode();
        UpdateWindowClip();
    }

    private void BackgroundRotationTimer_OnTick(object? sender, EventArgs e)
    {
        TryApplyCustomBackgroundImage();
    }

    private async Task LoadVersionsAsync()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            SetBusy(true, "Загружаю список версий...");
            using var versionLoadCts = new CancellationTokenSource(VersionListLoadTimeout);
            _availableVersions = await _launcherService.GetAvailableVersionsAsync(versionLoadCts.Token);
            if (_availableVersions.Count == 0)
            {
                throw new InvalidOperationException("Список версий пуст.");
            }

            RefreshVersionComboBox();
            var lastLaunchedVersionId = _uiState.LastLaunchedVersionId;
            var selected = !string.IsNullOrWhiteSpace(lastLaunchedVersionId) &&
                           SelectVersionByVersionId(lastLaunchedVersionId);
            if (!selected)
            {
                var defaultVersionId = _availableVersions.FirstOrDefault(version =>
                        string.Equals(version.Id, "1.21", StringComparison.Ordinal))
                    ?.Id ?? _availableVersions[0].Id;
                SelectVersionByVersionId(defaultVersionId);
            }
            DownloadProgressBar.Value = 0;
            SetStatus("Версии загружены.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка загрузки версий.");
            ShowError(ex, "Ошибка загрузки");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string? TryResolveBaseMinecraftVersionId(MinecraftVersionEntry? version)
    {
        if (version is null || string.IsNullOrWhiteSpace(version.Id))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(version.BaseVersionId))
        {
            return version.BaseVersionId.Trim();
        }

        var candidate = version.Id.Trim();
        if (candidate.All(character => char.IsDigit(character) || character == '.'))
        {
            return candidate;
        }

        var match = MinecraftVersionRegex.Match(candidate);
        return match.Success ? match.Value : null;
    }

    private static bool VersionMatchesLoader(MinecraftVersionEntry version, ModLoaderKind loaderKind)
    {
        var matchedLoaders = DetectInstalledLoaders(version);
        return matchedLoaders.Count == 1 && matchedLoaders[0] == loaderKind;
    }

    private static IReadOnlyList<ModLoaderKind> GetAutoInstallLoaders(VersionSelectionEntry selectedChoice)
    {
        if (selectedChoice.AutoInstallLoaderKinds is { Count: > 0 } explicitLoaders)
        {
            return explicitLoaders;
        }

        return selectedChoice.AutoInstallLoaderKind is ModLoaderKind loaderKind
            ? [loaderKind]
            : [];
    }

    private static string BuildLoaderCombinationDisplayName(IReadOnlyList<ModLoaderKind> loaders)
    {
        return string.Join("+", loaders.Select(GetLoaderDisplayName));
    }

    private static string BuildAutoInstallChoiceKey(string baseVersionId, IReadOnlyList<ModLoaderKind> loaders)
    {
        var loaderKey = string.Join("+", loaders.Select(loader => loader.ToString()));
        return $"auto:{baseVersionId}:{loaderKey}";
    }

    private MinecraftVersionEntry? TryResolveBaseVanillaVersion(string baseVersionId)
    {
        if (string.IsNullOrWhiteSpace(baseVersionId))
        {
            return null;
        }

        var normalizedBaseVersionId = baseVersionId.Trim();
        var baseChoice = _availableVersionChoices.FirstOrDefault(choice =>
            string.Equals(choice.Key, $"base:{normalizedBaseVersionId}", StringComparison.Ordinal));
        if (baseChoice?.Version is not null)
        {
            return baseChoice.Version;
        }

        return _availableVersions.FirstOrDefault(version =>
                   string.Equals(version.Id, normalizedBaseVersionId, StringComparison.Ordinal))
               ?? _availableVersions.FirstOrDefault(version =>
                   string.Equals(TryResolveBaseMinecraftVersionId(version), normalizedBaseVersionId, StringComparison.Ordinal));
    }

    private static bool LoaderSequencesEqual(IReadOnlyList<ModLoaderKind> left, IReadOnlyList<ModLoaderKind> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool HasInstalledVersionForPrimaryLoader(
        IReadOnlyList<MinecraftVersionEntry> groupVersions,
        ModLoaderKind primaryLoader)
    {
        return groupVersions.Any(version =>
        {
            var displayLoaders = GetDisplayLoadersForVersion(version);
            return displayLoaders.Count > 0 && displayLoaders[0] == primaryLoader;
        });
    }

    private static bool TryGetPrimaryModsLoader(IReadOnlyList<ModLoaderKind> loaders, out ModLoaderKind loaderKind)
    {
        if (loaders.Contains(ModLoaderKind.Fabric))
        {
            loaderKind = ModLoaderKind.Fabric;
            return true;
        }

        if (loaders.Contains(ModLoaderKind.Forge))
        {
            loaderKind = ModLoaderKind.Forge;
            return true;
        }

        loaderKind = default;
        return false;
    }

    private static int CompareMinecraftBaseVersionIds(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(left))
        {
            return -1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return 1;
        }

        var leftParts = ParseMinecraftVersionParts(left);
        var rightParts = ParseMinecraftVersionParts(right);
        var maxCount = Math.Max(leftParts.Count, rightParts.Count);
        for (var index = 0; index < maxCount; index++)
        {
            var leftValue = index < leftParts.Count ? leftParts[index] : 0;
            var rightValue = index < rightParts.Count ? rightParts[index] : 0;
            var compare = leftValue.CompareTo(rightValue);
            if (compare != 0)
            {
                return compare;
            }
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<int> ParseMinecraftVersionParts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        var match = MinecraftVersionRegex.Match(value.Trim());
        if (!match.Success)
        {
            return Array.Empty<int>();
        }

        return match.Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var numericPart) ? numericPart : 0)
            .ToArray();
    }

    private static IReadOnlyList<ModLoaderKind> DetectInstalledLoaders(MinecraftVersionEntry version)
    {
        var fingerprint = $"{version.Id} {version.Type}".ToLowerInvariant();
        var result = new List<ModLoaderKind>(3);
        if (fingerprint.Contains("optifine", StringComparison.Ordinal))
        {
            result.Add(ModLoaderKind.OptiFine);
        }

        if (fingerprint.Contains("forge", StringComparison.Ordinal) &&
            !fingerprint.Contains("neoforge", StringComparison.Ordinal))
        {
            result.Add(ModLoaderKind.Forge);
        }

        if (fingerprint.Contains("fabric", StringComparison.Ordinal))
        {
            result.Add(ModLoaderKind.Fabric);
        }

        return result;
    }

    private static IReadOnlyList<ModLoaderKind> DetectLoadersFromVersionFingerprint(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return Array.Empty<ModLoaderKind>();
        }

        var fingerprint = versionId.Trim().ToLowerInvariant();
        var result = new List<ModLoaderKind>(3);
        if (fingerprint.Contains("optifine", StringComparison.Ordinal))
        {
            result.Add(ModLoaderKind.OptiFine);
        }

        if (fingerprint.Contains("forge", StringComparison.Ordinal) &&
            !fingerprint.Contains("neoforge", StringComparison.Ordinal))
        {
            result.Add(ModLoaderKind.Forge);
        }

        if (fingerprint.Contains("fabric", StringComparison.Ordinal))
        {
            result.Add(ModLoaderKind.Fabric);
        }

        return result;
    }

    private static string? TryExtractBaseMinecraftVersionId(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        var match = MinecraftVersionRegex.Match(versionId.Trim());
        return match.Success ? match.Value : null;
    }

    private IReadOnlyList<ModLoaderKind> GetSelectionEntryLoaders(VersionSelectionEntry choice)
    {
        return choice.Version is not null
            ? GetDisplayLoadersForVersion(choice.Version)
            : GetAutoInstallLoaders(choice);
    }

    private static string GetLoaderDisplayName(ModLoaderKind loaderKind)
    {
        return loaderKind switch
        {
            ModLoaderKind.Forge => "Forge",
            ModLoaderKind.Fabric => "Fabric",
            ModLoaderKind.OptiFine => "OptiFine",
            _ => loaderKind.ToString()
        };
    }

    private static bool TryGetSupportedModsLoader(
        VersionSelectionEntry selectedChoice,
        out ModLoaderKind loaderKind,
        out bool requiresAutoInstall)
    {
        requiresAutoInstall = false;
        if (!string.IsNullOrWhiteSpace(selectedChoice.AvailabilityNote))
        {
            loaderKind = default;
            return false;
        }

        var autoLoaders = GetAutoInstallLoaders(selectedChoice);
        if (TryGetPrimaryModsLoader(autoLoaders, out loaderKind))
        {
            requiresAutoInstall = true;
            return true;
        }

        if (selectedChoice.Version is not null)
        {
            var installedLoaders = DetectInstalledLoaders(selectedChoice.Version);
            if (installedLoaders.Contains(ModLoaderKind.Fabric))
            {
                loaderKind = ModLoaderKind.Fabric;
                return true;
            }

            if (installedLoaders.Contains(ModLoaderKind.Forge))
            {
                loaderKind = ModLoaderKind.Forge;
                return true;
            }
        }

        loaderKind = default;
        return false;
    }

    private static string SanitizeVersionInstanceSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            buffer.Append(invalidChars.Contains(character) ? '_' : character);
        }

        var sanitized = buffer.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private string GetVersionModsDirectoryPath(string versionId)
    {
        var profileRoot = _launcherService.GetGameDirectory(GetSelectedProfile());
        var instanceName = SanitizeVersionInstanceSegment(versionId.Trim());
        return Path.Combine(profileRoot, "instances", instanceName, "mods");
    }

    private static bool IsOptiFineModFileName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
               fileName.Contains("optifine", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains("optifabric", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptiFabricModFileName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
               fileName.Contains("optifabric", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<ModLoaderKind> GetDisplayLoadersForVersion(MinecraftVersionEntry version)
    {
        var matchedLoaders = DetectInstalledLoaders(version);
        var hasOptiFine = matchedLoaders.Contains(ModLoaderKind.OptiFine);
        var hasFabric = matchedLoaders.Contains(ModLoaderKind.Fabric);
        var hasForge = matchedLoaders.Contains(ModLoaderKind.Forge);

        if (string.IsNullOrWhiteSpace(version.Id))
        {
            return matchedLoaders;
        }

        var modsDirectory = GetVersionModsDirectoryPath(version.Id);
        if (!Directory.Exists(modsDirectory))
        {
            if (hasFabric)
            {
                return [ModLoaderKind.Fabric];
            }

            if (hasForge)
            {
                return hasOptiFine
                    ? [ModLoaderKind.Forge, ModLoaderKind.OptiFine]
                    : [ModLoaderKind.Forge];
            }

            return hasOptiFine
                ? [ModLoaderKind.OptiFine]
                : matchedLoaders;
        }

        var installedModFiles = Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).ToArray();
        hasOptiFine = hasOptiFine || installedModFiles.Any(IsOptiFineModFileName);
        if (hasFabric)
        {
            var hasOptiFabric = installedModFiles.Any(IsOptiFabricModFileName);
            return hasOptiFine && hasOptiFabric
                ? [ModLoaderKind.Fabric, ModLoaderKind.OptiFine]
                : [ModLoaderKind.Fabric];
        }

        if (hasForge)
        {
            return hasOptiFine
                ? [ModLoaderKind.Forge, ModLoaderKind.OptiFine]
                : [ModLoaderKind.Forge];
        }

        return hasOptiFine
            ? [ModLoaderKind.OptiFine]
            : matchedLoaders;
    }

    private bool IsAutoInstallChoiceSupported(string baseVersionId, IReadOnlyList<ModLoaderKind> loaders)
    {
        return string.IsNullOrWhiteSpace(GetAutoInstallUnsupportedMessage(baseVersionId, loaders));
    }

    private string? GetAutoInstallAvailabilityNote(string baseVersionId, IReadOnlyList<ModLoaderKind> loaders)
    {
        var unsupportedMessage = GetAutoInstallUnsupportedMessage(baseVersionId, loaders);
        if (string.IsNullOrWhiteSpace(unsupportedMessage))
        {
            return null;
        }

        if (loaders.Count == 1 && loaders[0] == ModLoaderKind.Forge)
        {
            return string.Equals(baseVersionId?.Trim(), "1.17", StringComparison.OrdinalIgnoreCase)
                ? "выбери 1.17.1"
                : "недоступно";
        }

        if (loaders.Count == 2 &&
            loaders[0] == ModLoaderKind.Forge &&
            loaders[1] == ModLoaderKind.OptiFine)
        {
            return "временно отключено";
        }

        return "недоступно";
    }

    private string? GetAutoInstallUnsupportedMessage(string baseVersionId, IReadOnlyList<ModLoaderKind> loaders)
    {
        if (loaders.Count == 0)
        {
            return "Выбран некорректный набор модлоадеров.";
        }

        if (loaders.Count == 1 && loaders[0] == ModLoaderKind.Forge &&
            !_launcherService.IsForgeSupported(baseVersionId))
        {
            return string.Equals(baseVersionId?.Trim(), "1.17", StringComparison.OrdinalIgnoreCase)
                ? "Для версии 1.17 нет доступных сборок Forge. Выбери 1.17.1 или другую версию."
                : $"Для версии {baseVersionId} нет доступных сборок Forge.";
        }

        if (loaders.Count == 2 &&
            loaders[0] == ModLoaderKind.Forge &&
            loaders[1] == ModLoaderKind.OptiFine)
        {
            if (!_launcherService.IsForgeSupported(baseVersionId))
            {
                return string.Equals(baseVersionId?.Trim(), "1.17", StringComparison.OrdinalIgnoreCase)
                    ? "Для версии 1.17 нет доступных сборок Forge. Выбери 1.17.1 или другую версию."
                    : $"Для версии {baseVersionId} нет доступных сборок Forge.";
            }

            if (!_launcherService.IsForgeOptiFineSupported(baseVersionId))
            {
                return $"Для версии {baseVersionId} связка Forge+OptiFine временно отключена: эта сборка падает при запуске.";
            }
        }

        if (loaders.Count == 2 &&
            loaders[0] == ModLoaderKind.Fabric &&
            loaders[1] == ModLoaderKind.OptiFine &&
            !_launcherService.IsFabricOptiFineSupported(baseVersionId))
        {
            return $"Для версии {baseVersionId} связка Fabric+OptiFine недоступна: нет совместимого OptiFabric.";
        }

        return null;
    }

    private string BuildAutoInstallDisplayName(string baseVersionId, IReadOnlyList<ModLoaderKind> loaders)
    {
        var displayName = $"{baseVersionId} - {BuildLoaderCombinationDisplayName(loaders)}";
        var availabilityNote = GetAutoInstallAvailabilityNote(baseVersionId, loaders);
        return string.IsNullOrWhiteSpace(availabilityNote)
            ? displayName
            : $"{displayName} ({availabilityNote})";
    }

    private string BuildInstalledVersionDisplayName(string baseVersionId, MinecraftVersionEntry version)
    {
        var matchedLoaders = GetDisplayLoadersForVersion(version);
        if (matchedLoaders.Count == 0)
        {
            var fallbackLabel = BuildUnknownInstalledVersionDisplayName(baseVersionId, version);
            return $"{baseVersionId} - {fallbackLabel}";
        }

        var loaderLabel = BuildLoaderCombinationDisplayName(matchedLoaders);
        return $"{baseVersionId} - {loaderLabel}";
    }

    private static string BuildUnknownInstalledVersionDisplayName(string baseVersionId, MinecraftVersionEntry version)
    {
        var versionId = version.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return string.IsNullOrWhiteSpace(version.Type) ? "дополнительно" : version.Type;
        }

        if (versionId.StartsWith(baseVersionId + "-", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = versionId[(baseVersionId.Length + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                return suffix;
            }
        }

        return versionId;
    }

    private IReadOnlyList<VersionSelectionEntry> BuildVersionSelectionEntries()
    {
        var displayProfiles = new IReadOnlyList<ModLoaderKind>[]
        {
            [ModLoaderKind.OptiFine],
            [ModLoaderKind.Forge],
            [ModLoaderKind.Forge, ModLoaderKind.OptiFine],
            [ModLoaderKind.Fabric],
            [ModLoaderKind.Fabric, ModLoaderKind.OptiFine]
        };

        var groupedByBase = _availableVersions
            .Select(version => new
            {
                Version = version,
                BaseVersionId = TryResolveBaseMinecraftVersionId(version) ?? version.Id
            })
            .GroupBy(item => item.BaseVersionId, StringComparer.Ordinal)
            .OrderByDescending(group => group.Key, MinecraftBaseVersionComparer)
            .ThenByDescending(group => group.Max(item => item.Version.ReleaseTime))
            .ToList();

        var result = new List<VersionSelectionEntry>(groupedByBase.Count * 4);

        foreach (var group in groupedByBase)
        {
            var baseVersionId = group.Key;
            var groupVersions = group
                .Select(item => item.Version)
                .OrderByDescending(version => version.ReleaseTime)
                .ToList();

            var vanillaVersion = groupVersions.FirstOrDefault(version =>
                                    string.Equals(version.Id, baseVersionId, StringComparison.Ordinal))
                                ?? groupVersions[0];

            result.Add(new VersionSelectionEntry(
                Key: $"base:{baseVersionId}",
                DisplayName: $"{baseVersionId} - базовая",
                BaseVersionId: baseVersionId,
                Version: vanillaVersion,
                AutoInstallLoaderKind: null));

            var consumedVersionIds = new HashSet<string>(StringComparer.Ordinal)
            {
                vanillaVersion.Id
            };

            foreach (var displayLoaders in displayProfiles)
            {
                var installedVersion = groupVersions.FirstOrDefault(version =>
                    !consumedVersionIds.Contains(version.Id) &&
                    LoaderSequencesEqual(GetDisplayLoadersForVersion(version), displayLoaders));

                if (installedVersion is not null)
                {
                    consumedVersionIds.Add(installedVersion.Id);
                    result.Add(new VersionSelectionEntry(
                        Key: $"installed:{installedVersion.Id}",
                        DisplayName: BuildInstalledVersionDisplayName(baseVersionId, installedVersion),
                        BaseVersionId: baseVersionId,
                        Version: installedVersion,
                        AutoInstallLoaderKind: null));
                    continue;
                }

                var primaryLoader = displayLoaders[0];
                if (displayLoaders.Count == 1 &&
                    HasInstalledVersionForPrimaryLoader(groupVersions, primaryLoader))
                {
                    continue;
                }

                var availabilityNote = GetAutoInstallAvailabilityNote(baseVersionId, displayLoaders);
                if (!string.IsNullOrWhiteSpace(availabilityNote))
                {
                    continue;
                }

                result.Add(new VersionSelectionEntry(
                    Key: BuildAutoInstallChoiceKey(baseVersionId, displayLoaders),
                    DisplayName: BuildAutoInstallDisplayName(baseVersionId, displayLoaders),
                    BaseVersionId: baseVersionId,
                    Version: null,
                    AutoInstallLoaderKind: primaryLoader,
                    AutoInstallLoaderKinds: displayLoaders,
                    AvailabilityNote: availabilityNote));
            }

            foreach (var version in groupVersions.Where(version => !consumedVersionIds.Contains(version.Id)))
            {
                if (GetDisplayLoadersForVersion(version).Count > 0)
                {
                    continue;
                }

                result.Add(new VersionSelectionEntry(
                    Key: $"installed:{version.Id}",
                    DisplayName: BuildInstalledVersionDisplayName(baseVersionId, version),
                    BaseVersionId: baseVersionId,
                    Version: version,
                    AutoInstallLoaderKind: null));
            }
        }

        return result;
    }

    private async Task<MinecraftVersionEntry> ResolveLaunchVersionAsync(
        VersionSelectionEntry selectedChoice,
        IProgress<LauncherProgress> progress)
    {
        IReadOnlyList<ModLoaderKind> autoLoaders;
        string? sourceFabricModsVersionId = null;
        string? requiredFabricLoaderVersion = null;
        if (selectedChoice.Version is not null)
        {
            var installedLoaders = GetDisplayLoadersForVersion(selectedChoice.Version);
            if (installedLoaders.Count == 2 &&
                installedLoaders[0] == ModLoaderKind.Fabric &&
                installedLoaders[1] == ModLoaderKind.OptiFine)
            {
                SetStatus($"Убираю конфликтующие Fabric-моды для {selectedChoice.BaseVersionId}...");
                _launcherService.EnsureFabricOptiFineCompatibleMods(
                    selectedChoice.Version.Id,
                    GetSelectedProfile());

                requiredFabricLoaderVersion = _launcherService.GetRequiredMinimumFabricLoaderVersionForInstalledMods(
                    selectedChoice.Version.Id,
                    GetSelectedProfile());

                var hasOptiFineRuntimeSupport = _launcherService.DoesInstalledFabricVersionSupportOptiFineRuntime(
                    selectedChoice.Version.Id,
                    GetSelectedProfile());
                var satisfiesInstalledMods = _launcherService.DoesInstalledFabricVersionSatisfyInstalledMods(
                    selectedChoice.Version.Id,
                    GetSelectedProfile());
                if (hasOptiFineRuntimeSupport && satisfiesInstalledMods)
                {
                    return selectedChoice.Version;
                }

                sourceFabricModsVersionId = selectedChoice.Version.Id;
                autoLoaders = installedLoaders.ToArray();
                if (!satisfiesInstalledMods && !string.IsNullOrWhiteSpace(requiredFabricLoaderVersion))
                {
                    SetStatus(
                        $"Обновляю Fabric для {selectedChoice.BaseVersionId}: модам нужен loader не ниже {requiredFabricLoaderVersion}.");
                }
                else
                {
                    SetStatus($"Подбираю совместимый Fabric для OptiFine в {selectedChoice.BaseVersionId}...");
                }
            }
            else if (installedLoaders.Count == 2 &&
                     installedLoaders[0] == ModLoaderKind.Forge &&
                     installedLoaders[1] == ModLoaderKind.OptiFine)
            {
                var installedOptiFineVersions = await _launcherService.GetAvailableModLoaderVersionsAsync(
                    selectedChoice.BaseVersionId,
                    ModLoaderKind.OptiFine);
                var preferredInstalledOptiFineVersion = _launcherService.SelectPreferredOptiFineVersion(
                    installedOptiFineVersions,
                    ModLoaderKind.Forge);
                if (preferredInstalledOptiFineVersion is null)
                {
                    SetStatus($"Для Forge {selectedChoice.BaseVersionId} нет рабочего OptiFine. Убираю его и запускаю Forge без него...");
                    _launcherService.RemoveInstalledOptiFineMods(
                        selectedChoice.Version.Id,
                        GetSelectedProfile());

                    _availableVersions = await _launcherService.GetAvailableVersionsAsync();
                    RefreshVersionComboBox();
                    if (SelectVersionByVersionId(selectedChoice.Version.Id, allowFallback: false) &&
                        _selectedVersion is not null)
                    {
                        return _selectedVersion;
                    }

                    return selectedChoice.Version;
                }

                return selectedChoice.Version;
            }
            else
            {
                return selectedChoice.Version;
            }
        }
        else
        {
            autoLoaders = GetAutoInstallLoaders(selectedChoice);
            if (autoLoaders.Count == 0)
            {
                throw new InvalidOperationException("Выбран некорректный пункт версии.");
            }
        }

        var unsupportedMessage = GetAutoInstallUnsupportedMessage(selectedChoice.BaseVersionId, autoLoaders);
        if (!string.IsNullOrWhiteSpace(unsupportedMessage))
        {
            throw new InvalidOperationException(unsupportedMessage);
        }

        var loaderKind = autoLoaders[0];
        var loaderLabel = BuildLoaderCombinationDisplayName(autoLoaders);

        SetStatus($"Подбираю {loaderLabel} для {selectedChoice.BaseVersionId}...");
        var loaderVersions = await _launcherService.GetAvailableModLoaderVersionsAsync(
            selectedChoice.BaseVersionId,
            loaderKind);
        var latestLoaderVersion =
            loaderKind == ModLoaderKind.Fabric && autoLoaders.Contains(ModLoaderKind.OptiFine)
                ? await _launcherService.GetCompatibleFabricLoaderVersionForOptiFineAsync(
                    selectedChoice.BaseVersionId,
                    loaderVersions,
                    requiredFabricLoaderVersion)
                : loaderVersions.FirstOrDefault();
        if (latestLoaderVersion is null)
        {
            if (loaderKind == ModLoaderKind.OptiFine)
            {
                var fallbackBaseVersion = TryResolveBaseVanillaVersion(selectedChoice.BaseVersionId);
                if (fallbackBaseVersion is not null)
                {
                    SetStatus($"Для версии {selectedChoice.BaseVersionId} OptiFine ещё не вышел. Запускаю базовую версию без него.");
                    SelectVersionByVersionId(fallbackBaseVersion.Id, allowFallback: false);
                    return fallbackBaseVersion;
                }
            }

            throw new InvalidOperationException(
                loaderKind == ModLoaderKind.Fabric && autoLoaders.Contains(ModLoaderKind.OptiFine)
                    ? !string.IsNullOrWhiteSpace(requiredFabricLoaderVersion)
                        ? $"Для версии {selectedChoice.BaseVersionId} не нашлось совместимой сборки Fabric для OptiFine и модов с loader не ниже {requiredFabricLoaderVersion}."
                        : $"Для версии {selectedChoice.BaseVersionId} не нашлось совместимой сборки Fabric для OptiFine."
                    : $"Для версии {selectedChoice.BaseVersionId} нет доступных сборок {GetLoaderDisplayName(loaderKind)}.");
        }

        SetStatus($"Устанавливаю {GetLoaderDisplayName(loaderKind)} {latestLoaderVersion.Id}...");
        var installResult = await _launcherService.InstallModLoaderAsync(
            selectedChoice.BaseVersionId,
            loaderKind,
            latestLoaderVersion.Id,
            GetSelectedProfile(),
            progress);

        var installedVersionId = installResult.InstalledVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(installedVersionId))
        {
            throw new InvalidOperationException("Установщик вернул пустой идентификатор версии.");
        }

        if (!string.IsNullOrWhiteSpace(sourceFabricModsVersionId) &&
            !string.Equals(sourceFabricModsVersionId, installedVersionId, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Переношу моды в новую Fabric-сборку {installedVersionId}...");
            _launcherService.CopyInstalledMods(
                sourceFabricModsVersionId,
                installedVersionId,
                GetSelectedProfile());
        }

        if (autoLoaders.Contains(ModLoaderKind.OptiFine) &&
            loaderKind is ModLoaderKind.Forge or ModLoaderKind.Fabric)
        {
            SetStatus($"Подбираю OptiFine для {selectedChoice.BaseVersionId}...");
            var optiFineVersions = await _launcherService.GetAvailableModLoaderVersionsAsync(
                selectedChoice.BaseVersionId,
                ModLoaderKind.OptiFine);
            var latestOptiFineVersion = _launcherService.SelectPreferredOptiFineVersion(
                optiFineVersions,
                loaderKind);
            if (latestOptiFineVersion is null)
            {
                SetStatus(
                    optiFineVersions.Count > 0 && loaderKind == ModLoaderKind.Forge
                        ? $"Для версии {selectedChoice.BaseVersionId} у OptiFine пока только preview-сборки. Запускаю {GetLoaderDisplayName(loaderKind)} без него."
                        : $"Для версии {selectedChoice.BaseVersionId} OptiFine пока не вышел. Запускаю {GetLoaderDisplayName(loaderKind)} без него.");
            }
            else
            {
                SetStatus($"Добавляю OptiFine {latestOptiFineVersion.Id}...");
                await _launcherService.InstallOptiFineModAsync(
                    selectedChoice.BaseVersionId,
                    installedVersionId,
                    latestOptiFineVersion.Id,
                    GetSelectedProfile(),
                    progress);

                if (loaderKind == ModLoaderKind.Fabric)
                {
                    SetStatus($"Добавляю OptiFabric для {selectedChoice.BaseVersionId}...");
                    await _launcherService.InstallOptiFabricModAsync(
                        selectedChoice.BaseVersionId,
                        installedVersionId,
                        GetSelectedProfile(),
                        progress);
                }
            }
        }

        _availableVersions = await _launcherService.GetAvailableVersionsAsync();
        RefreshVersionComboBox();

        var isInstalledVersionSelected = SelectVersionByVersionId(installedVersionId, allowFallback: false);
        if (!isInstalledVersionSelected ||
            _selectedVersion is null ||
            !string.Equals(_selectedVersion.Id, installedVersionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Установлена версия {installedVersionId}, но выбрать её не удалось. " +
                "Запуск остановлен, чтобы не стартовала другая версия.");
        }

        return _selectedVersion;
    }

    private async void LaunchButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_gameProcessMonitor.IsRunning)
        {
            try
            {
                await _gameProcessMonitor.StopAsync(TimeSpan.FromSeconds(5));
                SetStatus("Игра принудительно закрыта.");
            }
            catch (Exception ex)
            {
                ShowError(ex, "Ошибка при закрытии игры");
            }
            return;
        }

        if (_isBusy)
        {
            return;
        }

        if (!HasLaunchIdentity())
        {
            ShowSidePanelSection(SidePanelSection.Account);
            SetStatus(HasRegisteredAccount()
                ? "Сессия истекла. Войди снова."
                : "Сначала войди или зарегистрируйся.");

            if (App.IsPhotinoBackendHost)
            {
                ApplyAccountUiState();
            }
            else
            {
                MessageBox.Show(
                    this,
                    "Перед запуском нужно войти или зарегистрироваться.",
                    "Нет аккаунта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            return;
        }

        if (_selectedVersionChoice is null)
        {
            MessageBox.Show(this, "Выбери версию Minecraft.", "Нет версии", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var enteredUsername = (GetLaunchNickname() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(enteredUsername))
        {
            MessageBox.Show(this, "Укажи ник.", "Нет ника", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var normalizedUsername = NormalizeMinecraftUsername(enteredUsername);
        if (!enteredUsername.Equals(normalizedUsername, StringComparison.Ordinal))
        {
            if (HasIncognitoIdentity() && !HasAuthenticatedCloudSession())
            {
                _guestIdentityState = new GuestIdentityState
                {
                    Username = normalizedUsername,
                    CreatedAtUtc = _guestIdentityState?.CreatedAtUtc ?? DateTimeOffset.UtcNow.ToString("O")
                };
                SaveGuestIdentityState();
            }

            UsernameTextBox.Text = normalizedUsername;
            SetStatus("Ник изменен на допустимый формат (A-Z, a-z, 0-9, _, 3-16 символов).");
        }

        if (!UsernameRegex.IsMatch(normalizedUsername))
        {
            MessageBox.Show(
                this,
                "Ник должен быть 3-16 символов и содержать только A-Z, a-z, 0-9 или _.",
                "Некорректный ник",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AddOrPromoteSavedUsername(normalizedUsername);

        try
        {
            var directConnectServerAddress = _pendingDirectConnectServerAddress;
            var directConnectServerPort = _pendingDirectConnectServerPort;
            var directConnectLabel = _pendingDirectConnectLabel;
            _pendingDirectConnectServerAddress = null;
            _pendingDirectConnectServerPort = null;
            _pendingDirectConnectLabel = null;

            SetBusy(true, "Подготовка запуска...");
            ButtonProgressBar.Value = 0;

            var progress = new Progress<LauncherProgress>(UpdateProgress);
            var launchVersion = await ResolveLaunchVersionAsync(_selectedVersionChoice, progress);
            _selectedVersion = launchVersion;
            string? precomputedUserPropertiesJson = null;
            string? offlineSkinSessionUuid = null;
            VesperAuthSession? vesperAuthSession = null;

            if (!string.IsNullOrWhiteSpace(_selectedSkinFilePath))
            {
                SetStatus("Готовлю скин...");
                var skinLaunchData = await _launcherService.PrepareOfflineSkinUserPropertiesAsync(
                    _selectedSkinFilePath,
                    _selectedSkinIsSlim,
                    normalizedUsername,
                    _accountState?.AccessToken,
                    launchVersion.Id);
                precomputedUserPropertiesJson = skinLaunchData?.UserPropertiesJson;
                offlineSkinSessionUuid = skinLaunchData?.SessionUuid;
                vesperAuthSession = skinLaunchData?.VesperAuthSession;
            }

            var launchOptions = new LaunchOptions
            {
                Username = normalizedUsername,
                JavaExecutable = ResolveEffectiveJavaExecutable(),
                Version = launchVersion,
                Profile = GetSelectedProfile(),
                MemoryMb = ResolveEffectiveLaunchMemoryMb(),
                ExtraJvmArgs = ResolveStoredExtraJvmArgs(),
                MinecraftLanguageCode = ResolveSelectedMinecraftLanguageCode(),
                SelectedSkinPath = _selectedSkinFilePath,
                SelectedSkinIsSlim = _selectedSkinIsSlim,
                PrecomputedUserPropertiesJson = precomputedUserPropertiesJson,
                OfflineSkinSessionUuid = offlineSkinSessionUuid,
                VesperAuthSession = vesperAuthSession,
                DirectConnectServerAddress = directConnectServerAddress,
                DirectConnectServerPort = directConnectServerPort
            };

            var result = await _launcherService.DownloadAndLaunchAsync(launchOptions, progress);
            
            if (result.RequiresLauncherRestart)
            {
                UpdateLastLaunchedVersion(launchVersion.Id);
                SetStatus(result.RestartMessage ?? "Перезапуск лаунчера...");
                ProgressLabelTextBlock.Text = "Перезапуск лаунчера после первой загрузки версии...";
                await Task.Delay(700);
                RestartLauncher();
                return;
            }

            if (result.ProcessId.HasValue)
            {
                try
                {
                    _gameProcess = Process.GetProcessById(result.ProcessId.Value);
                    _runningGameInstanceDirectory = result.GameDirectory;
                    _runningGameVersionId = launchVersion.Id;
                    _gameProcessMonitor.Attach(_gameProcess);
                }
                catch { /* process might have exited already */ }
            }

            ApplyPostLaunchWindowBehavior();
            UpdateLastLaunchedVersion(launchVersion.Id);
            var pidText = result.ProcessId.HasValue ? result.ProcessId.Value.ToString() : "unknown";
            SetStatus(string.IsNullOrWhiteSpace(directConnectServerAddress)
                ? $"Игра запущена. PID: {pidText}"
                : $"Подключение к {(directConnectLabel ?? $"{directConnectServerAddress}:{directConnectServerPort}")}. PID: {pidText}");
            ProgressLabelTextBlock.Text = $"Папка: {result.GameDirectory}";
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка запуска.");
            ShowError(ex, "Ошибка запуска");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateProgress(LauncherProgress progress)
    {
        if (progress.Total > 0)
        {
            var percent = progress.Current / progress.Total * 100.0;
            var clampedPercent = Math.Clamp(percent, 0, 100);
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = clampedPercent;
            ButtonProgressBar.Value = clampedPercent;
            var overlayText = clampedPercent switch
            {
                >= 99.5 => "100%",
                > 0 and < 10 => $"{clampedPercent:0.#}%",
                _ => $"{Math.Round(clampedPercent):0}%"
            };
            UpdateDownloadProgressOverlayText(overlayText);
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressBar.Value = 0;
            ButtonProgressBar.Value = 0;
            UpdateDownloadProgressOverlayText(string.Empty);
        }

        ProgressLabelTextBlock.Text = progress.Stage;
        SetStatus(progress.Stage);
    }

    private LauncherProfile GetSelectedProfile()
    {
        return LauncherProfile.Vanilla;
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        _isBusy = isBusy;
        var hasAccount = HasAuthenticatedCloudSession();
        var hasGuestIdentity = HasIncognitoIdentity() && !hasAccount;
        // LaunchButton is handled separately to allow "Close" functionality.
        VersionComboBox.IsEnabled = !isBusy;
        UsernameTextBox.IsEnabled = !isBusy && hasAccount;
        MemorySlider.IsEnabled = !isBusy;
        MemoryPreset4GbButton.IsEnabled = !isBusy;
        MemoryPreset6GbButton.IsEnabled = !isBusy;
        MemoryPreset8GbButton.IsEnabled = !isBusy;
        SettingsToggleButton.IsEnabled = !isBusy;
        InlineVersionBorder.IsEnabled = !isBusy;
        OpenProfileFolderButton.IsEnabled = !isBusy;
        QuickVersionListBox.IsEnabled = !isBusy;
        UseSystemJavaToggleButton.IsEnabled = !isBusy;
        ShowJvmArgsToggleButton.IsEnabled = !isBusy;
        AutoMinimizeLauncherToggleButton.IsEnabled = !isBusy;
        LauncherClickSoundToggleButton.IsEnabled = !isBusy;
        SavedUsernamesButton.IsEnabled = !isBusy;
        AddUsernameButton.IsEnabled = false;
        RemoveUsernameButton.IsEnabled = false;
        SavedUsernamesListBox.IsEnabled = false;
        if (AccountRecentUsernamesListBox is not null)
        {
            AccountRecentUsernamesListBox.IsEnabled = !isBusy && _savedUsernames.Count > 0;
        }
        SkinButton.IsEnabled = !isBusy;
        BackgroundButton.IsEnabled = !isBusy;
        ModsButton.IsEnabled = !isBusy;
        FriendsButton.IsEnabled = !isBusy && CanAccessFriendsFeature();
        InstallSelectedModsButton.IsEnabled = !isBusy && RecommendedModsListBox.SelectedItems.Count > 0;
        RefreshModCatalogButton.IsEnabled = !isBusy;
        ClearSelectedModsButton.IsEnabled = !isBusy && RecommendedModsListBox.SelectedItems.Count > 0;
        RecommendedModsListBox.IsEnabled = !isBusy;
        ModsSearchTextBox.IsEnabled = !isBusy;
        FriendNotificationsButton.IsEnabled = !isBusy && CanAccessFriendsFeature();
        FriendNicknameTextBox.IsEnabled = !isBusy && CanManageCloudFriends();
        FriendsListBox.IsEnabled = !isBusy && CanManageCloudFriends();
        IncomingFriendRequestsListBox.IsEnabled = !isBusy && CanManageCloudFriends();
        AcceptFriendRequestButton.IsEnabled = !isBusy &&
                                              CanManageCloudFriends() &&
                                              IncomingFriendRequestsListBox.SelectedItem is not null;
        DeclineFriendRequestButton.IsEnabled = !isBusy &&
                                               CanManageCloudFriends() &&
                                               IncomingFriendRequestsListBox.SelectedItem is not null;
        AccountNicknameTextBox.IsEnabled = !isBusy && !hasAccount;
        AccountPasswordBox.IsEnabled = !isBusy && !hasAccount;
        LoginAccountButton.IsEnabled = !isBusy && !hasAccount;
        CreateAccountButton.IsEnabled = !isBusy && !hasAccount;
        EnterIncognitoButton.IsEnabled = false;
        AccountAuthPrimaryButton.IsEnabled = !isBusy && !hasAccount;
        ChangeAvatarButton.IsEnabled = !isBusy && (hasAccount || hasGuestIdentity);
        LogoutAccountButton.IsEnabled = !isBusy && (hasAccount || hasGuestIdentity);

        if (isBusy)
        {
            FriendNotificationsPopup.IsOpen = false;
            ButtonProgressBar.Visibility = Visibility.Visible;
            LaunchButtonText.Text = "Запуск...";
            if (DownloadProgressBar.Value <= 0)
            {
                DownloadProgressBar.IsIndeterminate = true;
                UpdateDownloadProgressOverlayText(string.Empty);
            }
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            ButtonProgressBar.Visibility = Visibility.Collapsed;
            ButtonProgressBar.Value = 0;
            UpdateLaunchButtonIdleState();
            UpdateDownloadProgressOverlayText(string.Empty);
        }

        RefreshFriendsAccessUi();
        ApplyAccountEntryModeUi();
        if (isBusy && VersionPickerPopup is not null)
        {
            VersionPickerPopup.IsOpen = false;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatus(status);
        }

        ApplyLauncherSettingsUiState();
    }

    private void SetStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    private void UpdateDownloadProgressOverlayText(string text)
    {
        if (DownloadProgressOverlayTextBlock is not null)
        {
            DownloadProgressOverlayTextBlock.Text = text;
        }
    }

    private void RestartLauncher()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        try
        {
            var restartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            };

            Process.Start(restartInfo);
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private void MemorySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateMemoryPresentation();
        PersistLauncherSettingsFromControls();
    }

    private void MemoryPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string presetValue } ||
            !int.TryParse(presetValue, out var memoryMb))
        {
            return;
        }

        MemorySlider.Value = NormalizeMemoryMb(memoryMb);
        PersistLauncherSettingsFromControls();
        SetStatus($"Профиль памяти применён: {(int)MemorySlider.Value} MB.");
    }

    private void UpdateMemoryPresetButtonStates()
    {
        if (MemorySlider is null)
        {
            return;
        }

        var selectedMemoryMb = ResolveDisplayedMemoryMb();
        ApplyMemoryPresetButtonState(MemoryPreset4GbButton, selectedMemoryMb == 4096);
        ApplyMemoryPresetButtonState(MemoryPreset6GbButton, selectedMemoryMb == 6144);
        ApplyMemoryPresetButtonState(MemoryPreset8GbButton, selectedMemoryMb == 8192);
    }

    private void UpdateMemoryPresentation()
    {
        if (MemoryValueTextBlock is not null)
        {
            MemoryValueTextBlock.Text = $"{ResolveDisplayedMemoryMb()} MB";
        }

        UpdateMemoryPresetButtonStates();
    }

    private static void ApplyMemoryPresetButtonState(ToggleButton? presetButton, bool isSelected)
    {
        if (presetButton is null)
        {
            return;
        }

        presetButton.IsChecked = isSelected;
    }

    private void LauncherSettingsTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        PersistLauncherSettingsFromControls();
    }

    private void MinecraftLanguageComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingLauncherSettingsState)
        {
            return;
        }

        PersistLauncherSettingsFromControls();
    }

    private void InitializeMinecraftLanguageComboBox()
    {
        if (MinecraftLanguageComboBox is null)
        {
            return;
        }

        MinecraftLanguageComboBox.ItemsSource = MinecraftLanguageOptions.ToList();
    }

    private void InitializeLauncherLoginFormPlacementComboBox()
    {
        if (LauncherLoginFormPlacementComboBox is null)
        {
            return;
        }

        LauncherLoginFormPlacementComboBox.ItemsSource = LoginFormPlacementOptions.ToList();
    }

    private void InitializeLauncherDirectoryViewComboBox()
    {
        if (LauncherDirectoryViewModeComboBox is null)
        {
            return;
        }

        LauncherDirectoryViewModeComboBox.ItemsSource = LauncherDirectoryViewOptions.ToList();
    }

    private void InitializeLauncherJavaRuntimeComboBox()
    {
        if (LauncherJavaRuntimeComboBox is null)
        {
            return;
        }

        LauncherJavaRuntimeComboBox.ItemsSource = LauncherJavaRuntimeOptions.ToList();
    }

    private void LauncherLoginFormPlacementComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingLauncherSettingsState)
        {
            return;
        }

        ApplyAccountShellPlacement();
        PersistLauncherSettingsFromControls();
    }

    private void LauncherDirectoryViewModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingLauncherSettingsState)
        {
            return;
        }

        UpdateLauncherOverviewPresentation();
        PersistLauncherSettingsFromControls();
    }

    private void LauncherJavaRuntimeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingLauncherSettingsState ||
            LauncherJavaRuntimeComboBox?.SelectedItem is not LauncherJavaRuntimeOption selectedRuntime)
        {
            return;
        }

        if (UseSystemJavaToggleButton is not null)
        {
            UseSystemJavaToggleButton.IsChecked = selectedRuntime.UseSystemJava;
        }

        ApplyLauncherSettingsUiState();
        PersistLauncherSettingsFromControls();
    }

    private void LauncherJavaSetupButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowLauncherSettingsTab(LauncherSettingsTabJava);
        SetStatus("Открыт раздел Java.");
    }

    private void LauncherOpenGameDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var targetDirectory = ResolveDisplayedLauncherGameDirectory();
        try
        {
            Directory.CreateDirectory(targetDirectory);
            OpenFolderInExplorer(targetDirectory, $"Открыта папка: {targetDirectory}");
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка открытия папки");
        }
    }

    private void LauncherAboutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
        var versionText = string.IsNullOrWhiteSpace(executablePath)
            ? "dev"
            : FileVersionInfo.GetVersionInfo(executablePath).ProductVersion ?? "dev";
        var message =
            $"Vesper Launcher{Environment.NewLine}" +
            $"Версия: {versionText}{Environment.NewLine}" +
            $"Профиль: {GetSelectedProfile()}{Environment.NewLine}" +
            $"Папка игры: {ResolveDisplayedLauncherGameDirectory()}{Environment.NewLine}" +
            $"Данные лаунчера: {GetPreferredLauncherDataDirectory()}";
        MessageBox.Show(this, message, "О программе", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LauncherSettingsTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tabId })
        {
            return;
        }

        ShowLauncherSettingsTab(tabId);
    }

    private void ShowLauncherSettingsTab(string? tabId)
    {
        var normalizedTabId = string.Equals(tabId, LauncherSettingsTabLauncher, StringComparison.OrdinalIgnoreCase)
            ? LauncherSettingsTabLauncher
            : string.Equals(tabId, LauncherSettingsTabJava, StringComparison.OrdinalIgnoreCase)
            ? LauncherSettingsTabJava
            : string.Equals(tabId, LauncherSettingsTabVesper, StringComparison.OrdinalIgnoreCase)
                ? LauncherSettingsTabVesper
                : string.Equals(tabId, LauncherSettingsTabGlass, StringComparison.OrdinalIgnoreCase)
                    ? LauncherSettingsTabGlass
                : string.Equals(tabId, LauncherSettingsTabLanguage, StringComparison.OrdinalIgnoreCase)
                    ? LauncherSettingsTabLanguage
                : LauncherSettingsTabLaunch;

        _activeLauncherSettingsTabId = normalizedTabId;

        if (LauncherSettingsLauncherTabButton is not null)
        {
            LauncherSettingsLauncherTabButton.IsChecked = normalizedTabId == LauncherSettingsTabLauncher;
        }

        if (LauncherSettingsJavaTabButton is not null)
        {
            LauncherSettingsJavaTabButton.IsChecked = normalizedTabId == LauncherSettingsTabJava;
        }

        if (LauncherSettingsVesperTabButton is not null)
        {
            LauncherSettingsVesperTabButton.IsChecked = normalizedTabId == LauncherSettingsTabVesper;
        }

        if (LauncherSettingsLaunchTabButton is not null)
        {
            LauncherSettingsLaunchTabButton.IsChecked = normalizedTabId == LauncherSettingsTabLaunch;
        }

        if (LauncherSettingsLanguageTabButton is not null)
        {
            LauncherSettingsLanguageTabButton.IsChecked = normalizedTabId == LauncherSettingsTabLanguage;
        }

        if (LauncherJavaCardBorder is not null)
        {
            LauncherJavaCardBorder.Visibility = normalizedTabId == LauncherSettingsTabJava
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (LauncherOverviewCardBorder is not null)
        {
            LauncherOverviewCardBorder.Visibility = normalizedTabId == LauncherSettingsTabLauncher
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (LauncherExclusiveCardBorder is not null)
        {
            LauncherExclusiveCardBorder.Visibility = normalizedTabId == LauncherSettingsTabVesper
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (LauncherPerformanceCardBorder is not null)
        {
            LauncherPerformanceCardBorder.Visibility = normalizedTabId == LauncherSettingsTabLaunch
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (LauncherLanguageCardBorder is not null)
        {
            LauncherLanguageCardBorder.Visibility = normalizedTabId == LauncherSettingsTabLanguage
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        UpdateLauncherOverviewPresentation();
    }

    private void LauncherSettingsToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender == UseSystemJavaToggleButton &&
            UseSystemJavaToggleButton.IsChecked == true &&
            string.IsNullOrWhiteSpace(JavaPathTextBox.Text))
        {
            JavaPathTextBox.Text = "javaw";
        }

        ApplyLauncherSettingsUiState();
        PersistLauncherSettingsFromControls();

        if (sender == AutoMinimizeLauncherToggleButton)
        {
            SetStatus(AutoMinimizeLauncherToggleButton.IsChecked == true
                ? "Лаунчер будет сворачиваться после запуска игры."
                : "Лаунчер останется открытым после запуска игры.");
        }
        else if (sender == AutoOptimizeMemoryToggleButton)
        {
            SetStatus(AutoOptimizeMemoryToggleButton.IsChecked == true
                ? $"Авто память включена: {ResolveDisplayedMemoryMb()} MB."
                : "Авто память отключена.");
        }
        else if (sender == RestoreLauncherAfterGameExitToggleButton)
        {
            SetStatus(RestoreLauncherAfterGameExitToggleButton.IsChecked == true
                ? "После выхода из игры окно лаунчера будет возвращаться."
                : "После выхода из игры лаунчер останется свёрнутым.");
        }
        else if (sender == LauncherClickSoundToggleButton)
        {
            SetStatus(LauncherClickSoundToggleButton.IsChecked == true
                ? "Звуки интерфейса включены."
                : "Звуки интерфейса отключены.");
        }
    }

    private void ApplyLauncherSettingsUiState()
    {
        var useSystemJava = UseSystemJavaToggleButton?.IsChecked == true;
        var showJvmArgs = ShowJvmArgsToggleButton?.IsChecked == true;
        var autoOptimizeMemory = IsAutoMemoryOptimizationEnabled();
        var autoMinimizeOnLaunch = AutoMinimizeLauncherToggleButton?.IsChecked == true;
        var controlsEnabled = !_isBusy;

        if (JavaPathTextBox is not null)
        {
            JavaPathTextBox.IsEnabled = controlsEnabled && !useSystemJava;
            JavaPathTextBox.Opacity = useSystemJava ? 0.72 : 1d;
        }

        if (JavaModeHintTextBlock is not null)
        {
            JavaModeHintTextBlock.Text = useSystemJava
                ? "javaw из системы."
                : "Свой путь к javaw.";
        }

        if (UseSystemJavaToggleButton is not null)
        {
            UseSystemJavaToggleButton.IsEnabled = controlsEnabled;
        }

        if (ShowJvmArgsToggleButton is not null)
        {
            ShowJvmArgsToggleButton.IsEnabled = controlsEnabled;
        }

        if (AutoOptimizeMemoryToggleButton is not null)
        {
            AutoOptimizeMemoryToggleButton.IsEnabled = controlsEnabled;
        }

        if (JvmArgsCardBorder is not null)
        {
            JvmArgsCardBorder.Visibility = showJvmArgs ? Visibility.Visible : Visibility.Collapsed;
            JvmArgsCardBorder.Opacity = controlsEnabled ? 1d : 0.76d;
        }

        if (ExtraJvmArgsTextBox is not null)
        {
            ExtraJvmArgsTextBox.IsEnabled = controlsEnabled && showJvmArgs;
        }

        if (JvmArgsHintTextBlock is not null)
        {
            JvmArgsHintTextBlock.Text = showJvmArgs
                ? "Поле активно."
                : "Поле выключено.";
        }

        if (MemorySlider is not null)
        {
            MemorySlider.IsEnabled = controlsEnabled && !autoOptimizeMemory;
            MemorySlider.Opacity = autoOptimizeMemory ? 0.58d : 1d;
        }

        if (MemoryPreset4GbButton is not null)
        {
            MemoryPreset4GbButton.IsEnabled = controlsEnabled && !autoOptimizeMemory;
            MemoryPreset4GbButton.Opacity = autoOptimizeMemory ? 0.58d : 1d;
        }

        if (MemoryPreset6GbButton is not null)
        {
            MemoryPreset6GbButton.IsEnabled = controlsEnabled && !autoOptimizeMemory;
            MemoryPreset6GbButton.Opacity = autoOptimizeMemory ? 0.58d : 1d;
        }

        if (MemoryPreset8GbButton is not null)
        {
            MemoryPreset8GbButton.IsEnabled = controlsEnabled && !autoOptimizeMemory;
            MemoryPreset8GbButton.Opacity = autoOptimizeMemory ? 0.58d : 1d;
        }

        if (AutoMemoryHintTextBlock is not null)
        {
            AutoMemoryHintTextBlock.Text = BuildAutoMemoryHintText();
        }

        if (AutoMinimizeLauncherToggleButton is not null)
        {
            AutoMinimizeLauncherToggleButton.IsEnabled = controlsEnabled;
        }

        if (RestoreLauncherAfterGameExitToggleButton is not null)
        {
            RestoreLauncherAfterGameExitToggleButton.IsEnabled = controlsEnabled && autoMinimizeOnLaunch;
            RestoreLauncherAfterGameExitToggleButton.Opacity = autoMinimizeOnLaunch ? 1d : 0.56d;
        }

        if (AutoMinimizeLauncherHintTextBlock is not null)
        {
            AutoMinimizeLauncherHintTextBlock.Text = autoMinimizeOnLaunch
                ? "Окно свернётся."
                : "Окно останется открытым.";
        }

        if (RestoreLauncherAfterGameExitHintTextBlock is not null)
        {
            RestoreLauncherAfterGameExitHintTextBlock.Text = autoMinimizeOnLaunch
                ? "Вернётся после выхода."
                : "Доступно при сворачивании.";
        }

        if (LauncherClickSoundToggleButton is not null)
        {
            LauncherClickSoundToggleButton.IsEnabled = controlsEnabled;
        }

        if (MinecraftLanguageComboBox is not null)
        {
            MinecraftLanguageComboBox.IsEnabled = controlsEnabled;
        }

        if (LauncherLoginFormPlacementComboBox is not null)
        {
            LauncherLoginFormPlacementComboBox.IsEnabled = controlsEnabled;
        }

        if (LauncherDirectoryViewModeComboBox is not null)
        {
            LauncherDirectoryViewModeComboBox.IsEnabled = controlsEnabled;
        }

        if (LauncherJavaRuntimeComboBox is not null)
        {
            LauncherJavaRuntimeComboBox.IsEnabled = controlsEnabled;
        }

        if (LauncherJavaSetupButton is not null)
        {
            LauncherJavaSetupButton.IsEnabled = controlsEnabled;
        }

        if (LauncherOpenGameDirectoryButton is not null)
        {
            LauncherOpenGameDirectoryButton.IsEnabled = true;
        }

        if (LauncherAboutButton is not null)
        {
            LauncherAboutButton.IsEnabled = true;
        }

        UpdateLauncherOverviewPresentation();
        ApplyAccountShellPlacement();
        UpdateMemoryPresentation();
    }

    private void ApplyPersistedLauncherSettingsToControls()
    {
        _isApplyingLauncherSettingsState = true;
        try
        {
            if (JavaPathTextBox is not null)
            {
                JavaPathTextBox.Text = NormalizeJavaExecutablePath(_uiState.JavaExecutablePath);
            }

            if (UseSystemJavaToggleButton is not null)
            {
                UseSystemJavaToggleButton.IsChecked = _uiState.UseSystemJava;
            }

            if (MemorySlider is not null)
            {
                MemorySlider.Value = NormalizeMemoryMb(_uiState.MemoryMb);
            }

            if (ExtraJvmArgsTextBox is not null)
            {
                ExtraJvmArgsTextBox.Text = NormalizeExtraJvmArgs(_uiState.ExtraJvmArgs);
            }

            if (ShowJvmArgsToggleButton is not null)
            {
                ShowJvmArgsToggleButton.IsChecked = _uiState.ShowJvmArgs || !string.IsNullOrWhiteSpace(_uiState.ExtraJvmArgs);
            }

            if (AutoOptimizeMemoryToggleButton is not null)
            {
                AutoOptimizeMemoryToggleButton.IsChecked = _uiState.AutoOptimizeMemory ?? true;
            }

            if (AutoMinimizeLauncherToggleButton is not null)
            {
                AutoMinimizeLauncherToggleButton.IsChecked = _uiState.AutoMinimizeOnLaunch;
            }

            if (RestoreLauncherAfterGameExitToggleButton is not null)
            {
                RestoreLauncherAfterGameExitToggleButton.IsChecked = _uiState.RestoreLauncherAfterGameExit;
            }

            if (LauncherClickSoundToggleButton is not null)
            {
                LauncherClickSoundToggleButton.IsChecked = _uiState.ClickSoundEnabled;
            }

            if (MinecraftLanguageComboBox is not null)
            {
                MinecraftLanguageComboBox.SelectedItem = ResolveMinecraftLanguageOption(_uiState.MinecraftLanguageCode);
            }

            if (LauncherLoginFormPlacementComboBox is not null)
            {
                LauncherLoginFormPlacementComboBox.SelectedItem = ResolveLoginFormPlacementOption(_uiState.LoginFormPlacementId);
            }

            if (LauncherDirectoryViewModeComboBox is not null)
            {
                LauncherDirectoryViewModeComboBox.SelectedItem = ResolveLauncherDirectoryViewOption(_uiState.LauncherDirectoryViewId);
            }

            if (LauncherJavaRuntimeComboBox is not null)
            {
                LauncherJavaRuntimeComboBox.SelectedItem = ResolveLauncherJavaRuntimeOption(_uiState.UseSystemJava);
            }

            UpdateLauncherOverviewPresentation();
            ApplyAccountShellPlacement();
            UpdateMemoryPresentation();
        }
        finally
        {
            _isApplyingLauncherSettingsState = false;
        }
    }

    private void PersistLauncherSettingsFromControls()
    {
        if (_isApplyingLauncherSettingsState ||
            JavaPathTextBox is null ||
            MemorySlider is null ||
            ExtraJvmArgsTextBox is null)
        {
            return;
        }

        _uiState = BuildLauncherUiState(
            selectedSkinFileName: _uiState.SelectedSkinFileName,
            backgroundPresetId: _uiState.BackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: _uiState.LastLaunchedVersionId);
        SaveUiState();
    }

    private LauncherUiState BuildLauncherUiState(
        string? selectedSkinFileName,
        string? backgroundPresetId,
        string? skinModelPreferenceId,
        string? lastLaunchedVersionId)
    {
        return new LauncherUiState
        {
            SelectedSkinFileName = selectedSkinFileName,
            BackgroundPresetId = NormalizeBackgroundPresetId(backgroundPresetId),
            SkinModelPreferenceId = NormalizeSkinModelPreferenceId(skinModelPreferenceId),
            LastLaunchedVersionId = lastLaunchedVersionId,
            UseSystemJava = ResolveUseSystemJavaPreference(),
            JavaExecutablePath = ResolveStoredJavaExecutablePath(),
            MemoryMb = ResolveStoredMemoryMb(),
            ExtraJvmArgs = ResolveStoredExtraJvmArgs(),
            ShowJvmArgs = ResolveShowJvmArgsPreference(),
            AutoOptimizeMemory = ResolveAutoOptimizeMemoryPreference(),
            MinecraftLanguageCode = ResolveSelectedMinecraftLanguageCode(),
            LoginFormPlacementId = ResolveSelectedLoginFormPlacementId(),
            LauncherDirectoryViewId = ResolveSelectedLauncherDirectoryViewId(),
            AutoMinimizeOnLaunch = IsAutoMinimizeLauncherEnabled(),
            RestoreLauncherAfterGameExit = IsRestoreLauncherAfterGameExitEnabled(),
            ClickSoundEnabled = IsLauncherClickSoundEnabled()
        };
    }

    private bool ResolveUseSystemJavaPreference()
    {
        return UseSystemJavaToggleButton?.IsChecked ?? _uiState.UseSystemJava;
    }

    private string ResolveStoredJavaExecutablePath()
    {
        if (JavaPathTextBox is not null)
        {
            return NormalizeJavaExecutablePath(JavaPathTextBox.Text);
        }

        return NormalizeJavaExecutablePath(_uiState.JavaExecutablePath);
    }

    private string ResolveEffectiveJavaExecutable()
    {
        return ResolveUseSystemJavaPreference()
            ? DefaultJavaExecutable
            : ResolveStoredJavaExecutablePath();
    }

    private int ResolveStoredMemoryMb()
    {
        if (MemorySlider is not null)
        {
            return NormalizeMemoryMb((int)Math.Round(MemorySlider.Value));
        }

        return NormalizeMemoryMb(_uiState.MemoryMb);
    }

    private string ResolveStoredExtraJvmArgs()
    {
        if (ExtraJvmArgsTextBox is not null)
        {
            return NormalizeExtraJvmArgs(ExtraJvmArgsTextBox.Text);
        }

        return NormalizeExtraJvmArgs(_uiState.ExtraJvmArgs);
    }

    private bool ResolveShowJvmArgsPreference()
    {
        if (ShowJvmArgsToggleButton?.IsChecked is bool isChecked)
        {
            return isChecked;
        }

        return _uiState.ShowJvmArgs || !string.IsNullOrWhiteSpace(_uiState.ExtraJvmArgs);
    }

    private bool ResolveAutoOptimizeMemoryPreference()
    {
        return AutoOptimizeMemoryToggleButton?.IsChecked ?? (_uiState.AutoOptimizeMemory ?? true);
    }

    private string ResolveSelectedMinecraftLanguageCode()
    {
        if (MinecraftLanguageComboBox?.SelectedItem is LauncherLanguageOption option)
        {
            return NormalizeMinecraftLanguageCode(option.Id);
        }

        return NormalizeMinecraftLanguageCode(_uiState.MinecraftLanguageCode);
    }

    private string ResolveSelectedLoginFormPlacementId()
    {
        if (LauncherLoginFormPlacementComboBox?.SelectedItem is LoginFormPlacementOption option)
        {
            return NormalizeLoginFormPlacementId(option.Id);
        }

        return NormalizeLoginFormPlacementId(_uiState.LoginFormPlacementId);
    }

    private string ResolveSelectedLauncherDirectoryViewId()
    {
        if (LauncherDirectoryViewModeComboBox?.SelectedItem is LauncherDirectoryViewOption option)
        {
            return NormalizeLauncherDirectoryViewId(option.Id);
        }

        return NormalizeLauncherDirectoryViewId(_uiState.LauncherDirectoryViewId);
    }

    private bool IsAutoMinimizeLauncherEnabled()
    {
        return AutoMinimizeLauncherToggleButton?.IsChecked ?? _uiState.AutoMinimizeOnLaunch;
    }

    private bool IsRestoreLauncherAfterGameExitEnabled()
    {
        return RestoreLauncherAfterGameExitToggleButton?.IsChecked ?? _uiState.RestoreLauncherAfterGameExit;
    }

    private bool IsLauncherClickSoundEnabled()
    {
        return LauncherClickSoundToggleButton?.IsChecked ?? _uiState.ClickSoundEnabled;
    }

    private bool IsAutoMemoryOptimizationEnabled()
    {
        return AutoOptimizeMemoryToggleButton?.IsChecked ?? (_uiState.AutoOptimizeMemory ?? true);
    }

    private int ResolveDisplayedMemoryMb()
    {
        return IsAutoMemoryOptimizationEnabled()
            ? ResolveAutoOptimizedMemoryMb()
            : ResolveStoredMemoryMb();
    }

    private int ResolveEffectiveLaunchMemoryMb()
    {
        return ResolveDisplayedMemoryMb();
    }

    private int ResolveAutoOptimizedMemoryMb()
    {
        var installedMemoryMb = TryGetInstalledSystemMemoryMb();
        return CalculateRecommendedMemoryMb(installedMemoryMb);
    }

    private string BuildAutoMemoryHintText()
    {
        if (!IsAutoMemoryOptimizationEnabled())
        {
            return "Ручная память включена.";
        }

        var installedMemoryMb = TryGetInstalledSystemMemoryMb();
        var optimizedMemoryMb = CalculateRecommendedMemoryMb(installedMemoryMb);
        if (!installedMemoryMb.HasValue)
        {
            return $"Авто выбрано: {optimizedMemoryMb} MB.";
        }

        var installedMemoryGb = Math.Max(1, (int)Math.Round(installedMemoryMb.Value / 1024d));
        return $"Подбор по {installedMemoryGb} GB RAM: {optimizedMemoryMb} MB.";
    }

    private LauncherLanguageOption ResolveMinecraftLanguageOption(string? languageCode)
    {
        var normalizedLanguageCode = NormalizeMinecraftLanguageCode(languageCode);
        return MinecraftLanguageOptions.FirstOrDefault(option =>
                   string.Equals(option.Id, normalizedLanguageCode, StringComparison.OrdinalIgnoreCase))
               ?? MinecraftLanguageOptions[0];
    }

    private LoginFormPlacementOption ResolveLoginFormPlacementOption(string? placementId)
    {
        var normalizedPlacementId = NormalizeLoginFormPlacementId(placementId);
        return LoginFormPlacementOptions.FirstOrDefault(option =>
                   string.Equals(option.Id, normalizedPlacementId, StringComparison.OrdinalIgnoreCase))
               ?? LoginFormPlacementOptions[1];
    }

    private LauncherDirectoryViewOption ResolveLauncherDirectoryViewOption(string? viewId)
    {
        var normalizedViewId = NormalizeLauncherDirectoryViewId(viewId);
        return LauncherDirectoryViewOptions.FirstOrDefault(option =>
                   string.Equals(option.Id, normalizedViewId, StringComparison.OrdinalIgnoreCase))
               ?? LauncherDirectoryViewOptions[0];
    }

    private static LauncherJavaRuntimeOption ResolveLauncherJavaRuntimeOption(bool useSystemJava)
    {
        return LauncherJavaRuntimeOptions.First(option => option.UseSystemJava == useSystemJava);
    }

    private static string NormalizeJavaExecutablePath(string? javaExecutablePath)
    {
        var trimmedPath = javaExecutablePath?.Trim();
        return string.IsNullOrWhiteSpace(trimmedPath)
            ? DefaultJavaExecutable
            : trimmedPath;
    }

    private static string NormalizeMinecraftLanguageCode(string? languageCode)
    {
        var normalizedCode = languageCode?.Trim().ToLowerInvariant();
        return normalizedCode switch
        {
            null or "" => AutomaticMinecraftLanguageCode,
            "ru" => "ru_ru",
            "en" => "en_us",
            AutomaticMinecraftLanguageCode => AutomaticMinecraftLanguageCode,
            "ru_ru" => "ru_ru",
            "en_us" => "en_us",
            _ => AutomaticMinecraftLanguageCode
        };
    }

    private static string NormalizeLoginFormPlacementId(string? placementId)
    {
        var normalizedId = placementId?.Trim().ToLowerInvariant();
        return normalizedId switch
        {
            LoginFormPlacementLeftId => LoginFormPlacementLeftId,
            LoginFormPlacementRightId => LoginFormPlacementRightId,
            _ => DefaultLoginFormPlacementId
        };
    }

    private static string NormalizeLauncherDirectoryViewId(string? viewId)
    {
        var normalizedId = viewId?.Trim().ToLowerInvariant();
        return normalizedId switch
        {
            LauncherDirectoryViewSharedId => LauncherDirectoryViewSharedId,
            _ => DefaultLauncherDirectoryViewId
        };
    }

    private static string NormalizeExtraJvmArgs(string? extraJvmArgs)
    {
        return extraJvmArgs?.Trim() ?? string.Empty;
    }

    private static int NormalizeMemoryMb(int memoryMb)
    {
        return (int)Math.Clamp(memoryMb, MinimumLauncherMemoryMb, MaximumLauncherMemoryMb);
    }

    private static int CalculateRecommendedMemoryMb(int? installedMemoryMb)
    {
        if (!installedMemoryMb.HasValue || installedMemoryMb.Value <= 0)
        {
            return DefaultMemoryMb;
        }

        var totalMb = installedMemoryMb.Value;
        var recommendedMb = totalMb switch
        {
            <= 4096 => 2048,
            <= 6144 => 3072,
            <= 8192 => 4096,
            <= 12288 => 5120,
            <= 16384 => 6144,
            <= 24576 => 8192,
            _ => 10240
        };

        return NormalizeMemoryMb(RoundMemoryMbToStep(recommendedMb));
    }

    private static int RoundMemoryMbToStep(int memoryMb)
    {
        const int memoryStepMb = 512;
        var roundedMb = (int)Math.Round(memoryMb / (double)memoryStepMb, MidpointRounding.AwayFromZero) * memoryStepMb;
        return Math.Max(MinimumLauncherMemoryMb, roundedMb);
    }

    private static int? TryGetInstalledSystemMemoryMb()
    {
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out var totalKilobytes) && totalKilobytes > 0)
            {
                var totalMb = totalKilobytes / 1024UL;
                return totalMb > int.MaxValue ? int.MaxValue : (int)totalMb;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private void ApplyPostLaunchWindowBehavior()
    {
        if (!IsAutoMinimizeLauncherEnabled())
        {
            return;
        }

        WindowState = WindowState.Minimized;
    }

    private void RestoreLauncherAfterGameExit()
    {
        if (!IsAutoMinimizeLauncherEnabled() ||
            !IsRestoreLauncherAfterGameExitEnabled())
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }


    private void RefreshVersionComboBox()
    {
        _availableVersionChoices = BuildVersionSelectionEntries();
        _isRefreshingVersionSelection = true;
        _isRefreshingQuickVersionSelection = true;
        try
        {
            VersionComboBox.ItemsSource = null;
            VersionComboBox.ItemsSource = _availableVersionChoices.ToList();

            QuickVersionListBox.ItemsSource = null;
            QuickVersionListBox.ItemsSource = _availableVersionChoices.ToList();

            if (_selectedVersionChoice is null)
            {
                VersionComboBox.SelectedIndex = -1;
                QuickVersionListBox.SelectedIndex = -1;
            }
        }
        finally
        {
            _isRefreshingVersionSelection = false;
            _isRefreshingQuickVersionSelection = false;
        }
    }

    private void UpdateVersionSelectionLabels(VersionSelectionEntry? selectedChoice)
    {
        if (selectedChoice is null)
        {
            InlineVersionLabel.Text = "Версия: не выбрана";
            QuickVersionHintTextBlock.Text = "Выбери версию";
            return;
        }

        InlineVersionLabel.Text = $"Версия: {selectedChoice.DisplayName.Trim()}";
        if (selectedChoice.Version is not null)
        {
            ProgressLabelTextBlock.Text = $"Выбрана версия: {selectedChoice.DisplayName}";
            QuickVersionHintTextBlock.Text = $"Подпись: {selectedChoice.DisplayName}";
        }
        else
        {
            var autoLoaders = GetAutoInstallLoaders(selectedChoice);
            var loaderName = autoLoaders.Count > 0
                ? BuildLoaderCombinationDisplayName(autoLoaders)
                : "неизвестно";
            if (!string.IsNullOrWhiteSpace(selectedChoice.AvailabilityNote))
            {
                ProgressLabelTextBlock.Text =
                    $"Выбрано: {selectedChoice.BaseVersionId} + {loaderName} ({selectedChoice.AvailabilityNote})";
                QuickVersionHintTextBlock.Text =
                    $"Подпись: {selectedChoice.BaseVersionId} + {loaderName} ({selectedChoice.AvailabilityNote})";
            }
            else
            {
                ProgressLabelTextBlock.Text =
                    $"Выбрано: {selectedChoice.BaseVersionId} + {loaderName}";
                QuickVersionHintTextBlock.Text = $"Подпись: {selectedChoice.BaseVersionId} + {loaderName}";
            }
        }
    }

    private void ApplySelectedVersionChoice(
        VersionSelectionEntry selectedChoice,
        bool syncSettingsCombo,
        bool syncQuickList)
    {
        _selectedVersionChoice = selectedChoice;
        _selectedVersion = selectedChoice.Version;

        if (syncSettingsCombo)
        {
            _isRefreshingVersionSelection = true;
            try
            {
                VersionComboBox.SelectedItem = selectedChoice;
            }
            finally
            {
                _isRefreshingVersionSelection = false;
            }
        }

        if (syncQuickList)
        {
            _isRefreshingQuickVersionSelection = true;
            try
            {
                QuickVersionListBox.SelectedItem = selectedChoice;
            }
            finally
            {
                _isRefreshingQuickVersionSelection = false;
            }
        }

        UpdateVersionSelectionLabels(selectedChoice);
        UpdateLaunchButtonIdleState();
        UpdateProfilePathLabel();
        RefreshModsSection();
    }

    private bool SelectVersionByVersionId(string versionId, bool allowFallback = true)
    {
        if (_availableVersionChoices.Count == 0)
        {
            _selectedVersionChoice = null;
            _selectedVersion = null;
            _isRefreshingVersionSelection = true;
            _isRefreshingQuickVersionSelection = true;
            try
            {
                VersionComboBox.SelectedIndex = -1;
                QuickVersionListBox.SelectedIndex = -1;
            }
            finally
            {
                _isRefreshingVersionSelection = false;
                _isRefreshingQuickVersionSelection = false;
            }

            UpdateVersionSelectionLabels(null);
            return false;
        }

        var normalizedVersionId = versionId?.Trim() ?? string.Empty;
        VersionSelectionEntry? matchedChoice = _availableVersionChoices.FirstOrDefault(choice =>
            choice.Version is not null &&
            string.Equals(choice.Version.Id, normalizedVersionId, StringComparison.Ordinal));

        if (matchedChoice is null)
        {
            var sourceVersion = _availableVersions.FirstOrDefault(version =>
                string.Equals(version.Id, normalizedVersionId, StringComparison.Ordinal));
            if (sourceVersion is not null)
            {
                var sourceBaseVersionId = TryResolveBaseMinecraftVersionId(sourceVersion) ?? sourceVersion.Id;
                var sourceDisplayLoaders = GetDisplayLoadersForVersion(sourceVersion);
                if (sourceDisplayLoaders.Count > 0)
                {
                    matchedChoice = _availableVersionChoices.FirstOrDefault(choice =>
                        choice.Version is not null &&
                        string.Equals(choice.BaseVersionId, sourceBaseVersionId, StringComparison.Ordinal) &&
                        LoaderSequencesEqual(GetDisplayLoadersForVersion(choice.Version), sourceDisplayLoaders));
                }
            }
        }

        if (matchedChoice is null)
        {
            matchedChoice = _availableVersionChoices.FirstOrDefault(choice =>
                string.Equals(choice.Key, $"base:{normalizedVersionId}", StringComparison.Ordinal));
        }

        if (matchedChoice is null && allowFallback)
        {
            matchedChoice = _availableVersionChoices.FirstOrDefault(choice => choice.Version is not null)
                ?? _availableVersionChoices[0];
        }

        if (matchedChoice is null)
        {
            return false;
        }

        ApplySelectedVersionChoice(matchedChoice, syncSettingsCombo: true, syncQuickList: true);
        return true;
    }

    private bool TrySelectFriendLaunchVersion(CloudFriendListItem friend, out string? statusMessage)
    {
        statusMessage = null;
        var preferredVersionId = friend.PreferredVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(preferredVersionId))
        {
            return true;
        }

        if (SelectVersionByVersionId(preferredVersionId, allowFallback: false))
        {
            return true;
        }

        var baseVersionId = TryExtractBaseMinecraftVersionId(preferredVersionId);
        if (string.IsNullOrWhiteSpace(baseVersionId))
        {
            statusMessage = $"Не удалось определить версию мира друга: {preferredVersionId}.";
            return false;
        }

        var preferredLoaders = DetectLoadersFromVersionFingerprint(preferredVersionId);
        VersionSelectionEntry? matchedChoice = null;

        if (preferredLoaders.Count > 0)
        {
            matchedChoice = _availableVersionChoices
                .Where(choice => string.Equals(choice.BaseVersionId, baseVersionId, StringComparison.Ordinal))
                .OrderBy(choice => choice.Version is null ? 1 : 0)
                .FirstOrDefault(choice => LoaderSequencesEqual(GetSelectionEntryLoaders(choice), preferredLoaders));

            if (matchedChoice is null)
            {
                var primaryLoader = preferredLoaders.FirstOrDefault(loader =>
                    loader is ModLoaderKind.Fabric or ModLoaderKind.Forge);
                if (primaryLoader is ModLoaderKind.Fabric or ModLoaderKind.Forge)
                {
                    matchedChoice = _availableVersionChoices
                        .Where(choice => string.Equals(choice.BaseVersionId, baseVersionId, StringComparison.Ordinal))
                        .OrderBy(choice => choice.Version is null ? 1 : 0)
                        .FirstOrDefault(choice =>
                        {
                            var choiceLoaders = GetSelectionEntryLoaders(choice);
                            return choiceLoaders.Count > 0 && choiceLoaders[0] == primaryLoader;
                        });
                }
            }
        }
        else
        {
            matchedChoice = _availableVersionChoices.FirstOrDefault(choice =>
                string.Equals(choice.Key, $"base:{baseVersionId}", StringComparison.Ordinal));
        }

        if (matchedChoice is null &&
            preferredLoaders.Count == 1 &&
            preferredLoaders[0] == ModLoaderKind.OptiFine)
        {
            matchedChoice = _availableVersionChoices.FirstOrDefault(choice =>
                string.Equals(choice.Key, $"base:{baseVersionId}", StringComparison.Ordinal));
        }

        if (matchedChoice is null)
        {
            var loaderText = preferredLoaders.Count > 0
                ? BuildLoaderCombinationDisplayName(preferredLoaders)
                : "Vanilla";
            statusMessage = $"Для подключения нужна версия {baseVersionId} ({loaderText}), но она не найдена у тебя в лаунчере.";
            return false;
        }

        ApplySelectedVersionChoice(matchedChoice, syncSettingsCombo: true, syncQuickList: true);
        return true;
    }

    private bool SelectVersionChoiceByKey(string key, bool allowFallback = true)
    {
        if (_availableVersionChoices.Count == 0)
        {
            return false;
        }

        var normalizedKey = key?.Trim() ?? string.Empty;
        var matchedChoice = _availableVersionChoices.FirstOrDefault(choice =>
            string.Equals(choice.Key, normalizedKey, StringComparison.Ordinal));

        if (matchedChoice is null && allowFallback)
        {
            matchedChoice = _availableVersionChoices.FirstOrDefault(choice => choice.Version is not null)
                ?? _availableVersionChoices[0];
        }

        if (matchedChoice is null)
        {
            return false;
        }

        ApplySelectedVersionChoice(matchedChoice, syncSettingsCombo: true, syncQuickList: true);
        return true;
    }

    private void VersionComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingVersionSelection)
        {
            return;
        }

        if (VersionComboBox.SelectedItem is not VersionSelectionEntry selectedChoice)
        {
            return;
        }

        ApplySelectedVersionChoice(selectedChoice, syncSettingsCombo: false, syncQuickList: true);
    }

    private void QuickVersionListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingQuickVersionSelection)
        {
            return;
        }

        if (QuickVersionListBox.SelectedItem is not VersionSelectionEntry selectedChoice)
        {
            return;
        }

        ApplySelectedVersionChoice(selectedChoice, syncSettingsCombo: true, syncQuickList: false);
        VersionPickerPopup.IsOpen = false;
    }

    private string ResolveCurrentProfileDirectory()
    {
        var profile = GetSelectedProfile();
        var versionId = ResolveCurrentProfileVersionId();
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            return _launcherService.GetVersionInstanceDirectory(profile, versionId);
        }

        return _launcherService.GetGameDirectory(profile);
    }

    private string ResolveDisplayedLauncherGameDirectory()
    {
        return string.Equals(
            ResolveSelectedLauncherDirectoryViewId(),
            LauncherDirectoryViewSharedId,
            StringComparison.OrdinalIgnoreCase)
            ? _launcherService.GetGameDirectory(GetSelectedProfile())
            : ResolveCurrentProfileDirectory();
    }

    private void UpdateLauncherOverviewPresentation()
    {
        var previousApplyingState = _isApplyingLauncherSettingsState;
        _isApplyingLauncherSettingsState = true;
        try
        {
            if (LauncherLoginFormPlacementComboBox is not null)
            {
                var selectedPlacement = ResolveLoginFormPlacementOption(ResolveSelectedLoginFormPlacementId());
                if (!Equals(LauncherLoginFormPlacementComboBox.SelectedItem, selectedPlacement))
                {
                    LauncherLoginFormPlacementComboBox.SelectedItem = selectedPlacement;
                }
            }

            if (LauncherDirectoryViewModeComboBox is not null)
            {
                var selectedView = ResolveLauncherDirectoryViewOption(ResolveSelectedLauncherDirectoryViewId());
                if (!Equals(LauncherDirectoryViewModeComboBox.SelectedItem, selectedView))
                {
                    LauncherDirectoryViewModeComboBox.SelectedItem = selectedView;
                }
            }

            if (LauncherJavaRuntimeComboBox is not null)
            {
                var selectedRuntime = ResolveLauncherJavaRuntimeOption(ResolveUseSystemJavaPreference());
                if (!Equals(LauncherJavaRuntimeComboBox.SelectedItem, selectedRuntime))
                {
                    LauncherJavaRuntimeComboBox.SelectedItem = selectedRuntime;
                }
            }

            if (LauncherGameDirectoryTextBox is not null)
            {
                LauncherGameDirectoryTextBox.Text = ResolveDisplayedLauncherGameDirectory();
            }
        }
        finally
        {
            _isApplyingLauncherSettingsState = previousApplyingState;
        }
    }

    private void ApplyAccountShellPlacement()
    {
        if (AccountShellCardBorder is null)
        {
            return;
        }

        AccountShellCardBorder.HorizontalAlignment = ResolveSelectedLoginFormPlacementId() switch
        {
            LoginFormPlacementLeftId => HorizontalAlignment.Left,
            LoginFormPlacementRightId => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center
        };
    }

    private string? ResolveCurrentProfileVersionId()
    {
        var versionId = _selectedVersionChoice?.Version?.Id ?? _selectedVersion?.Id;
        return string.IsNullOrWhiteSpace(versionId)
            ? _selectedVersionChoice?.BaseVersionId
            : versionId.Trim();
    }

    private void UpdateProfilePathLabel()
    {
        if (ProgressLabelTextBlock is null)
        {
            return;
        }

        var profilePath = ResolveCurrentProfileDirectory();
        var modsPath = Path.Combine(profilePath, "mods");
        var resourcepacksPath = Path.Combine(profilePath, "resourcepacks");
        var shaderpacksPath = Path.Combine(profilePath, "shaderpacks");
        ProgressLabelTextBlock.Text =
            $"Папка профиля: {profilePath}{Environment.NewLine}" +
            $"Моды: {modsPath}{Environment.NewLine}" +
            $"Ресурспаки: {resourcepacksPath}{Environment.NewLine}" +
            $"Шейдеры: {shaderpacksPath}";
        UpdateLauncherOverviewPresentation();
    }

    private void SettingsToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!HasAuthenticatedCloudSession())
        {
            ToggleSidePanelSection(SidePanelSection.Account);
            return;
        }

        ToggleSidePanelSection(SidePanelSection.Settings);
    }

    private void VersionPickerButton_OnClick(object sender, RoutedEventArgs e)
    {
        VersionPickerPopup.IsOpen = !VersionPickerPopup.IsOpen;
        if (VersionPickerPopup.IsOpen)
        {
            QuickVersionListBox.Focus();
            SetStatus("Открыт выбор версии.");
        }
    }

    private void OpenProfileFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var profilePath = ResolveCurrentProfileDirectory();
        OpenFolderInExplorer(profilePath, $"Открыта папка профиля: {profilePath}");
    }

    private void SkinButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSidePanelSection(SidePanelSection.Skin);
    }

    private void CurrentNicknameDisplayButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSidePanelSection(SidePanelSection.Account);
    }

    private void FriendNotificationsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsIncognitoOnlyMode())
        {
            FriendNotificationsPopup.IsOpen = false;
            ShowSidePanelSection(SidePanelSection.Account);
            SetStatus("В режиме инкогнито друзья и уведомления недоступны.");
            return;
        }

        RefreshFriendNotificationsPopup();
        FriendNotificationsPopup.IsOpen = !FriendNotificationsPopup.IsOpen;
        if (!FriendNotificationsPopup.IsOpen)
        {
            return;
        }

        if (!HasRegisteredAccount())
        {
            SetStatus("Открыто меню уведомлений. Войди в аккаунт, чтобы видеть заявки.");
            return;
        }

        if (!HasAuthenticatedCloudSession())
        {
            SetStatus($"Открыто меню уведомлений аккаунта {_accountState!.Username}. Войди снова, чтобы обновить заявки.");
            return;
        }

        SetStatus(_incomingFriendRequests.Count > 0
            ? $"Открыты уведомления аккаунта {_accountState!.Username}: {_incomingFriendRequests.Count}."
            : $"Открыты уведомления аккаунта {_accountState!.Username}.");
    }

    private void FriendNotificationsPopupActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        FriendNotificationsPopup.IsOpen = false;
        if (HasAuthenticatedCloudSession())
        {
            ShowSidePanelSection(SidePanelSection.Friends);
            return;
        }

        ShowSidePanelSection(SidePanelSection.Account);
    }

    private void FriendNotificationAcceptButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CloudIncomingFriendRequestItem request })
        {
            _ = RespondToFriendRequestAsync(request, "accept");
        }
    }

    private void FriendNotificationDeclineButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CloudIncomingFriendRequestItem request })
        {
            _ = RespondToFriendRequestAsync(request, "decline");
        }
    }

    private void ThemesButton_OnClick(object sender, RoutedEventArgs e)
    {
        var themesDirectory = Path.Combine(GetAssetsDirectory(), "Themes");
        OpenFolderInExplorer(themesDirectory, $"Открыта папка тем: {themesDirectory}");
    }

    private void BackgroundButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSidePanelSection(SidePanelSection.Background);
    }

    private void ModsButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleSidePanelSection(SidePanelSection.Mods);
    }

    private void FriendsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsIncognitoOnlyMode())
        {
            ShowSidePanelSection(SidePanelSection.Account);
            SetStatus("В режиме инкогнито друзья недоступны.");
            return;
        }

        ToggleSidePanelSection(SidePanelSection.Friends);
    }

    private void ToggleSidePanelSection(SidePanelSection targetSection)
    {
        if (SettingsPanel.Visibility == Visibility.Visible && _activeSidePanelSection == targetSection)
        {
            HideSidePanel();
            return;
        }

        ShowSidePanelSection(targetSection);
    }

    private bool IsFriendsSectionOpen()
    {
        return SettingsPanel.Visibility == Visibility.Visible &&
               _activeSidePanelSection == SidePanelSection.Friends;
    }

    private void UpdateCloudSyncTimerIntervals()
    {
        _cloudFriendsRefreshTimer.Interval = IsFriendsSectionOpen()
            ? VisibleFriendsRefreshInterval
            : HiddenFriendsRefreshInterval;
        _presenceHeartbeatTimer.Interval = PresenceHeartbeatInterval;
    }

    private void ShowSidePanelSection(SidePanelSection section)
    {
        if (section == SidePanelSection.Friends && IsIncognitoOnlyMode())
        {
            section = SidePanelSection.Account;
        }

        FriendNotificationsPopup.IsOpen = false;
        _activeSidePanelSection = section;
        UpdateCloudSyncTimerIntervals();
        ApplySidePanelWidth(section);
        ApplySidePanelStyle(section);
        ApplySidePanelScrollMode(section);
        SettingsPanel.Visibility = Visibility.Visible;
        AccountSectionPanel.Visibility = section == SidePanelSection.Account ? Visibility.Visible : Visibility.Collapsed;
        SettingsSectionPanel.Visibility = section == SidePanelSection.Settings ? Visibility.Visible : Visibility.Collapsed;
        SkinSectionPanel.Visibility = section == SidePanelSection.Skin ? Visibility.Visible : Visibility.Collapsed;
        BackgroundSectionPanel.Visibility = section == SidePanelSection.Background ? Visibility.Visible : Visibility.Collapsed;
        ModsSectionPanel.Visibility = section == SidePanelSection.Mods ? Visibility.Visible : Visibility.Collapsed;
        FriendsSectionPanel.Visibility = section == SidePanelSection.Friends ? Visibility.Visible : Visibility.Collapsed;

        if (section == SidePanelSection.Account)
        {
            if (!HasAuthenticatedCloudSession() && !HasRegisteredAccount() && !_isEditingIncognitoNickname)
            {
                _accountEntryMode = AccountEntryMode.Login;
            }

            RefreshAccountSection();
            FocusAccountNicknameEditorIfNeeded();
            SetStatus("Открыт раздел аккаунта.");
            return;
        }

        if (section == SidePanelSection.Skin)
        {
            RefreshSkinFiles(_uiState.SelectedSkinFileName);
            Dispatcher.BeginInvoke(UpdateSkinSectionResponsiveLayout, DispatcherPriority.Loaded);
            SetStatus("Открыт раздел скинов.");
            return;
        }

        if (section == SidePanelSection.Background)
        {
            RefreshBackgroundSection();
            SetStatus("Открыт раздел фонов.");
            return;
        }

        if (section == SidePanelSection.Mods)
        {
            BeginOpenModsSection();
            SetStatus("Открыт раздел модов.");
            return;
        }

        if (section == SidePanelSection.Friends)
        {
            RefreshFriendsSection();
            Dispatcher.BeginInvoke(UpdateFriendsSectionResponsiveLayout, DispatcherPriority.Loaded);
            SetStatus("Открыт раздел друзей.");
            return;
        }

        if (section == SidePanelSection.Settings)
        {
            ShowLauncherSettingsTab(_activeLauncherSettingsTabId);
            Dispatcher.BeginInvoke(UpdateLauncherSettingsResponsiveLayout, DispatcherPriority.Loaded);
        }

        SetStatus("Открыты настройки.");
    }

    private void HideSidePanel()
    {
        FriendNotificationsPopup.IsOpen = false;
        _activeSidePanelSection = SidePanelSection.None;
        UpdateCloudSyncTimerIntervals();
        ApplySidePanelWidth(SidePanelSection.None);
        ApplySidePanelStyle(SidePanelSection.None);
        ApplySidePanelScrollMode(SidePanelSection.None);
        SettingsPanel.Visibility = Visibility.Collapsed;
        AccountSectionPanel.Visibility = Visibility.Collapsed;
        SettingsSectionPanel.Visibility = Visibility.Collapsed;
        SkinSectionPanel.Visibility = Visibility.Collapsed;
        BackgroundSectionPanel.Visibility = Visibility.Collapsed;
        ModsSectionPanel.Visibility = Visibility.Collapsed;
        FriendsSectionPanel.Visibility = Visibility.Collapsed;
        SetStatus("Панель закрыта.");
    }

    private void ApplySidePanelWidth(SidePanelSection section)
    {
        if (SettingsPanel is null)
        {
            return;
        }

        SettingsPanel.Width = double.NaN;
        SettingsPanel.MaxWidth = section switch
        {
            SidePanelSection.Account => AccountSettingsPanelWidth,
            SidePanelSection.Settings => LauncherSettingsPanelWidth,
            SidePanelSection.Background => BackgroundSettingsPanelWidth,
            SidePanelSection.Skin => SkinSettingsPanelWidth,
            SidePanelSection.Mods => ModsSettingsPanelWidth,
            SidePanelSection.Friends => FriendsSettingsPanelWidth,
            _ => DefaultSettingsPanelWidth
        };
    }

    private void ApplySidePanelScrollMode(SidePanelSection section)
    {
        if (SettingsScrollViewer is null)
        {
            return;
        }

        var lockPanelScroll = section is SidePanelSection.Mods or SidePanelSection.Skin or SidePanelSection.Account or SidePanelSection.Friends or SidePanelSection.Settings;
        SettingsScrollViewer.VerticalScrollBarVisibility = section switch
        {
            SidePanelSection.Skin => ScrollBarVisibility.Disabled,
            SidePanelSection.Friends => ScrollBarVisibility.Disabled,
            SidePanelSection.Settings => ScrollBarVisibility.Disabled,
            _ when lockPanelScroll => ScrollBarVisibility.Disabled,
            _ => ScrollBarVisibility.Hidden
        };
        SettingsScrollViewer.PanningMode = lockPanelScroll ? PanningMode.None : PanningMode.VerticalOnly;
        if (lockPanelScroll || section == SidePanelSection.None)
        {
            SettingsScrollViewer.ScrollToVerticalOffset(0);
        }
    }

    private void UpdateLauncherSettingsResponsiveLayout()
    {
        if (LauncherSettingsContentGrid is null ||
            LauncherSettingsLeftColumn is null ||
            LauncherSettingsGapColumn is null ||
            LauncherSettingsRightColumn is null ||
            LauncherSettingsTopRow is null ||
            LauncherSettingsMiddleGapRow is null ||
            LauncherSettingsMiddleRow is null ||
            LauncherSettingsBottomGapRow is null ||
            LauncherSettingsBottomRow is null ||
            LauncherSettingsExtraGapRow is null ||
            LauncherSettingsExtraRow is null ||
            LauncherJavaCardBorder is null ||
            LauncherExclusiveCardBorder is null ||
            LauncherLanguageCardBorder is null ||
            LauncherPerformanceCardBorder is null)
        {
            return;
        }

        LauncherSettingsLeftColumn.Width = new GridLength(1d, GridUnitType.Star);
        LauncherSettingsGapColumn.Width = new GridLength(0d);
        LauncherSettingsRightColumn.Width = new GridLength(0d);
        LauncherSettingsTopRow.Height = GridLength.Auto;
        LauncherSettingsMiddleGapRow.Height = new GridLength(0d);
        LauncherSettingsMiddleRow.Height = new GridLength(0d);
        LauncherSettingsBottomGapRow.Height = new GridLength(0d);
        LauncherSettingsBottomRow.Height = new GridLength(0d);
        LauncherSettingsExtraGapRow.Height = new GridLength(0d);
        LauncherSettingsExtraRow.Height = new GridLength(0d);

        Grid.SetRow(LauncherJavaCardBorder, 0);
        Grid.SetColumn(LauncherJavaCardBorder, 0);
        Grid.SetColumnSpan(LauncherJavaCardBorder, 3);

        Grid.SetRow(LauncherExclusiveCardBorder, 0);
        Grid.SetColumn(LauncherExclusiveCardBorder, 0);
        Grid.SetColumnSpan(LauncherExclusiveCardBorder, 3);

        Grid.SetRow(LauncherLanguageCardBorder, 0);
        Grid.SetColumn(LauncherLanguageCardBorder, 0);
        Grid.SetColumnSpan(LauncherLanguageCardBorder, 3);

        Grid.SetRow(LauncherPerformanceCardBorder, 0);
        Grid.SetColumn(LauncherPerformanceCardBorder, 0);
        Grid.SetColumnSpan(LauncherPerformanceCardBorder, 3);
    }

    private void UpdateSkinSectionResponsiveLayout()
    {
        if (SkinResponsiveLayoutGrid is null ||
            SkinPreviewCard is null ||
            SkinControlsCard is null ||
            SkinPreviewContentGrid is null ||
            SkinLayoutPreviewColumn is null ||
            SkinLayoutGapColumn is null ||
            SkinLayoutControlsColumn is null ||
            SkinLayoutTopRow is null ||
            SkinLayoutGapRow is null ||
            SkinLayoutBottomRow is null)
        {
            return;
        }

        var availableWidth = SkinResponsiveLayoutGrid.ActualWidth;
        if (availableWidth <= 0d)
        {
            availableWidth = SettingsPanel?.ActualWidth ?? 0d;
        }

        if (availableWidth <= 0d)
        {
            return;
        }

        var stackedLayout = availableWidth < 470d;
        var availableHeight = SettingsPanel?.ActualHeight ?? ActualHeight;

        if (stackedLayout)
        {
            SkinLayoutPreviewColumn.Width = new GridLength(1d, GridUnitType.Star);
            SkinLayoutGapColumn.Width = new GridLength(0d);
            SkinLayoutControlsColumn.Width = new GridLength(0d);
            SkinLayoutTopRow.Height = GridLength.Auto;
            SkinLayoutGapRow.Height = new GridLength(12d);
            SkinLayoutBottomRow.Height = GridLength.Auto;

            SkinPreviewCard.Width = double.NaN;
            SkinControlsCard.Width = double.NaN;
            SkinPreviewContentGrid.Height = Math.Max(160d, Math.Min(200d, availableHeight - 430d));

            Grid.SetRow(SkinPreviewCard, 0);
            Grid.SetColumn(SkinPreviewCard, 0);
            Grid.SetColumnSpan(SkinPreviewCard, 3);

            Grid.SetRow(SkinControlsCard, 2);
            Grid.SetColumn(SkinControlsCard, 0);
            Grid.SetColumnSpan(SkinControlsCard, 3);
            return;
        }

        SkinLayoutPreviewColumn.Width = new GridLength(1.12d, GridUnitType.Star);
        SkinLayoutGapColumn.Width = new GridLength(14d);
        SkinLayoutControlsColumn.Width = new GridLength(0.88d, GridUnitType.Star);
        SkinLayoutTopRow.Height = GridLength.Auto;
        SkinLayoutGapRow.Height = new GridLength(0d);
        SkinLayoutBottomRow.Height = new GridLength(0d);

        SkinPreviewCard.Width = double.NaN;
        SkinControlsCard.Width = double.NaN;
        SkinPreviewContentGrid.Height = Math.Max(210d, Math.Min(250d, availableHeight - 340d));

        Grid.SetRow(SkinPreviewCard, 0);
        Grid.SetColumn(SkinPreviewCard, 0);
        Grid.SetColumnSpan(SkinPreviewCard, 1);

        Grid.SetRow(SkinControlsCard, 0);
        Grid.SetColumn(SkinControlsCard, 2);
        Grid.SetColumnSpan(SkinControlsCard, 1);
    }

    private void UpdateFriendsSectionResponsiveLayout()
    {
        if (FriendsSectionPanel is null ||
            SettingsPanel is null ||
            SettingsScrollViewer is null ||
            FriendsSectionIntroTextBlock is null ||
            FriendsProfileCardHostBorder is null ||
            AddFriendActionInputColumn is null ||
            AddFriendActionGapColumn is null ||
            AddFriendActionButtonColumn is null ||
            AddFriendActionGapRow is null ||
            AddFriendActionBottomRow is null ||
            FriendsInlineAddFriendBorder is null ||
            FriendsInlineAddFriendTitleTextBlock is null ||
            FriendsInlineAddFriendSubtitleTextBlock is null ||
            AddFriendButton is null ||
            FriendNicknameTextBox is null ||
            FriendsListSectionBorder is null ||
            VesperNetStatusTextBlock is null ||
            IncomingFriendRequestsActionLeftColumn is null ||
            IncomingFriendRequestsActionGapColumn is null ||
            IncomingFriendRequestsActionRightColumn is null ||
            IncomingFriendRequestsActionGapRow is null ||
            IncomingFriendRequestsActionBottomRow is null ||
            IncomingFriendRequestsActionGrid is null ||
            AcceptFriendRequestButton is null ||
            DeclineFriendRequestButton is null ||
            IncomingFriendRequestsSectionBorder is null ||
            FriendsProfileCardAvatarColumn is null ||
            FriendsProfileCardGapColumn is null ||
            FriendsProfileCardInfoColumn is null ||
            FriendsProfileCardActionGapColumn is null ||
            FriendsProfileCardActionColumn is null ||
            FriendsProfileCardGapRow is null ||
            FriendsProfileCardMiddleRow is null ||
            FriendsProfileCardBottomGapRow is null ||
            FriendsProfileCardBottomRow is null ||
            FriendsProfileAvatarBorder is null ||
            FriendsProfileInfoStackPanel is null ||
            FriendsListBox is null ||
            IncomingFriendRequestsListBox is null)
        {
            return;
        }

        var availableWidth = SettingsPanel.ActualWidth;
        if (availableWidth <= 0d)
        {
            availableWidth = FriendsSectionPanel.ActualWidth;
        }

        if (availableWidth <= 0d)
        {
            return;
        }

        var viewportHeight = SettingsScrollViewer.ActualHeight > 0d
            ? Math.Max(0d, SettingsScrollViewer.ActualHeight - 36d)
            : SettingsPanel.ActualHeight > 0d
                ? Math.Max(0d, SettingsPanel.ActualHeight - 36d)
                : 0d;
        var availableHeight = viewportHeight > 0d
            ? viewportHeight
            : ActualHeight;
        var compactWidth = availableWidth < 510d;
        var tightWidth = availableWidth < 440d;
        var addFriendStackWidth = availableWidth < 360d;
        var compactHeight = availableHeight < 760d;
        var veryCompactHeight = availableHeight < 690d;
        var hasIncomingRequests = _incomingFriendRequests.Count > 0;

        if (tightWidth)
        {
            FriendsProfileCardAvatarColumn.Width = new GridLength(1d, GridUnitType.Star);
            FriendsProfileCardGapColumn.Width = new GridLength(0d);
            FriendsProfileCardInfoColumn.Width = new GridLength(0d);
            FriendsProfileCardActionGapColumn.Width = new GridLength(0d);
            FriendsProfileCardActionColumn.Width = new GridLength(0d);
            FriendsProfileCardGapRow.Height = new GridLength(10d);
            FriendsProfileCardMiddleRow.Height = GridLength.Auto;
            FriendsProfileCardBottomGapRow.Height = new GridLength(10d);
            FriendsProfileCardBottomRow.Height = GridLength.Auto;
            FriendsProfileAvatarBorder.Width = 72d;
            FriendsProfileAvatarBorder.Height = 72d;
            Grid.SetRow(FriendsProfileAvatarBorder, 0);
            Grid.SetColumn(FriendsProfileAvatarBorder, 0);
            Grid.SetColumnSpan(FriendsProfileAvatarBorder, 5);
            FriendsProfileAvatarBorder.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(FriendsProfileInfoStackPanel, 2);
            Grid.SetColumn(FriendsProfileInfoStackPanel, 0);
            Grid.SetColumnSpan(FriendsProfileInfoStackPanel, 5);
            Grid.SetRow(FriendsInlineAddFriendBorder, 4);
            Grid.SetColumn(FriendsInlineAddFriendBorder, 0);
            Grid.SetColumnSpan(FriendsInlineAddFriendBorder, 5);
        }
        else
        {
            FriendsProfileCardAvatarColumn.Width = GridLength.Auto;
            FriendsProfileCardGapColumn.Width = new GridLength(14d);
            FriendsProfileCardInfoColumn.Width = new GridLength(1d, GridUnitType.Star);
            FriendsProfileCardActionGapColumn.Width = new GridLength(10d);
            FriendsProfileCardActionColumn.Width = new GridLength(242d);
            FriendsProfileCardGapRow.Height = new GridLength(0d);
            FriendsProfileCardMiddleRow.Height = new GridLength(0d);
            FriendsProfileCardBottomGapRow.Height = new GridLength(0d);
            FriendsProfileCardBottomRow.Height = new GridLength(0d);
            FriendsProfileAvatarBorder.Width = compactHeight ? 68d : 72d;
            FriendsProfileAvatarBorder.Height = compactHeight ? 68d : 72d;
            Grid.SetRow(FriendsProfileAvatarBorder, 0);
            Grid.SetColumn(FriendsProfileAvatarBorder, 0);
            Grid.SetColumnSpan(FriendsProfileAvatarBorder, 1);
            FriendsProfileAvatarBorder.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(FriendsProfileInfoStackPanel, 0);
            Grid.SetColumn(FriendsProfileInfoStackPanel, 2);
            Grid.SetColumnSpan(FriendsProfileInfoStackPanel, 1);
            Grid.SetRow(FriendsInlineAddFriendBorder, 0);
            Grid.SetColumn(FriendsInlineAddFriendBorder, 4);
            Grid.SetColumnSpan(FriendsInlineAddFriendBorder, 1);
        }

        FriendsSectionIntroTextBlock.Visibility = veryCompactHeight ? Visibility.Collapsed : Visibility.Visible;
        FriendsProfileTypeTextBlock.Visibility = veryCompactHeight ? Visibility.Collapsed : Visibility.Visible;
        VesperNetStatusTextBlock.Visibility = veryCompactHeight ? Visibility.Collapsed : Visibility.Visible;
        IncomingFriendRequestsActionGrid.Visibility = hasIncomingRequests ? Visibility.Visible : Visibility.Collapsed;
        FriendsInlineAddFriendTitleTextBlock.Visibility = Visibility.Visible;
        FriendsInlineAddFriendSubtitleTextBlock.Visibility = veryCompactHeight ? Visibility.Collapsed : Visibility.Visible;
        IncomingFriendRequestsSectionBorder.Visibility = hasIncomingRequests ? Visibility.Visible : Visibility.Collapsed;

        FriendsProfileCardHostBorder.Margin = new Thickness(0, compactHeight ? 12d : 16d, 0, 0);
        FriendsListSectionBorder.Margin = new Thickness(0, compactHeight ? 12d : 16d, 0, 0);
        IncomingFriendRequestsSectionBorder.Margin = new Thickness(0, compactHeight ? 12d : 16d, 0, 0);

        FriendsProfileCardHostBorder.Padding = compactHeight ? new Thickness(10d) : new Thickness(12d);
        FriendsListSectionBorder.Padding = compactHeight ? new Thickness(10d) : new Thickness(12d);
        IncomingFriendRequestsSectionBorder.Padding = compactHeight ? new Thickness(10d) : new Thickness(12d);
        FriendsInlineAddFriendBorder.Padding = compactHeight ? new Thickness(7d) : new Thickness(8d);

        FriendNicknameTextBox.Height = compactHeight ? 36d : 40d;
        AddFriendButton.Width = compactHeight ? 36d : 38d;
        AddFriendButton.Height = compactHeight ? 36d : 38d;
        AddFriendButton.FontSize = compactHeight ? 17d : 18d;
        AcceptFriendRequestButton.Height = compactHeight ? 36d : 38d;
        DeclineFriendRequestButton.Height = compactHeight ? 36d : 38d;

        if (addFriendStackWidth)
        {
            AddFriendActionInputColumn.Width = new GridLength(1d, GridUnitType.Star);
            AddFriendActionGapColumn.Width = new GridLength(0d);
            AddFriendActionButtonColumn.Width = new GridLength(0d);
            AddFriendActionGapRow.Height = new GridLength(10d);
            AddFriendActionBottomRow.Height = GridLength.Auto;
            Grid.SetRow(AddFriendButton, 2);
            Grid.SetColumn(AddFriendButton, 0);
            Grid.SetColumnSpan(AddFriendButton, 3);
        }
        else
        {
            AddFriendActionInputColumn.Width = new GridLength(1d, GridUnitType.Star);
            AddFriendActionGapColumn.Width = new GridLength(6d);
            AddFriendActionButtonColumn.Width = new GridLength(compactHeight ? 38d : 40d);
            AddFriendActionGapRow.Height = new GridLength(0d);
            AddFriendActionBottomRow.Height = new GridLength(0d);
            Grid.SetRow(AddFriendButton, 0);
            Grid.SetColumn(AddFriendButton, 2);
            Grid.SetColumnSpan(AddFriendButton, 1);
        }

        if (tightWidth)
        {
            IncomingFriendRequestsActionLeftColumn.Width = new GridLength(1d, GridUnitType.Star);
            IncomingFriendRequestsActionGapColumn.Width = new GridLength(0d);
            IncomingFriendRequestsActionRightColumn.Width = new GridLength(0d);
            IncomingFriendRequestsActionGapRow.Height = new GridLength(10d);
            IncomingFriendRequestsActionBottomRow.Height = GridLength.Auto;
            Grid.SetRow(AcceptFriendRequestButton, 0);
            Grid.SetColumn(AcceptFriendRequestButton, 0);
            Grid.SetColumnSpan(AcceptFriendRequestButton, 3);
            Grid.SetRow(DeclineFriendRequestButton, 2);
            Grid.SetColumn(DeclineFriendRequestButton, 0);
            Grid.SetColumnSpan(DeclineFriendRequestButton, 3);
        }
        else
        {
            IncomingFriendRequestsActionLeftColumn.Width = new GridLength(1d, GridUnitType.Star);
            IncomingFriendRequestsActionGapColumn.Width = new GridLength(10d);
            IncomingFriendRequestsActionRightColumn.Width = new GridLength(1d, GridUnitType.Star);
            IncomingFriendRequestsActionGapRow.Height = new GridLength(0d);
            IncomingFriendRequestsActionBottomRow.Height = new GridLength(0d);
            Grid.SetRow(AcceptFriendRequestButton, 0);
            Grid.SetColumn(AcceptFriendRequestButton, 0);
            Grid.SetColumnSpan(AcceptFriendRequestButton, 1);
            Grid.SetRow(DeclineFriendRequestButton, 0);
            Grid.SetColumn(DeclineFriendRequestButton, 2);
            Grid.SetColumnSpan(DeclineFriendRequestButton, 1);
        }

        var reservedHeight = hasIncomingRequests
            ? (veryCompactHeight ? 330d : compactHeight ? 305d : 270d)
            : (veryCompactHeight ? 245d : compactHeight ? 225d : 200d);
        var totalScrollableSpace = Math.Max(hasIncomingRequests ? 190d : 250d, availableHeight - reservedHeight);
        var incomingTargetHeight = hasIncomingRequests
            ? Math.Min(compactHeight ? 104d : 124d, Math.Max(60d, totalScrollableSpace * 0.24d))
            : 0d;
        var friendsTargetHeight = Math.Max(
            hasIncomingRequests
                ? (compactHeight ? 178d : 228d)
                : (compactHeight ? 228d : 300d),
            totalScrollableSpace - incomingTargetHeight);

        FriendsListBox.MinHeight = hasIncomingRequests
            ? (compactHeight ? 178d : 228d)
            : (compactHeight ? 228d : 300d);
        FriendsListBox.MaxHeight = friendsTargetHeight;
        IncomingFriendRequestsListBox.MinHeight = hasIncomingRequests ? (compactHeight ? 60d : 72d) : 0d;
        IncomingFriendRequestsListBox.MaxHeight = incomingTargetHeight;
        UpdateFriendsProfileAvatarClip();
    }

    private void CacheSettingsPanelDefaults()
    {
        if (_settingsPanelDefaultsCached || SettingsPanel is null)
        {
            return;
        }

        _settingsPanelDefaultBackground = SettingsPanel.Background;
        _settingsPanelDefaultBorderBrush = SettingsPanel.BorderBrush;
        _settingsPanelDefaultsCached = true;
    }

    private void ApplySidePanelStyle(SidePanelSection section)
    {
        if (SettingsPanel is null)
        {
            return;
        }

        CacheSettingsPanelDefaults();
        if (!_settingsPanelDefaultsCached)
        {
            return;
        }

        if (section == SidePanelSection.None)
        {
            SettingsPanel.Background = _settingsPanelDefaultBackground;
            SettingsPanel.BorderBrush = _settingsPanelDefaultBorderBrush;
            return;
        }

        if (LeftControlSurfaceBorder is not null)
        {
            SettingsPanel.Background = LeftControlSurfaceBorder.Background;
        }
        else
        {
            SettingsPanel.Background = _settingsPanelDefaultBackground;
        }

        if (LeftControlSurfaceBorder is not null)
        {
            SettingsPanel.BorderBrush = LeftControlSurfaceBorder.BorderBrush;
        }
        else
        {
            SettingsPanel.BorderBrush = _settingsPanelDefaultBorderBrush;
        }
    }

    private void LoadUiState()
    {
        _uiState = new LauncherUiState
        {
            BackgroundPresetId = DefaultBackgroundPresetId,
            SkinModelPreferenceId = DefaultSkinModelPreferenceId,
            UseSystemJava = true,
            JavaExecutablePath = DefaultJavaExecutable,
            MemoryMb = DefaultMemoryMb,
            AutoOptimizeMemory = true,
            MinecraftLanguageCode = AutomaticMinecraftLanguageCode,
            LoginFormPlacementId = DefaultLoginFormPlacementId,
            LauncherDirectoryViewId = DefaultLauncherDirectoryViewId,
            RestoreLauncherAfterGameExit = true,
            ClickSoundEnabled = true
        };

        if (!File.Exists(_uiStatePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_uiStatePath);
            var loadedState = JsonSerializer.Deserialize<LauncherUiState>(json);
            if (loadedState is null)
            {
                return;
            }

            _uiState = new LauncherUiState
            {
                SelectedSkinFileName = loadedState.SelectedSkinFileName,
                BackgroundPresetId = NormalizeBackgroundPresetId(loadedState.BackgroundPresetId),
                SkinModelPreferenceId = NormalizeSkinModelPreferenceId(loadedState.SkinModelPreferenceId),
                LastLaunchedVersionId = loadedState.LastLaunchedVersionId,
                UseSystemJava = loadedState.UseSystemJava,
                JavaExecutablePath = NormalizeJavaExecutablePath(loadedState.JavaExecutablePath),
                MemoryMb = NormalizeMemoryMb(loadedState.MemoryMb),
                ExtraJvmArgs = NormalizeExtraJvmArgs(loadedState.ExtraJvmArgs),
                ShowJvmArgs = loadedState.ShowJvmArgs || !string.IsNullOrWhiteSpace(loadedState.ExtraJvmArgs),
                AutoOptimizeMemory = loadedState.AutoOptimizeMemory ?? true,
                MinecraftLanguageCode = NormalizeMinecraftLanguageCode(loadedState.MinecraftLanguageCode),
                LoginFormPlacementId = NormalizeLoginFormPlacementId(loadedState.LoginFormPlacementId),
                LauncherDirectoryViewId = NormalizeLauncherDirectoryViewId(loadedState.LauncherDirectoryViewId),
                AutoMinimizeOnLaunch = loadedState.AutoMinimizeOnLaunch,
                RestoreLauncherAfterGameExit = loadedState.RestoreLauncherAfterGameExit,
                ClickSoundEnabled = loadedState.ClickSoundEnabled
            };
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки состояния интерфейса");
        }
    }

    private void SaveUiState()
    {
        try
        {
            var stateDirectory = Path.GetDirectoryName(_uiStatePath);
            if (!string.IsNullOrWhiteSpace(stateDirectory))
            {
                Directory.CreateDirectory(stateDirectory);
            }

            var stateToSave = new LauncherUiState
            {
                SelectedSkinFileName = _uiState.SelectedSkinFileName,
                BackgroundPresetId = NormalizeBackgroundPresetId(_uiState.BackgroundPresetId),
                SkinModelPreferenceId = NormalizeSkinModelPreferenceId(_uiState.SkinModelPreferenceId),
                LastLaunchedVersionId = _uiState.LastLaunchedVersionId,
                UseSystemJava = _uiState.UseSystemJava,
                JavaExecutablePath = NormalizeJavaExecutablePath(_uiState.JavaExecutablePath),
                MemoryMb = NormalizeMemoryMb(_uiState.MemoryMb),
                ExtraJvmArgs = NormalizeExtraJvmArgs(_uiState.ExtraJvmArgs),
                ShowJvmArgs = _uiState.ShowJvmArgs,
                AutoOptimizeMemory = _uiState.AutoOptimizeMemory ?? true,
                MinecraftLanguageCode = NormalizeMinecraftLanguageCode(_uiState.MinecraftLanguageCode),
                LoginFormPlacementId = NormalizeLoginFormPlacementId(_uiState.LoginFormPlacementId),
                LauncherDirectoryViewId = NormalizeLauncherDirectoryViewId(_uiState.LauncherDirectoryViewId),
                AutoMinimizeOnLaunch = _uiState.AutoMinimizeOnLaunch,
                RestoreLauncherAfterGameExit = _uiState.RestoreLauncherAfterGameExit,
                ClickSoundEnabled = _uiState.ClickSoundEnabled
            };

            var json = JsonSerializer.Serialize(stateToSave, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_uiStatePath, json);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка сохранения состояния интерфейса");
        }
    }

    private void UpdateLastLaunchedVersion(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return;
        }

        _uiState = BuildLauncherUiState(
            selectedSkinFileName: _uiState.SelectedSkinFileName,
            backgroundPresetId: _uiState.BackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: versionId.Trim());
        SaveUiState();
    }

    private void InitializeSkinModelComboBox()
    {
        _isRefreshingSkinModelSelection = true;
        try
        {
            SkinModelComboBox.ItemsSource = SkinModelOptions;
            SkinModelComboBox.DisplayMemberPath = nameof(SkinModelOption.DisplayName);

            _skinModelPreference = ParseSkinModelPreference(_uiState.SkinModelPreferenceId);
            var selectedOption = SkinModelOptions.FirstOrDefault(option => option.Preference == _skinModelPreference)
                                 ?? SkinModelOptions[0];
            SkinModelComboBox.SelectedItem = selectedOption;
        }
        finally
        {
            _isRefreshingSkinModelSelection = false;
        }
    }

    private static string NormalizeBackgroundPresetId(string? presetId)
    {
        return string.IsNullOrWhiteSpace(presetId)
            ? DefaultBackgroundPresetId
            : presetId.Trim();
    }

    private static string NormalizeSkinModelPreferenceId(string? preferenceId)
    {
        if (string.IsNullOrWhiteSpace(preferenceId))
        {
            return DefaultSkinModelPreferenceId;
        }

        return preferenceId.Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            "classic" => "classic",
            "slim" => "slim",
            _ => DefaultSkinModelPreferenceId
        };
    }

    private static SkinModelPreference ParseSkinModelPreference(string? preferenceId)
    {
        return NormalizeSkinModelPreferenceId(preferenceId) switch
        {
            "classic" => SkinModelPreference.Classic,
            "slim" => SkinModelPreference.Slim,
            _ => SkinModelPreference.Auto
        };
    }

    private static string ToSkinModelPreferenceId(SkinModelPreference preference)
    {
        return preference switch
        {
            SkinModelPreference.Classic => "classic",
            SkinModelPreference.Slim => "slim",
            _ => DefaultSkinModelPreferenceId
        };
    }

    private static string GetSkinsDirectory()
    {
        var launcherSkinsDirectory = Path.Combine(
            GetPreferredLauncherDataDirectory(),
            "skins");
        Directory.CreateDirectory(launcherSkinsDirectory);

        TryMigrateFilesToLauncherDirectory(
            Path.Combine(GetLegacyLauncherAppDataDirectory(ensureExists: false), "skins"),
            launcherSkinsDirectory,
            "*.png");
        TryMigrateFilesToLauncherDirectory(
            Path.Combine(GetAssetsDirectory(), "Skins"),
            launcherSkinsDirectory,
            "*.png");

        return launcherSkinsDirectory;
    }

    private void InitializeSkinPreviewModel()
    {
        var sceneRoot = new Model3DGroup();

        _skinPreviewRotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), SkinPreviewBaseYaw);
        var sceneTransform = new Transform3DGroup();
        sceneTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0)));
        sceneTransform.Children.Add(new RotateTransform3D(_skinPreviewRotation));
        sceneTransform.Children.Add(new TranslateTransform3D(0, -0.08, 0));
        sceneRoot.Transform = sceneTransform;

        _skinPreviewHeadModel = new GeometryModel3D();
        _skinPreviewHatModel = new GeometryModel3D();
        sceneRoot.Children.Add(_skinPreviewHeadModel);
        sceneRoot.Children.Add(_skinPreviewHatModel);

        SkinPreviewViewport.Children.Clear();
        SkinPreviewViewport.Children.Add(new ModelVisual3D
        {
            Content = sceneRoot
        });
        RenderOptions.SetBitmapScalingMode(SkinPreviewViewport, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(SkinPreviewViewport, EdgeMode.Aliased);

        ApplySkinPreview(_selectedSkinFilePath);
        _skinPreviewTimer.Stop();
    }

    private void SkinPreviewTimer_OnTick(object? sender, EventArgs e)
    {
        if (_skinPreviewRotation is null)
        {
            return;
        }

        _skinPreviewSwingPhase += SkinPreviewSwingStep;
        _skinPreviewRotation.Angle =
            SkinPreviewBaseYaw + Math.Sin(_skinPreviewSwingPhase) * SkinPreviewSwingAmplitude;
    }

    private void StartSkinPreviewSwing()
    {
        if (_skinPreviewRotation is not null)
        {
            _skinPreviewSwingPhase = 0d;
            _skinPreviewRotation.Angle = SkinPreviewBaseYaw;
        }
    }

    private void ApplySkinPreview(string? skinFilePath)
    {
        var loadedSkinBitmap = LoadSkinBitmapForPreview(skinFilePath);
        var skinBitmap = loadedSkinBitmap ?? CreateFallbackSkinBitmap();
        var sourceTextureWidth = Math.Max(1, skinBitmap.PixelWidth);
        var sourceTextureHeight = Math.Max(1, skinBitmap.PixelHeight);
        ResolveSkinScale(sourceTextureWidth, sourceTextureHeight, out var sourceScaleX, out var sourceScaleY);
        var isModernLayout = sourceTextureWidth == sourceTextureHeight;
        var autoSlimModel = loadedSkinBitmap is not null && isModernLayout && IsSlimSkinModel(skinBitmap, sourceScaleX, sourceScaleY);
        var isSlimModel = _skinModelPreference switch
        {
            SkinModelPreference.Slim => isModernLayout,
            SkinModelPreference.Classic => false,
            _ => autoSlimModel
        };
        _selectedSkinIsSlim = isSlimModel;

        if (ShouldUseFlatSkinPreview())
        {
            SkinPreviewImage.Source = RenderSoftwareSkinPreview(
                skinBitmap,
                isModernLayout,
                isSlimModel,
                sourceScaleX,
                sourceScaleY);
            SkinPreviewImage.Visibility = Visibility.Visible;
            SkinPreviewViewport.Visibility = Visibility.Collapsed;
            _skinPreviewTimer.Stop();
            return;
        }

        if (_skinPreviewHeadModel is null || _skinPreviewHatModel is null)
        {
            return;
        }

        var previewAtlas = CreateSkinPreviewBaseAtlas(skinBitmap, isModernLayout, isSlimModel);
        var previewSkinBitmap = previewAtlas.Bitmap;
        Int32Rect AtlasRect(string name) => previewAtlas.Get(name);

        const double pixel = 0.10;
        static double ToPreviewUnit(double value) => value * pixel;
        var textureWidth = Math.Max(1, previewSkinBitmap.PixelWidth);
        var textureHeight = Math.Max(1, previewSkinBitmap.PixelHeight);

        var baseBrush = new ImageBrush(previewSkinBitmap)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        RenderOptions.SetBitmapScalingMode(baseBrush, BitmapScalingMode.NearestNeighbor);
        baseBrush.Freeze();
        var baseMaterial = new EmissiveMaterial(baseBrush);
        baseMaterial.Freeze();

        var baseMesh = new MeshGeometry3D();

        AppendCubeWithSkinUv(
            baseMesh,
            minX: ToPreviewUnit(-4), maxX: ToPreviewUnit(4),
            minY: ToPreviewUnit(12), maxY: ToPreviewUnit(20),
            minZ: ToPreviewUnit(-4), maxZ: ToPreviewUnit(4),
            textureWidth,
            textureHeight,
            frontUv: AtlasRect("head-front"),
            backUv: AtlasRect("head-back"),
            leftUv: AtlasRect("head-left"),
            rightUv: AtlasRect("head-right"),
            topUv: AtlasRect("head-top"),
            bottomUv: AtlasRect("head-bottom"));

        AppendCubeWithSkinUv(
            baseMesh,
            minX: ToPreviewUnit(-4), maxX: ToPreviewUnit(4),
            minY: ToPreviewUnit(0), maxY: ToPreviewUnit(12),
            minZ: ToPreviewUnit(-2), maxZ: ToPreviewUnit(2),
            textureWidth,
            textureHeight,
            frontUv: AtlasRect("body-front"),
            backUv: AtlasRect("body-back"),
            leftUv: AtlasRect("body-left"),
            rightUv: AtlasRect("body-right"),
            topUv: AtlasRect("body-top"),
            bottomUv: AtlasRect("body-bottom"));

        var rightArmMinX = isSlimModel ? -7d : -8d;
        var rightArmMaxX = -4d;
        var leftArmMinX = 4d;
        var leftArmMaxX = isSlimModel ? 7d : 8d;
        var rightLegMinX = -4d;
        var rightLegMaxX = 0d;
        var leftLegMinX = 0d;
        var leftLegMaxX = 4d;

        AppendCubeWithSkinUv(
            baseMesh,
            minX: ToPreviewUnit(rightArmMinX), maxX: ToPreviewUnit(rightArmMaxX),
            minY: ToPreviewUnit(0), maxY: ToPreviewUnit(12),
            minZ: ToPreviewUnit(-2), maxZ: ToPreviewUnit(2),
            textureWidth,
            textureHeight,
            frontUv: AtlasRect("right-arm-front"),
            backUv: AtlasRect("right-arm-back"),
            leftUv: AtlasRect("right-arm-left"),
            rightUv: AtlasRect("right-arm-right"),
            topUv: AtlasRect("right-arm-top"),
            bottomUv: AtlasRect("right-arm-bottom"));

        AppendCubeWithSkinUv(
            baseMesh,
            minX: ToPreviewUnit(leftArmMinX), maxX: ToPreviewUnit(leftArmMaxX),
            minY: ToPreviewUnit(0), maxY: ToPreviewUnit(12),
            minZ: ToPreviewUnit(-2), maxZ: ToPreviewUnit(2),
            textureWidth,
            textureHeight,
            frontUv: AtlasRect("left-arm-front"),
            backUv: AtlasRect("left-arm-back"),
            leftUv: AtlasRect("left-arm-left"),
            rightUv: AtlasRect("left-arm-right"),
            topUv: AtlasRect("left-arm-top"),
            bottomUv: AtlasRect("left-arm-bottom"));

        AppendCubeWithSkinUv(
            baseMesh,
            minX: ToPreviewUnit(rightLegMinX), maxX: ToPreviewUnit(rightLegMaxX),
            minY: ToPreviewUnit(-12), maxY: ToPreviewUnit(0),
            minZ: ToPreviewUnit(-2), maxZ: ToPreviewUnit(2),
            textureWidth,
            textureHeight,
            frontUv: AtlasRect("right-leg-front"),
            backUv: AtlasRect("right-leg-back"),
            leftUv: AtlasRect("right-leg-left"),
            rightUv: AtlasRect("right-leg-right"),
            topUv: AtlasRect("right-leg-top"),
            bottomUv: AtlasRect("right-leg-bottom"));

        AppendCubeWithSkinUv(
            baseMesh,
            minX: ToPreviewUnit(leftLegMinX), maxX: ToPreviewUnit(leftLegMaxX),
            minY: ToPreviewUnit(-12), maxY: ToPreviewUnit(0),
            minZ: ToPreviewUnit(-2), maxZ: ToPreviewUnit(2),
            textureWidth,
            textureHeight,
            frontUv: AtlasRect("left-leg-front"),
            backUv: AtlasRect("left-leg-back"),
            leftUv: AtlasRect("left-leg-left"),
            rightUv: AtlasRect("left-leg-right"),
            topUv: AtlasRect("left-leg-top"),
            bottomUv: AtlasRect("left-leg-bottom"));

        baseMesh.Freeze();
        _skinPreviewHeadModel.Geometry = baseMesh;
        _skinPreviewHeadModel.Material = baseMaterial;
        _skinPreviewHeadModel.BackMaterial = null;

        _skinPreviewHatModel.Geometry = new MeshGeometry3D();
        _skinPreviewHatModel.Material = null;
        _skinPreviewHatModel.BackMaterial = null;
        SkinPreviewImage.Visibility = Visibility.Collapsed;
        SkinPreviewViewport.Visibility = Visibility.Visible;
        StartSkinPreviewSwing();
    }

    private static void AppendCubeWithSkinUv(
        MeshGeometry3D targetMesh,
        double minX,
        double maxX,
        double minY,
        double maxY,
        double minZ,
        double maxZ,
        int textureWidth,
        int textureHeight,
        Int32Rect frontUv,
        Int32Rect backUv,
        Int32Rect leftUv,
        Int32Rect rightUv,
        Int32Rect topUv,
        Int32Rect bottomUv)
    {
        var cube = CreateCubeMeshWithSkinUv(
            minX,
            maxX,
            minY,
            maxY,
            minZ,
            maxZ,
            textureWidth,
            textureHeight,
            frontUv,
            backUv,
            leftUv,
            rightUv,
            topUv,
            bottomUv);

        var indexOffset = targetMesh.Positions.Count;
        foreach (var position in cube.Positions)
        {
            targetMesh.Positions.Add(position);
        }

        foreach (var textureCoordinate in cube.TextureCoordinates)
        {
            targetMesh.TextureCoordinates.Add(textureCoordinate);
        }

        foreach (var index in cube.TriangleIndices)
        {
            targetMesh.TriangleIndices.Add(index + indexOffset);
        }
    }

    private static Int32Rect ScaleSkinRect(int x, int y, int width, int height, double scaleX, double scaleY)
    {
        var scaledX = Math.Max(0, (int)Math.Round(x * scaleX));
        var scaledY = Math.Max(0, (int)Math.Round(y * scaleY));
        var scaledWidth = Math.Max(1, (int)Math.Round(width * scaleX));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * scaleY));
        return new Int32Rect(scaledX, scaledY, scaledWidth, scaledHeight);
    }

    private static void ResolveSkinScale(int textureWidth, int textureHeight, out double scaleX, out double scaleY)
    {
        // Skin layout can be 64x64 (modern) or 64x32 (legacy). HD skins keep same ratios.
        scaleX = textureWidth / 64d;
        var isLegacyLayoutRatio = textureWidth == textureHeight * 2;
        scaleY = isLegacyLayoutRatio
            ? textureHeight / 32d
            : textureHeight / 64d;
    }

    private static bool HasVisiblePixels(
        BitmapSource bitmap,
        Int32Rect region,
        byte alphaThreshold,
        int minimumVisiblePixels)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return false;
        }

        var clampedRegion = new Int32Rect(
            x: Math.Clamp(region.X, 0, bitmap.PixelWidth - 1),
            y: Math.Clamp(region.Y, 0, bitmap.PixelHeight - 1),
            width: Math.Clamp(region.Width, 1, bitmap.PixelWidth - Math.Clamp(region.X, 0, bitmap.PixelWidth - 1)),
            height: Math.Clamp(region.Height, 1, bitmap.PixelHeight - Math.Clamp(region.Y, 0, bitmap.PixelHeight - 1)));

        var stride = clampedRegion.Width * 4;
        var pixels = new byte[stride * clampedRegion.Height];
        bitmap.CopyPixels(clampedRegion, pixels, stride, 0);

        var visiblePixels = 0;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] >= alphaThreshold)
            {
                visiblePixels++;
                if (visiblePixels >= minimumVisiblePixels)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSlimSkinModel(BitmapSource bitmap, double scaleX, double scaleY)
    {
        // In modern 64x64 skins, slim (Alex) leaves two "unused" arm columns transparent.
        var rightArmUnused = ScaleSkinRect(54, 20, 2, 12, scaleX, scaleY);
        var leftArmUnused = ScaleSkinRect(46, 52, 2, 12, scaleX, scaleY);

        return IsFullyTransparent(bitmap, rightArmUnused, alphaThreshold: 24) &&
               IsFullyTransparent(bitmap, leftArmUnused, alphaThreshold: 24);
    }

    private static bool IsFullyTransparent(BitmapSource bitmap, Int32Rect region, byte alphaThreshold)
    {
        return !HasVisiblePixels(bitmap, region, alphaThreshold, minimumVisiblePixels: 1);
    }

    private static BitmapSource? LoadSkinBitmapForPreview(string? skinFilePath)
    {
        if (string.IsNullOrWhiteSpace(skinFilePath) || !File.Exists(skinFilePath))
        {
            return null;
        }

        try
        {
            var bitmap = LoadBitmapFromFile(skinFilePath, decodePixelWidth: null);
            if (bitmap.PixelWidth < 64 || bitmap.PixelHeight < 32)
            {
                return null;
            }

            var isModernLayout = bitmap.PixelWidth == bitmap.PixelHeight;
            var isLegacyLayout = bitmap.PixelWidth == bitmap.PixelHeight * 2;
            if (!isModernLayout && !isLegacyLayout)
            {
                return null;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource CreateFallbackSkinBitmap()
    {
        const int width = 64;
        const int height = 64;
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelOffset = y * stride + x * bytesPerPixel;
                var checker = ((x / 8) + (y / 8)) % 2 == 0;
                var baseTone = checker ? (byte)54 : (byte)34;

                pixels[pixelOffset] = (byte)(baseTone + 26);     // B
                pixels[pixelOffset + 1] = (byte)(baseTone + 46); // G
                pixels[pixelOffset + 2] = (byte)(baseTone + 60); // R
                pixels[pixelOffset + 3] = 255;                   // A
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class SkinPreviewTextureAtlas
    {
        public SkinPreviewTextureAtlas(BitmapSource bitmap, Dictionary<string, Int32Rect> regions)
        {
            Bitmap = bitmap;
            Regions = regions;
        }

        public BitmapSource Bitmap { get; }

        public IReadOnlyDictionary<string, Int32Rect> Regions { get; }

        public Int32Rect Get(string key) => Regions[key];
    }

    private static SkinPreviewTextureAtlas CreateSkinPreviewBaseAtlas(
        BitmapSource source,
        bool isModernLayout,
        bool isSlimModel)
    {
        var prepared = PrepareSkinBitmapForPreview(source);
        var preparedWidth = prepared.PixelWidth;
        var preparedHeight = prepared.PixelHeight;
        ResolveSkinScale(preparedWidth, preparedHeight, out var scaleX, out var scaleY);

        var regions = new List<(string Name, Int32Rect Rect)>(36);

        void Add(string name, int x, int y, int width, int height)
        {
            regions.Add((name, ScaleSkinRect(x, y, width, height, scaleX, scaleY)));
        }

        Add("head-front", 8, 8, 8, 8);
        Add("head-back", 24, 8, 8, 8);
        Add("head-left", 16, 8, 8, 8);
        Add("head-right", 0, 8, 8, 8);
        Add("head-top", 8, 0, 8, 8);
        Add("head-bottom", 16, 0, 8, 8);

        Add("body-front", 20, 20, 8, 12);
        Add("body-back", 32, 20, 8, 12);
        Add("body-left", 28, 20, 4, 12);
        Add("body-right", 16, 20, 4, 12);
        Add("body-top", 20, 16, 8, 4);
        Add("body-bottom", 28, 16, 8, 4);

        if (isSlimModel)
        {
            Add("right-arm-front", 44, 20, 3, 12);
            Add("right-arm-back", 51, 20, 3, 12);
            Add("right-arm-left", 47, 20, 4, 12);
            Add("right-arm-right", 40, 20, 4, 12);
            Add("right-arm-top", 44, 16, 3, 4);
            Add("right-arm-bottom", 47, 16, 3, 4);

            if (isModernLayout)
            {
                Add("left-arm-front", 36, 52, 3, 12);
                Add("left-arm-back", 43, 52, 3, 12);
                Add("left-arm-left", 39, 52, 4, 12);
                Add("left-arm-right", 32, 52, 4, 12);
                Add("left-arm-top", 36, 48, 3, 4);
                Add("left-arm-bottom", 39, 48, 3, 4);
            }
            else
            {
                Add("left-arm-front", 44, 20, 3, 12);
                Add("left-arm-back", 51, 20, 3, 12);
                Add("left-arm-left", 47, 20, 4, 12);
                Add("left-arm-right", 40, 20, 4, 12);
                Add("left-arm-top", 44, 16, 3, 4);
                Add("left-arm-bottom", 47, 16, 3, 4);
            }
        }
        else
        {
            Add("right-arm-front", 44, 20, 4, 12);
            Add("right-arm-back", 52, 20, 4, 12);
            Add("right-arm-left", 48, 20, 4, 12);
            Add("right-arm-right", 40, 20, 4, 12);
            Add("right-arm-top", 44, 16, 4, 4);
            Add("right-arm-bottom", 48, 16, 4, 4);

            if (isModernLayout)
            {
                Add("left-arm-front", 36, 52, 4, 12);
                Add("left-arm-back", 44, 52, 4, 12);
                Add("left-arm-left", 40, 52, 4, 12);
                Add("left-arm-right", 32, 52, 4, 12);
                Add("left-arm-top", 36, 48, 4, 4);
                Add("left-arm-bottom", 40, 48, 4, 4);
            }
            else
            {
                Add("left-arm-front", 44, 20, 4, 12);
                Add("left-arm-back", 52, 20, 4, 12);
                Add("left-arm-left", 48, 20, 4, 12);
                Add("left-arm-right", 40, 20, 4, 12);
                Add("left-arm-top", 44, 16, 4, 4);
                Add("left-arm-bottom", 48, 16, 4, 4);
            }
        }

        Add("right-leg-front", 4, 20, 4, 12);
        Add("right-leg-back", 12, 20, 4, 12);
        Add("right-leg-left", 8, 20, 4, 12);
        Add("right-leg-right", 0, 20, 4, 12);
        Add("right-leg-top", 4, 16, 4, 4);
        Add("right-leg-bottom", 8, 16, 4, 4);

        if (isModernLayout)
        {
            Add("left-leg-front", 20, 52, 4, 12);
            Add("left-leg-back", 28, 52, 4, 12);
            Add("left-leg-left", 24, 52, 4, 12);
            Add("left-leg-right", 16, 52, 4, 12);
            Add("left-leg-top", 20, 48, 4, 4);
            Add("left-leg-bottom", 24, 48, 4, 4);
        }
        else
        {
            Add("left-leg-front", 4, 20, 4, 12);
            Add("left-leg-back", 12, 20, 4, 12);
            Add("left-leg-left", 8, 20, 4, 12);
            Add("left-leg-right", 0, 20, 4, 12);
            Add("left-leg-top", 4, 16, 4, 4);
            Add("left-leg-bottom", 8, 16, 4, 4);
        }

        var padding = Math.Max(2, (int)Math.Ceiling(Math.Max(scaleX, scaleY)));
        var cellWidth = regions.Max(region => region.Rect.Width) + padding * 2;
        var cellHeight = regions.Max(region => region.Rect.Height) + padding * 2;
        const int columns = 6;
        var rows = (int)Math.Ceiling(regions.Count / (double)columns);
        var atlasWidth = cellWidth * columns;
        var atlasHeight = cellHeight * rows;

        var sourceStride = preparedWidth * 4;
        var sourcePixels = new byte[sourceStride * preparedHeight];
        prepared.CopyPixels(sourcePixels, sourceStride, 0);

        var atlasStride = atlasWidth * 4;
        var atlasPixels = new byte[atlasStride * atlasHeight];
        var atlasRegions = new Dictionary<string, Int32Rect>(regions.Count, StringComparer.Ordinal);

        for (var index = 0; index < regions.Count; index++)
        {
            var (name, rect) = regions[index];
            var column = index % columns;
            var row = index / columns;
            var destinationX = column * cellWidth + padding;
            var destinationY = row * cellHeight + padding;

            CopySkinRegionWithPadding(
                sourcePixels,
                preparedWidth,
                preparedHeight,
                atlasPixels,
                atlasWidth,
                atlasHeight,
                rect,
                destinationX,
                destinationY,
                padding);

            atlasRegions[name] = new Int32Rect(destinationX, destinationY, rect.Width, rect.Height);
        }

        var atlasBitmap = BitmapSource.Create(
            atlasWidth,
            atlasHeight,
            prepared.DpiX,
            prepared.DpiY,
            PixelFormats.Bgra32,
            palette: null,
            atlasPixels,
            atlasStride);
        atlasBitmap.Freeze();

        return new SkinPreviewTextureAtlas(atlasBitmap, atlasRegions);
    }

    private static void CopySkinRegionWithPadding(
        byte[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        byte[] targetPixels,
        int targetWidth,
        int targetHeight,
        Int32Rect sourceRect,
        int destinationX,
        int destinationY,
        int padding)
    {
        var sourceStride = sourceWidth * 4;
        var targetStride = targetWidth * 4;

        for (var y = -padding; y < sourceRect.Height + padding; y++)
        {
            var sampleY = sourceRect.Y + Math.Clamp(y, 0, sourceRect.Height - 1);
            var writeY = destinationY + y;
            if (sampleY < 0 || sampleY >= sourceHeight || writeY < 0 || writeY >= targetHeight)
            {
                continue;
            }

            for (var x = -padding; x < sourceRect.Width + padding; x++)
            {
                var sampleX = sourceRect.X + Math.Clamp(x, 0, sourceRect.Width - 1);
                var writeX = destinationX + x;
                if (sampleX < 0 || sampleX >= sourceWidth || writeX < 0 || writeX >= targetWidth)
                {
                    continue;
                }

                var sourceIndex = (sampleY * sourceStride) + (sampleX * 4);
                var targetIndex = (writeY * targetStride) + (writeX * 4);
                targetPixels[targetIndex] = sourcePixels[sourceIndex];
                targetPixels[targetIndex + 1] = sourcePixels[sourceIndex + 1];
                targetPixels[targetIndex + 2] = sourcePixels[sourceIndex + 2];
                targetPixels[targetIndex + 3] = sourcePixels[sourceIndex + 3];
            }
        }
    }

    private static BitmapSource PrepareSkinBitmapForPreview(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var working = (byte[])pixels.Clone();
        var pixelCount = width * height;
        ResolveSkinScale(width, height, out var scaleX, out var scaleY);
        var isModernLayout = width == height;
        var isSlimModel = isModernLayout && IsSlimSkinModel(bitmap, scaleX, scaleY);

        EnsureBaseSkinOpacity(working, width, height, scaleX, scaleY, isModernLayout, isSlimModel);

        var owner = new int[pixelCount];
        Array.Fill(owner, -1);
        var queue = new Queue<int>(pixelCount);

        for (var y = 0; y < height; y++)
        {
            var rowBase = y * stride;
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = y * width + x;
                var byteIndex = rowBase + x * 4;
                if (working[byteIndex + 3] == 0)
                {
                    continue;
                }

                owner[pixelIndex] = pixelIndex;
                queue.Enqueue(pixelIndex);
            }
        }

        if (queue.Count > 0)
        {
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var currentOwner = owner[current];
                var cx = current % width;
                var cy = current / width;

                void TryVisit(int nx, int ny)
                {
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    {
                        return;
                    }

                    var neighbor = ny * width + nx;
                    if (owner[neighbor] != -1)
                    {
                        return;
                    }

                    owner[neighbor] = currentOwner;
                    queue.Enqueue(neighbor);
                }

                TryVisit(cx - 1, cy);
                TryVisit(cx + 1, cy);
                TryVisit(cx, cy - 1);
                TryVisit(cx, cy + 1);
            }
        }

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var byteIndex = pixelIndex * 4;
            if (working[byteIndex + 3] != 0)
            {
                continue;
            }

            var sourcePixel = owner[pixelIndex];
            if (sourcePixel < 0)
            {
                continue;
            }

            var sourceByteIndex = sourcePixel * 4;
            working[byteIndex] = working[sourceByteIndex];
            working[byteIndex + 1] = working[sourceByteIndex + 1];
            working[byteIndex + 2] = working[sourceByteIndex + 2];
            working[byteIndex + 3] = 255;
        }

        var prepared = BitmapSource.Create(
            width,
            height,
            bitmap.DpiX,
            bitmap.DpiY,
            PixelFormats.Bgra32,
            palette: null,
            working,
            stride);
        prepared.Freeze();

        // WPF Viewport3D always applies linear filtering. Upscaling with nearest neighbor first
        // keeps the final preview crisp and close to the style on minecraft-inside.
        var minSide = Math.Max(1, Math.Min(width, height));
        var upscaleFactor = Math.Max(1, 512 / minSide);
        if (upscaleFactor <= 1)
        {
            return prepared;
        }

        return UpscaleBitmapNearest(prepared, upscaleFactor);
    }

    private static BitmapSource PrepareSkinOverlayBitmapForPreview(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var sourcePixels = new byte[stride * height];
        bitmap.CopyPixels(sourcePixels, stride, 0);
        var overlayPixels = new byte[sourcePixels.Length];

        ResolveSkinScale(width, height, out var scaleX, out var scaleY);
        var isModernLayout = width == height;

        CopySkinRegionPreserveAlpha(
            sourcePixels,
            overlayPixels,
            width,
            ScaleSkinRect(32, 0, 32, 16, scaleX, scaleY));

        if (isModernLayout)
        {
            CopySkinRegionPreserveAlpha(
                sourcePixels,
                overlayPixels,
                width,
                ScaleSkinRect(16, 32, 24, 16, scaleX, scaleY));
            CopySkinRegionPreserveAlpha(
                sourcePixels,
                overlayPixels,
                width,
                ScaleSkinRect(40, 32, 16, 16, scaleX, scaleY));
            CopySkinRegionPreserveAlpha(
                sourcePixels,
                overlayPixels,
                width,
                ScaleSkinRect(0, 32, 16, 16, scaleX, scaleY));
            CopySkinRegionPreserveAlpha(
                sourcePixels,
                overlayPixels,
                width,
                ScaleSkinRect(48, 48, 16, 16, scaleX, scaleY));
            CopySkinRegionPreserveAlpha(
                sourcePixels,
                overlayPixels,
                width,
                ScaleSkinRect(0, 48, 16, 16, scaleX, scaleY));
        }

        var overlay = BitmapSource.Create(
            width,
            height,
            bitmap.DpiX,
            bitmap.DpiY,
            PixelFormats.Bgra32,
            palette: null,
            overlayPixels,
            stride);
        overlay.Freeze();

        var minSide = Math.Max(1, Math.Min(width, height));
        var upscaleFactor = Math.Max(1, 512 / minSide);
        if (upscaleFactor <= 1)
        {
            return overlay;
        }

        return UpscaleBitmapNearest(overlay, upscaleFactor);
    }

    private static void FillTransparentBaseSkinPixelsFromOverlay(
        byte[] pixels,
        int width,
        int height,
        double scaleX,
        double scaleY,
        bool isModernLayout,
        bool isSlimModel)
    {
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(40, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(8, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(32, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(0, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(48, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(16, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(56, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(24, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(40, 0, 8, 8, scaleX, scaleY), ScaleSkinRect(8, 0, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(48, 0, 8, 8, scaleX, scaleY), ScaleSkinRect(16, 0, 8, 8, scaleX, scaleY));

        if (!isModernLayout)
        {
            return;
        }

        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(20, 36, 8, 12, scaleX, scaleY), ScaleSkinRect(20, 20, 8, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(16, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(16, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(28, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(28, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(32, 36, 8, 12, scaleX, scaleY), ScaleSkinRect(32, 20, 8, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(20, 32, 8, 4, scaleX, scaleY), ScaleSkinRect(20, 16, 8, 4, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(28, 32, 8, 4, scaleX, scaleY), ScaleSkinRect(28, 16, 8, 4, scaleX, scaleY));

        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(4, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(4, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(0, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(0, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(8, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(8, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(12, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(12, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(4, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(4, 16, 4, 4, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(8, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(8, 16, 4, 4, scaleX, scaleY));

        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(4, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(20, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(0, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(16, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(8, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(24, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(12, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(28, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(4, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(20, 48, 4, 4, scaleX, scaleY));
        CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(8, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(24, 48, 4, 4, scaleX, scaleY));

        if (isSlimModel)
        {
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(44, 36, 3, 12, scaleX, scaleY), ScaleSkinRect(44, 20, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(40, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(40, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(47, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(47, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(51, 36, 3, 12, scaleX, scaleY), ScaleSkinRect(51, 20, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(44, 32, 3, 4, scaleX, scaleY), ScaleSkinRect(44, 16, 3, 4, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(47, 32, 3, 4, scaleX, scaleY), ScaleSkinRect(47, 16, 3, 4, scaleX, scaleY));

            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(52, 52, 3, 12, scaleX, scaleY), ScaleSkinRect(36, 52, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(48, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(32, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(55, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(39, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(59, 52, 3, 12, scaleX, scaleY), ScaleSkinRect(43, 52, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(52, 48, 3, 4, scaleX, scaleY), ScaleSkinRect(36, 48, 3, 4, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(55, 48, 3, 4, scaleX, scaleY), ScaleSkinRect(39, 48, 3, 4, scaleX, scaleY));
        }
        else
        {
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(44, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(44, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(40, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(40, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(48, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(48, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(52, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(52, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(44, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(44, 16, 4, 4, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(48, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(48, 16, 4, 4, scaleX, scaleY));

            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(52, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(36, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(48, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(32, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(56, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(40, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(60, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(44, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(52, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(36, 48, 4, 4, scaleX, scaleY));
            CopyVisibleSkinPixelsIntoTransparentBase(pixels, width, height, ScaleSkinRect(56, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(40, 48, 4, 4, scaleX, scaleY));
        }
    }

    private static void EnsureBaseSkinOpacity(
        byte[] pixels,
        int width,
        int height,
        double scaleX,
        double scaleY,
        bool isModernLayout,
        bool isSlimModel)
    {
        MakeSkinRegionOpaque(pixels, width, height, ScaleSkinRect(8, 0, 24, 16, scaleX, scaleY));
        MakeSkinRegionOpaque(pixels, width, height, ScaleSkinRect(16, 16, 40, 16, scaleX, scaleY));

        if (!isModernLayout)
        {
            return;
        }

        MakeSkinRegionOpaque(pixels, width, height, ScaleSkinRect(16, 48, 16, 16, scaleX, scaleY));
        MakeSkinRegionOpaque(
            pixels,
            width,
            height,
            isSlimModel
                ? ScaleSkinRect(32, 48, 15, 16, scaleX, scaleY)
                : ScaleSkinRect(32, 48, 16, 16, scaleX, scaleY));
    }

    private static void CopyVisibleSkinPixels(byte[] pixels, int width, int height, Int32Rect sourceRect, Int32Rect destinationRect)
    {
        var copyWidth = Math.Min(sourceRect.Width, destinationRect.Width);
        var copyHeight = Math.Min(sourceRect.Height, destinationRect.Height);
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        var stride = width * 4;
        for (var y = 0; y < copyHeight; y++)
        {
            for (var x = 0; x < copyWidth; x++)
            {
                var sourceIndex = ((sourceRect.Y + y) * stride) + ((sourceRect.X + x) * 4);
                if (sourceIndex < 0 || sourceIndex + 3 >= pixels.Length || pixels[sourceIndex + 3] < 24)
                {
                    continue;
                }

                var destinationIndex = ((destinationRect.Y + y) * stride) + ((destinationRect.X + x) * 4);
                if (destinationIndex < 0 || destinationIndex + 3 >= pixels.Length)
                {
                    continue;
                }

                pixels[destinationIndex] = pixels[sourceIndex];
                pixels[destinationIndex + 1] = pixels[sourceIndex + 1];
                pixels[destinationIndex + 2] = pixels[sourceIndex + 2];
                pixels[destinationIndex + 3] = 255;
            }
        }
    }

    private static void CopyVisibleSkinPixelsIntoTransparentBase(byte[] pixels, int width, int height, Int32Rect sourceRect, Int32Rect destinationRect)
    {
        var copyWidth = Math.Min(sourceRect.Width, destinationRect.Width);
        var copyHeight = Math.Min(sourceRect.Height, destinationRect.Height);
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        var stride = width * 4;
        for (var y = 0; y < copyHeight; y++)
        {
            for (var x = 0; x < copyWidth; x++)
            {
                var sourceIndex = ((sourceRect.Y + y) * stride) + ((sourceRect.X + x) * 4);
                if (sourceIndex < 0 || sourceIndex + 3 >= pixels.Length || pixels[sourceIndex + 3] < 24)
                {
                    continue;
                }

                var destinationIndex = ((destinationRect.Y + y) * stride) + ((destinationRect.X + x) * 4);
                if (destinationIndex < 0 || destinationIndex + 3 >= pixels.Length)
                {
                    continue;
                }

                pixels[destinationIndex] = pixels[sourceIndex];
                pixels[destinationIndex + 1] = pixels[sourceIndex + 1];
                pixels[destinationIndex + 2] = pixels[sourceIndex + 2];
                pixels[destinationIndex + 3] = 255;
            }
        }
    }

    private static void CopySkinRegionPreserveAlpha(byte[] sourcePixels, byte[] targetPixels, int width, Int32Rect region)
    {
        var stride = width * 4;
        var maxX = Math.Min(width, region.X + region.Width);
        var maxY = Math.Min(sourcePixels.Length / stride, region.Y + region.Height);
        for (var y = Math.Max(0, region.Y); y < maxY; y++)
        {
            for (var x = Math.Max(0, region.X); x < maxX; x++)
            {
                var pixelIndex = (y * stride) + (x * 4);
                if (pixelIndex < 0 || pixelIndex + 3 >= sourcePixels.Length || pixelIndex + 3 >= targetPixels.Length)
                {
                    continue;
                }

                targetPixels[pixelIndex] = sourcePixels[pixelIndex];
                targetPixels[pixelIndex + 1] = sourcePixels[pixelIndex + 1];
                targetPixels[pixelIndex + 2] = sourcePixels[pixelIndex + 2];
                targetPixels[pixelIndex + 3] = sourcePixels[pixelIndex + 3];
            }
        }
    }

    private static void MakeSkinRegionOpaque(byte[] pixels, int width, int height, Int32Rect region)
    {
        var stride = width * 4;
        var maxX = Math.Min(width, region.X + region.Width);
        var maxY = Math.Min(height, region.Y + region.Height);
        for (var y = Math.Max(0, region.Y); y < maxY; y++)
        {
            for (var x = Math.Max(0, region.X); x < maxX; x++)
            {
                var pixelIndex = (y * stride) + (x * 4);
                if (pixelIndex >= 0 && pixelIndex + 3 < pixels.Length && pixels[pixelIndex + 3] < 24)
                {
                    pixels[pixelIndex + 3] = 255;
                }
            }
        }
    }

    private static BitmapSource UpscaleBitmapNearest(BitmapSource source, int factor)
    {
        if (factor <= 1)
        {
            return source;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var sourceStride = width * 4;
        var sourcePixels = new byte[sourceStride * height];
        source.CopyPixels(sourcePixels, sourceStride, 0);

        var targetWidth = width * factor;
        var targetHeight = height * factor;
        var targetStride = targetWidth * 4;
        var targetPixels = new byte[targetStride * targetHeight];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var srcIndex = y * sourceStride + x * 4;
                for (var fy = 0; fy < factor; fy++)
                {
                    var ty = y * factor + fy;
                    var rowBase = ty * targetStride;
                    for (var fx = 0; fx < factor; fx++)
                    {
                        var tx = x * factor + fx;
                        var dstIndex = rowBase + tx * 4;
                        targetPixels[dstIndex] = sourcePixels[srcIndex];
                        targetPixels[dstIndex + 1] = sourcePixels[srcIndex + 1];
                        targetPixels[dstIndex + 2] = sourcePixels[srcIndex + 2];
                        targetPixels[dstIndex + 3] = sourcePixels[srcIndex + 3];
                    }
                }
            }
        }

        var upscaled = BitmapSource.Create(
            targetWidth,
            targetHeight,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            palette: null,
            targetPixels,
            targetStride);
        upscaled.Freeze();
        return upscaled;
    }

    private static bool ShouldUseFlatSkinPreview() => true;

    private static string? TryGetDroppedSkinFilePath(IDataObject? dataObject)
    {
        if (dataObject is null || !dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] droppedFiles || droppedFiles.Length == 0)
        {
            return null;
        }

        return droppedFiles.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) &&
            File.Exists(path) &&
            string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase));
    }

    private void SetSkinDropZoneState(bool isActive)
    {
        if (SkinDropZoneBorder is null || SkinDropHintTextBlock is null)
        {
            return;
        }

        SkinDropZoneBorder.BorderBrush = (Brush)new BrushConverter().ConvertFromString(
            isActive ? "#76FFFFFF" : "#34FFFFFF")!;
        SkinDropZoneBorder.Background = (Brush)new BrushConverter().ConvertFromString(
            isActive ? "#CC161C10" : "#B90B0F0C")!;
        SkinDropHintTextBlock.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        SkinDropHintTextBlock.Text = isActive
            ? "Отпусти PNG, чтобы импортировать"
            : "Перетащи PNG сюда";
    }

    private void ImportSkinFromPath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Файл скина не найден.", sourcePath);
        }

        if (!string.Equals(Path.GetExtension(sourcePath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Можно импортировать только PNG-скин.");
        }

        var skinsDirectory = GetSkinsDirectory();
        var originalFileName = Path.GetFileName(sourcePath);
        var destinationPath = Path.Combine(skinsDirectory, originalFileName);

        if (File.Exists(destinationPath))
        {
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(originalFileName);
            var extension = Path.GetExtension(originalFileName);
            var uniqueName = $"{nameWithoutExtension}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
            destinationPath = Path.Combine(skinsDirectory, uniqueName);
        }

        File.Copy(sourcePath, destinationPath, overwrite: false);
        var importedFileName = Path.GetFileName(destinationPath);

        _uiState = BuildLauncherUiState(
            selectedSkinFileName: importedFileName,
            backgroundPresetId: _uiState.BackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: _uiState.LastLaunchedVersionId);

        SaveUiState();
        RefreshSkinFiles(importedFileName);
        SetStatus($"Импортирован скин: {importedFileName}");
    }

    private static BitmapSource RenderFlatSkinPreview(
        BitmapSource skinBitmap,
        bool isModernLayout,
        bool isSlimModel,
        double scaleX,
        double scaleY)
    {
        var bitmap = skinBitmap.Format == PixelFormats.Bgra32
            ? skinBitmap
            : new FormatConvertedBitmap(skinBitmap, PixelFormats.Bgra32, null, 0);
        var sourceWidth = bitmap.PixelWidth;
        var sourceHeight = bitmap.PixelHeight;
        var sourceStride = sourceWidth * 4;
        var sourcePixels = new byte[sourceStride * sourceHeight];
        bitmap.CopyPixels(sourcePixels, sourceStride, 0);

        Int32Rect S(int x, int y, int w, int h) => ScaleSkinRect(x, y, w, h, scaleX, scaleY);

        var headFront = S(8, 8, 8, 8);
        var bodyFront = S(20, 20, 8, 12);
        var rightArmFront = isSlimModel ? S(44, 20, 3, 12) : S(44, 20, 4, 12);
        var leftArmFront = isModernLayout
            ? (isSlimModel ? S(36, 52, 3, 12) : S(36, 52, 4, 12))
            : rightArmFront;
        var rightLegFront = S(4, 20, 4, 12);
        var leftLegFront = isModernLayout ? S(20, 52, 4, 12) : rightLegFront;

        var headOverlayFront = S(40, 8, 8, 8);
        var bodyOverlayFront = S(20, 36, 8, 12);
        var rightArmOverlayFront = isSlimModel ? S(44, 36, 3, 12) : S(44, 36, 4, 12);
        var leftArmOverlayFront = isModernLayout
            ? (isSlimModel ? S(52, 52, 3, 12) : S(52, 52, 4, 12))
            : rightArmOverlayFront;
        var rightLegOverlayFront = S(4, 36, 4, 12);
        var leftLegOverlayFront = isModernLayout ? S(4, 52, 4, 12) : rightLegOverlayFront;

        var hasHeadOverlay = HasVisiblePixels(skinBitmap, headOverlayFront, alphaThreshold: 24, minimumVisiblePixels: 1);
        var hasBodyOverlay = isModernLayout &&
                             HasVisiblePixels(skinBitmap, bodyOverlayFront, alphaThreshold: 24, minimumVisiblePixels: 1);
        var hasRightArmOverlay = isModernLayout &&
                                 HasVisiblePixels(skinBitmap, rightArmOverlayFront, alphaThreshold: 24, minimumVisiblePixels: 1);
        var hasLeftArmOverlay = isModernLayout &&
                                HasVisiblePixels(skinBitmap, leftArmOverlayFront, alphaThreshold: 24, minimumVisiblePixels: 1);
        var hasRightLegOverlay = isModernLayout &&
                                 HasVisiblePixels(skinBitmap, rightLegOverlayFront, alphaThreshold: 24, minimumVisiblePixels: 1);
        var hasLeftLegOverlay = isModernLayout &&
                                HasVisiblePixels(skinBitmap, leftLegOverlayFront, alphaThreshold: 24, minimumVisiblePixels: 1);

        const int unit = 6;
        var armWidth = isSlimModel ? 3 : 4;
        const int bodyWidth = 8;
        const int headHeight = 8;
        const int bodyHeight = 12;
        const int legWidth = 4;
        const int legHeight = 12;

        var contentWidth = armWidth + bodyWidth + armWidth;
        var contentHeight = headHeight + bodyHeight + legHeight;
        const int marginX = 4;
        const int marginY = 2;

        var targetWidth = (contentWidth + marginX * 2) * unit;
        var targetHeight = (contentHeight + marginY * 2) * unit;
        var targetStride = targetWidth * 4;
        var targetPixels = new byte[targetStride * targetHeight];

        var torsoX = (marginX + armWidth) * unit;
        var torsoY = (marginY + headHeight) * unit;
        var headX = torsoX;
        var headY = marginY * unit;
        var rightArmX = (marginX + armWidth - armWidth) * unit;
        var leftArmX = (marginX + armWidth + bodyWidth) * unit;
        var armsY = torsoY;
        var legsY = (marginY + headHeight + bodyHeight) * unit;
        var rightLegX = torsoX;
        var leftLegX = (marginX + armWidth + legWidth) * unit;

        DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, rightLegFront, targetPixels, targetWidth, targetHeight, rightLegX, legsY, legWidth * unit, legHeight * unit);
        DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, leftLegFront, targetPixels, targetWidth, targetHeight, leftLegX, legsY, legWidth * unit, legHeight * unit);
        DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, bodyFront, targetPixels, targetWidth, targetHeight, torsoX, torsoY, bodyWidth * unit, bodyHeight * unit);
        DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, rightArmFront, targetPixels, targetWidth, targetHeight, rightArmX, armsY, armWidth * unit, bodyHeight * unit);
        DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, leftArmFront, targetPixels, targetWidth, targetHeight, leftArmX, armsY, armWidth * unit, bodyHeight * unit);
        DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, headFront, targetPixels, targetWidth, targetHeight, headX, headY, bodyWidth * unit, headHeight * unit);

        if (hasRightLegOverlay)
        {
            DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, rightLegOverlayFront, targetPixels, targetWidth, targetHeight, rightLegX, legsY, legWidth * unit, legHeight * unit);
        }

        if (hasLeftLegOverlay)
        {
            DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, leftLegOverlayFront, targetPixels, targetWidth, targetHeight, leftLegX, legsY, legWidth * unit, legHeight * unit);
        }

        if (hasBodyOverlay)
        {
            DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, bodyOverlayFront, targetPixels, targetWidth, targetHeight, torsoX, torsoY, bodyWidth * unit, bodyHeight * unit);
        }

        if (hasRightArmOverlay)
        {
            DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, rightArmOverlayFront, targetPixels, targetWidth, targetHeight, rightArmX, armsY, armWidth * unit, bodyHeight * unit);
        }

        if (hasLeftArmOverlay)
        {
            DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, leftArmOverlayFront, targetPixels, targetWidth, targetHeight, leftArmX, armsY, armWidth * unit, bodyHeight * unit);
        }

        if (hasHeadOverlay)
        {
            DrawRegionNearest(sourcePixels, sourceWidth, sourceHeight, headOverlayFront, targetPixels, targetWidth, targetHeight, headX, headY, bodyWidth * unit, headHeight * unit);
        }

        var result = BitmapSource.Create(
            targetWidth,
            targetHeight,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            targetPixels,
            targetStride);
        result.Freeze();
        return result;
    }

    private readonly record struct SoftwareSkinQuad(
        Point3D TopLeft,
        Point3D TopRight,
        Point3D BottomRight,
        Point3D BottomLeft,
        Int32Rect UvRect);

    private readonly record struct SoftwareSkinVertex(
        double X,
        double Y,
        double Z,
        double U,
        double V);

    private static BitmapSource RenderSoftwareSkinPreview(
        BitmapSource skinBitmap,
        bool isModernLayout,
        bool isSlimModel,
        double scaleX,
        double scaleY)
    {
        var atlas = CreateSkinPreviewBaseAtlas(skinBitmap, isModernLayout, isSlimModel);
        var atlasBitmap = atlas.Bitmap;
        var atlasWidth = atlasBitmap.PixelWidth;
        var atlasHeight = atlasBitmap.PixelHeight;
        var atlasStride = atlasWidth * 4;
        var atlasPixels = new byte[atlasStride * atlasHeight];
        atlasBitmap.CopyPixels(atlasPixels, atlasStride, 0);

        Int32Rect Region(string key) => atlas.Get(key);
        var quads = new List<SoftwareSkinQuad>(36);

        static Point3D P(double x, double y, double z) => new(x, y, z);

        void AddCuboid(
            double minX,
            double maxX,
            double minY,
            double maxY,
            double minZ,
            double maxZ,
            Int32Rect front,
            Int32Rect back,
            Int32Rect left,
            Int32Rect right,
            Int32Rect top,
            Int32Rect bottom)
        {
            quads.Add(new SoftwareSkinQuad(P(minX, maxY, maxZ), P(maxX, maxY, maxZ), P(maxX, minY, maxZ), P(minX, minY, maxZ), front));
            quads.Add(new SoftwareSkinQuad(P(maxX, maxY, minZ), P(minX, maxY, minZ), P(minX, minY, minZ), P(maxX, minY, minZ), back));
            quads.Add(new SoftwareSkinQuad(P(maxX, maxY, maxZ), P(maxX, maxY, minZ), P(maxX, minY, minZ), P(maxX, minY, maxZ), left));
            quads.Add(new SoftwareSkinQuad(P(minX, maxY, minZ), P(minX, maxY, maxZ), P(minX, minY, maxZ), P(minX, minY, minZ), right));
            quads.Add(new SoftwareSkinQuad(P(minX, maxY, minZ), P(maxX, maxY, minZ), P(maxX, maxY, maxZ), P(minX, maxY, maxZ), top));
            quads.Add(new SoftwareSkinQuad(P(minX, minY, maxZ), P(maxX, minY, maxZ), P(maxX, minY, minZ), P(minX, minY, minZ), bottom));
        }

        AddCuboid(
            -4, 4,
            12, 20,
            -4, 4,
            Region("head-front"),
            Region("head-back"),
            Region("head-left"),
            Region("head-right"),
            Region("head-top"),
            Region("head-bottom"));

        AddCuboid(
            -4, 4,
            0, 12,
            -2, 2,
            Region("body-front"),
            Region("body-back"),
            Region("body-left"),
            Region("body-right"),
            Region("body-top"),
            Region("body-bottom"));

        var rightArmMinX = isSlimModel ? -7d : -8d;
        var rightArmMaxX = -4d;
        var leftArmMinX = 4d;
        var leftArmMaxX = isSlimModel ? 7d : 8d;

        AddCuboid(
            rightArmMinX, rightArmMaxX,
            0, 12,
            -2, 2,
            Region("right-arm-front"),
            Region("right-arm-back"),
            Region("right-arm-left"),
            Region("right-arm-right"),
            Region("right-arm-top"),
            Region("right-arm-bottom"));

        AddCuboid(
            leftArmMinX, leftArmMaxX,
            0, 12,
            -2, 2,
            Region("left-arm-front"),
            Region("left-arm-back"),
            Region("left-arm-left"),
            Region("left-arm-right"),
            Region("left-arm-top"),
            Region("left-arm-bottom"));

        AddCuboid(
            -4, 0,
            -12, 0,
            -2, 2,
            Region("right-leg-front"),
            Region("right-leg-back"),
            Region("right-leg-left"),
            Region("right-leg-right"),
            Region("right-leg-top"),
            Region("right-leg-bottom"));

        AddCuboid(
            0, 4,
            -12, 0,
            -2, 2,
            Region("left-leg-front"),
            Region("left-leg-back"),
            Region("left-leg-left"),
            Region("left-leg-right"),
            Region("left-leg-top"),
            Region("left-leg-bottom"));

        static Point3D Rotate(Point3D point, double yawDegrees, double pitchDegrees)
        {
            var yawRadians = yawDegrees * Math.PI / 180d;
            var pitchRadians = pitchDegrees * Math.PI / 180d;

            var cosYaw = Math.Cos(yawRadians);
            var sinYaw = Math.Sin(yawRadians);
            var x1 = point.X * cosYaw + point.Z * sinYaw;
            var z1 = -point.X * sinYaw + point.Z * cosYaw;

            var cosPitch = Math.Cos(pitchRadians);
            var sinPitch = Math.Sin(pitchRadians);
            var y2 = point.Y * cosPitch - z1 * sinPitch;
            var z2 = point.Y * sinPitch + z1 * cosPitch;

            return new Point3D(x1, y2, z2);
        }

        const double yaw = -16d;
        const double pitch = -7d;

        var rotatedQuads = quads
            .Select(quad => new SoftwareSkinQuad(
                Rotate(quad.TopLeft, yaw, pitch),
                Rotate(quad.TopRight, yaw, pitch),
                Rotate(quad.BottomRight, yaw, pitch),
                Rotate(quad.BottomLeft, yaw, pitch),
                quad.UvRect))
            .ToArray();

        var minProjectedX = double.PositiveInfinity;
        var maxProjectedX = double.NegativeInfinity;
        var minProjectedY = double.PositiveInfinity;
        var maxProjectedY = double.NegativeInfinity;

        foreach (var quad in rotatedQuads)
        {
            minProjectedX = Math.Min(minProjectedX, Math.Min(Math.Min(quad.TopLeft.X, quad.TopRight.X), Math.Min(quad.BottomRight.X, quad.BottomLeft.X)));
            maxProjectedX = Math.Max(maxProjectedX, Math.Max(Math.Max(quad.TopLeft.X, quad.TopRight.X), Math.Max(quad.BottomRight.X, quad.BottomLeft.X)));
            minProjectedY = Math.Min(minProjectedY, Math.Min(Math.Min(quad.TopLeft.Y, quad.TopRight.Y), Math.Min(quad.BottomRight.Y, quad.BottomLeft.Y)));
            maxProjectedY = Math.Max(maxProjectedY, Math.Max(Math.Max(quad.TopLeft.Y, quad.TopRight.Y), Math.Max(quad.BottomRight.Y, quad.BottomLeft.Y)));
        }

        const int targetWidth = 280;
        const int targetHeight = 320;
        const int margin = 18;
        var projectedWidth = Math.Max(1d, maxProjectedX - minProjectedX);
        var projectedHeight = Math.Max(1d, maxProjectedY - minProjectedY);
        var scale = Math.Min((targetWidth - margin * 2) / projectedWidth, (targetHeight - margin * 2) / projectedHeight);
        scale *= 0.64d;
        var centerX = (minProjectedX + maxProjectedX) / 2d;
        var centerY = (minProjectedY + maxProjectedY) / 2d;

        SoftwareSkinVertex Project(Point3D point, double u, double v) => new(
            X: (point.X - centerX) * scale + targetWidth / 2d,
            Y: (centerY - point.Y) * scale + targetHeight / 2d,
            Z: point.Z,
            U: u,
            V: v);

        var targetStride = targetWidth * 4;
        var targetPixels = new byte[targetStride * targetHeight];
        var zBuffer = new double[targetWidth * targetHeight];
        Array.Fill(zBuffer, double.NegativeInfinity);

        static bool IsFaceVisible(SoftwareSkinQuad quad)
        {
            var edge1 = quad.TopRight - quad.TopLeft;
            var edge2 = quad.BottomRight - quad.TopLeft;
            var normal = Vector3D.CrossProduct(edge1, edge2);
            return normal.Z < 0d;
        }

        foreach (var quad in rotatedQuads)
        {
            if (!IsFaceVisible(quad))
            {
                continue;
            }

            var u0 = quad.UvRect.X + 0.5;
            var v0 = quad.UvRect.Y + 0.5;
            var u1 = quad.UvRect.X + quad.UvRect.Width - 0.5;
            var v1 = quad.UvRect.Y + quad.UvRect.Height - 0.5;

            var topLeft = Project(quad.TopLeft, u0, v0);
            var topRight = Project(quad.TopRight, u1, v0);
            var bottomRight = Project(quad.BottomRight, u1, v1);
            var bottomLeft = Project(quad.BottomLeft, u0, v1);

            RasterizeSoftwareSkinTriangle(topLeft, topRight, bottomRight, atlasPixels, atlasWidth, atlasHeight, targetPixels, targetWidth, targetHeight, zBuffer);
            RasterizeSoftwareSkinTriangle(topLeft, bottomRight, bottomLeft, atlasPixels, atlasWidth, atlasHeight, targetPixels, targetWidth, targetHeight, zBuffer);
        }

        var result = BitmapSource.Create(
            targetWidth,
            targetHeight,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            targetPixels,
            targetStride);
        result.Freeze();
        return result;
    }

    private static void RasterizeSoftwareSkinTriangle(
        SoftwareSkinVertex a,
        SoftwareSkinVertex b,
        SoftwareSkinVertex c,
        byte[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        byte[] targetPixels,
        int targetWidth,
        int targetHeight,
        double[] zBuffer)
    {
        static double Edge(double ax, double ay, double bx, double by, double px, double py)
            => (px - ax) * (by - ay) - (py - ay) * (bx - ax);

        var area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (Math.Abs(area) < 0.0001d)
        {
            return;
        }

        var minX = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        var maxX = Math.Min(targetWidth - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        var maxY = Math.Min(targetHeight - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        var targetStride = targetWidth * 4;
        var sourceStride = sourceWidth * 4;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5d;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5d;
                var w0 = Edge(b.X, b.Y, c.X, c.Y, px, py);
                var w1 = Edge(c.X, c.Y, a.X, a.Y, px, py);
                var w2 = Edge(a.X, a.Y, b.X, b.Y, px, py);

                var hasNegative = w0 < 0 || w1 < 0 || w2 < 0;
                var hasPositive = w0 > 0 || w1 > 0 || w2 > 0;
                if (hasNegative && hasPositive)
                {
                    continue;
                }

                var alpha = 1d / area;
                var b0 = w0 * alpha;
                var b1 = w1 * alpha;
                var b2 = w2 * alpha;

                var z = a.Z * b0 + b.Z * b1 + c.Z * b2;
                var bufferIndex = y * targetWidth + x;
                if (z <= zBuffer[bufferIndex])
                {
                    continue;
                }

                var u = a.U * b0 + b.U * b1 + c.U * b2;
                var v = a.V * b0 + b.V * b1 + c.V * b2;
                var sampleX = Math.Clamp((int)Math.Round(u), 0, sourceWidth - 1);
                var sampleY = Math.Clamp((int)Math.Round(v), 0, sourceHeight - 1);
                var sourceIndex = sampleY * sourceStride + sampleX * 4;
                var sourceAlpha = sourcePixels[sourceIndex + 3];
                if (sourceAlpha == 0)
                {
                    continue;
                }

                var targetIndex = y * targetStride + x * 4;
                targetPixels[targetIndex] = sourcePixels[sourceIndex];
                targetPixels[targetIndex + 1] = sourcePixels[sourceIndex + 1];
                targetPixels[targetIndex + 2] = sourcePixels[sourceIndex + 2];
                targetPixels[targetIndex + 3] = 255;
                zBuffer[bufferIndex] = z;
            }
        }
    }

    private static void DrawRegionNearest(
        byte[] sourcePixels,
        int sourceWidth,
        int sourceHeight,
        Int32Rect sourceRegion,
        byte[] targetPixels,
        int targetWidth,
        int targetHeight,
        int targetX,
        int targetY,
        int targetRegionWidth,
        int targetRegionHeight)
    {
        if (sourceRegion.Width <= 0 || sourceRegion.Height <= 0 || targetRegionWidth <= 0 || targetRegionHeight <= 0)
        {
            return;
        }

        var sourceStride = sourceWidth * 4;
        var targetStride = targetWidth * 4;
        for (var y = 0; y < targetRegionHeight; y++)
        {
            var destinationY = targetY + y;
            if (destinationY < 0 || destinationY >= targetHeight)
            {
                continue;
            }

            var sourceY = sourceRegion.Y + y * sourceRegion.Height / targetRegionHeight;
            sourceY = Math.Clamp(sourceY, 0, sourceHeight - 1);

            for (var x = 0; x < targetRegionWidth; x++)
            {
                var destinationX = targetX + x;
                if (destinationX < 0 || destinationX >= targetWidth)
                {
                    continue;
                }

                var sourceX = sourceRegion.X + x * sourceRegion.Width / targetRegionWidth;
                sourceX = Math.Clamp(sourceX, 0, sourceWidth - 1);

                var sourceIndex = sourceY * sourceStride + sourceX * 4;
                var sourceAlpha = sourcePixels[sourceIndex + 3];
                if (sourceAlpha == 0)
                {
                    continue;
                }

                var targetIndex = destinationY * targetStride + destinationX * 4;
                if (sourceAlpha == 255)
                {
                    targetPixels[targetIndex] = sourcePixels[sourceIndex];
                    targetPixels[targetIndex + 1] = sourcePixels[sourceIndex + 1];
                    targetPixels[targetIndex + 2] = sourcePixels[sourceIndex + 2];
                    targetPixels[targetIndex + 3] = 255;
                    continue;
                }

                var inverseAlpha = 255 - sourceAlpha;
                targetPixels[targetIndex] = (byte)((sourcePixels[sourceIndex] * sourceAlpha + targetPixels[targetIndex] * inverseAlpha) / 255);
                targetPixels[targetIndex + 1] = (byte)((sourcePixels[sourceIndex + 1] * sourceAlpha + targetPixels[targetIndex + 1] * inverseAlpha) / 255);
                targetPixels[targetIndex + 2] = (byte)((sourcePixels[sourceIndex + 2] * sourceAlpha + targetPixels[targetIndex + 2] * inverseAlpha) / 255);
                targetPixels[targetIndex + 3] = (byte)(sourceAlpha + targetPixels[targetIndex + 3] * inverseAlpha / 255);
            }
        }
    }

    private static MeshGeometry3D CreateCubeMeshWithSkinUv(
        double minX,
        double maxX,
        double minY,
        double maxY,
        double minZ,
        double maxZ,
        int textureWidth,
        int textureHeight,
        Int32Rect frontUv,
        Int32Rect backUv,
        Int32Rect leftUv,
        Int32Rect rightUv,
        Int32Rect topUv,
        Int32Rect bottomUv)
    {
        var mesh = new MeshGeometry3D();

        AddFace(
            mesh,
            new Point3D(minX, maxY, maxZ),
            new Point3D(maxX, maxY, maxZ),
            new Point3D(maxX, minY, maxZ),
            new Point3D(minX, minY, maxZ),
            frontUv,
            textureWidth,
            textureHeight);

        AddFace(
            mesh,
            new Point3D(maxX, maxY, minZ),
            new Point3D(minX, maxY, minZ),
            new Point3D(minX, minY, minZ),
            new Point3D(maxX, minY, minZ),
            backUv,
            textureWidth,
            textureHeight);

        AddFace(
            mesh,
            new Point3D(minX, maxY, minZ),
            new Point3D(minX, maxY, maxZ),
            new Point3D(minX, minY, maxZ),
            new Point3D(minX, minY, minZ),
            rightUv,
            textureWidth,
            textureHeight);

        AddFace(
            mesh,
            new Point3D(maxX, maxY, maxZ),
            new Point3D(maxX, maxY, minZ),
            new Point3D(maxX, minY, minZ),
            new Point3D(maxX, minY, maxZ),
            leftUv,
            textureWidth,
            textureHeight);

        AddFace(
            mesh,
            new Point3D(minX, maxY, minZ),
            new Point3D(maxX, maxY, minZ),
            new Point3D(maxX, maxY, maxZ),
            new Point3D(minX, maxY, maxZ),
            topUv,
            textureWidth,
            textureHeight);

        AddFace(
            mesh,
            new Point3D(minX, minY, maxZ),
            new Point3D(maxX, minY, maxZ),
            new Point3D(maxX, minY, minZ),
            new Point3D(minX, minY, minZ),
            bottomUv,
            textureWidth,
            textureHeight);

        mesh.Freeze();
        return mesh;
    }

    private static void AddFace(
        MeshGeometry3D mesh,
        Point3D topLeft,
        Point3D topRight,
        Point3D bottomRight,
        Point3D bottomLeft,
        Int32Rect uvRect,
        int textureWidth,
        int textureHeight)
    {
        var baseIndex = mesh.Positions.Count;
        mesh.Positions.Add(topLeft);
        mesh.Positions.Add(topRight);
        mesh.Positions.Add(bottomRight);
        mesh.Positions.Add(bottomLeft);

        var u0 = uvRect.X / (double)textureWidth;
        var v0 = uvRect.Y / (double)textureHeight;
        var u1 = (uvRect.X + uvRect.Width) / (double)textureWidth;
        var v1 = (uvRect.Y + uvRect.Height) / (double)textureHeight;
        var insetU = 0.5 / textureWidth;
        var insetV = 0.5 / textureHeight;
        u0 = Math.Min(1, u0 + insetU);
        v0 = Math.Min(1, v0 + insetV);
        u1 = Math.Max(0, u1 - insetU);
        v1 = Math.Max(0, v1 - insetV);
        if (u1 <= u0)
        {
            var centerU = (u0 + u1) / 2d;
            u0 = centerU;
            u1 = centerU;
        }

        if (v1 <= v0)
        {
            var centerV = (v0 + v1) / 2d;
            v0 = centerV;
            v1 = centerV;
        }

        mesh.TextureCoordinates.Add(new Point(u0, v0));
        mesh.TextureCoordinates.Add(new Point(u1, v0));
        mesh.TextureCoordinates.Add(new Point(u1, v1));
        mesh.TextureCoordinates.Add(new Point(u0, v1));

        mesh.TriangleIndices.Add(baseIndex);
        mesh.TriangleIndices.Add(baseIndex + 2);
        mesh.TriangleIndices.Add(baseIndex + 1);

        mesh.TriangleIndices.Add(baseIndex);
        mesh.TriangleIndices.Add(baseIndex + 3);
        mesh.TriangleIndices.Add(baseIndex + 2);
    }

    private void RefreshSkinFiles(string? preferredFileName = null)
    {
        var skinsDirectory = GetSkinsDirectory();
        var skinFiles = Directory.EnumerateFiles(skinsDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .Select(path => new SkinFileEntry(Path.GetFileName(path), path))
            .OrderBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _availableSkinFiles = skinFiles;

        _isRefreshingSkinFiles = true;
        try
        {
            SkinFilesComboBox.ItemsSource = null;
            SkinFilesComboBox.ItemsSource = skinFiles;

            var desiredFileName = !string.IsNullOrWhiteSpace(preferredFileName)
                ? preferredFileName
                : _uiState.SelectedSkinFileName;

            var selectedEntry = string.IsNullOrWhiteSpace(desiredFileName)
                ? null
                : skinFiles.FirstOrDefault(entry =>
                    string.Equals(entry.FileName, desiredFileName, StringComparison.OrdinalIgnoreCase));

            SkinFilesComboBox.SelectedItem = selectedEntry;

            if (selectedEntry is null)
            {
                _selectedSkinFilePath = null;
                SelectedSkinFileTextBlock.Text = skinFiles.Length == 0
                    ? "PNG не выбран"
                    : $"В библиотеке: {skinFiles.Length} PNG";
                ApplySkinPreview(null);
            }
            else
            {
                _selectedSkinFilePath = selectedEntry.FullPath;
                SelectedSkinFileTextBlock.Text = $"Файл: {selectedEntry.FileName}";
                ApplySkinPreview(_selectedSkinFilePath);
            }
        }
        finally
        {
            _isRefreshingSkinFiles = false;
        }
    }

    private void SkinFilesComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSkinFiles)
        {
            return;
        }

        if (SkinFilesComboBox.SelectedItem is not SkinFileEntry selectedEntry)
        {
            _selectedSkinFilePath = null;
            SelectedSkinFileTextBlock.Text = "PNG не выбран";
            ApplySkinPreview(null);
            _uiState = BuildLauncherUiState(
                selectedSkinFileName: null,
                backgroundPresetId: _uiState.BackgroundPresetId,
                skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
                lastLaunchedVersionId: _uiState.LastLaunchedVersionId);
            SaveUiState();
            return;
        }

        _selectedSkinFilePath = selectedEntry.FullPath;
        SelectedSkinFileTextBlock.Text = $"Файл: {selectedEntry.FileName}";
        ApplySkinPreview(_selectedSkinFilePath);
        _uiState = BuildLauncherUiState(
            selectedSkinFileName: selectedEntry.FileName,
            backgroundPresetId: _uiState.BackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: _uiState.LastLaunchedVersionId);
        SaveUiState();
    }

    private void SkinModelComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSkinModelSelection)
        {
            return;
        }

        if (SkinModelComboBox.SelectedItem is not SkinModelOption selectedOption)
        {
            return;
        }

        _skinModelPreference = selectedOption.Preference;
        ApplySkinPreview(_selectedSkinFilePath);
        _uiState = BuildLauncherUiState(
            selectedSkinFileName: _uiState.SelectedSkinFileName,
            backgroundPresetId: _uiState.BackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: _uiState.LastLaunchedVersionId);
        SaveUiState();
        SetStatus($"Модель скина: {selectedOption.DisplayName}");
    }

    private void ImportSkinButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбери PNG-скин",
            Filter = "PNG files (*.png)|*.png",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ImportSkinFromPath(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка импорта скина");
        }
    }

    private void SkinDropZoneBorder_OnDragEnter(object sender, DragEventArgs e)
    {
        var hasPngSkin = TryGetDroppedSkinFilePath(e.Data) is not null;
        e.Effects = hasPngSkin ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetSkinDropZoneState(hasPngSkin);
    }

    private void SkinDropZoneBorder_OnDragOver(object sender, DragEventArgs e)
    {
        var hasPngSkin = TryGetDroppedSkinFilePath(e.Data) is not null;
        e.Effects = hasPngSkin ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SetSkinDropZoneState(hasPngSkin);
    }

    private void SkinDropZoneBorder_OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetSkinDropZoneState(false);
    }

    private void SkinDropZoneBorder_OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetSkinDropZoneState(false);

        var droppedSkinPath = TryGetDroppedSkinFilePath(e.Data);
        if (string.IsNullOrWhiteSpace(droppedSkinPath))
        {
            SetStatus("Перетащи PNG-файл скина.");
            return;
        }

        try
        {
            ImportSkinFromPath(droppedSkinPath);
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка импорта скина");
        }
    }

    private void OpenSkinsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var skinsDirectory = GetSkinsDirectory();
        OpenFolderInExplorer(skinsDirectory, $"Открыта папка скинов: {skinsDirectory}");
    }

    private void RefreshSkinsButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshSkinFiles(_uiState.SelectedSkinFileName);
        SetStatus("Список скинов обновлен.");
    }

    private void ClearSkinSelectionButton_OnClick(object sender, RoutedEventArgs e)
    {
        _selectedSkinFilePath = null;
        SelectedSkinFileTextBlock.Text = "PNG не выбран";
        ApplySkinPreview(null);
        SkinFilesComboBox.SelectedItem = null;
        _uiState = BuildLauncherUiState(
            selectedSkinFileName: null,
            backgroundPresetId: _uiState.BackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: _uiState.LastLaunchedVersionId);
        SaveUiState();
        SetStatus("Выбор скина сброшен.");
    }

    private void RefreshBackgroundSection()
    {
        var presetId = NormalizeBackgroundPresetId(_uiState.BackgroundPresetId);
        BackgroundCurrentPresetTextBlock.Text = string.Equals(presetId, DefaultBackgroundPresetId, StringComparison.Ordinal)
            ? "Дефолтный"
            : presetId;
    }

    private void ApplyDefaultBackgroundButton_OnClick(object sender, RoutedEventArgs e)
    {
        _uiState = BuildLauncherUiState(
            selectedSkinFileName: _uiState.SelectedSkinFileName,
            backgroundPresetId: DefaultBackgroundPresetId,
            skinModelPreferenceId: ToSkinModelPreferenceId(_skinModelPreference),
            lastLaunchedVersionId: _uiState.LastLaunchedVersionId);
        SaveUiState();
        RefreshBackgroundSection();
        TryApplyCustomBackgroundImage();
        SetStatus("Применен дефолтный фон.");
    }

    private void OpenBackgroundsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var backgroundsDirectory = Path.Combine(GetAssetsDirectory(), "Backgrounds");
        OpenFolderInExplorer(backgroundsDirectory, $"Открыта папка фонов: {backgroundsDirectory}");
    }

    private async Task RefreshRecommendedModCatalogAsync(bool forceRefresh = false)
    {
        if (_isRefreshingRecommendedModCatalog)
        {
            return;
        }

        if (!ShouldRefreshRecommendedModCatalog(forceRefresh))
        {
            SetRecommendedCatalogLoadingState(false);
            if (_recommendedModCatalog.Count > 0)
            {
                ApplyRecommendedModsFilter();
            }

            UpdateSelectedModsState();
            return;
        }

        _isRefreshingRecommendedModCatalog = true;
        try
        {
            _recommendedModCatalog = [];
            _filteredRecommendedModCatalog = [];
            _loadedRecommendedCatalogKinds.Clear();
            RecommendedModsListBox.ItemsSource = null;
            RecommendedModsListBorder.Visibility = Visibility.Collapsed;
            ModCatalogEmptyStateTextBlock.Visibility = Visibility.Collapsed;
            InstallSelectedModsButton.IsEnabled = false;
            ClearSelectedModsButton.IsEnabled = false;
            RefreshModCatalogButton.IsEnabled = !_isBusy;
            ModsSearchLabelTextBlock.Visibility = Visibility.Collapsed;
            ModsSearchTextBox.Visibility = Visibility.Collapsed;
            ModsCategoriesLabelTextBlock.Visibility = Visibility.Collapsed;
            ModsCategoryFiltersScrollViewer.Visibility = Visibility.Collapsed;
            ModsCategoryFilterPanel.Children.Clear();

            if (_selectedVersionChoice is null)
            {
                _recommendedModCatalogContextKey = null;
                SetRecommendedCatalogLoadingState(false);
                ModCatalogSummaryTextBlock.Text = "Сначала выбери версию Minecraft.";
                ModCatalogEmptyStateTextBlock.Text =
                    "Каталог модов появится после выбора версии.";
                ModCatalogEmptyStateTextBlock.Visibility = Visibility.Visible;
                ModsTargetFolderTextBlock.Text = "Папки загрузок будут показаны здесь.";
                return;
            }

            RefreshRecommendedModsCategoryFilters();
            UpdateRecommendedCatalogTargetFolderHint();

            var requestedKinds = GetRequestedCatalogContentKinds(_selectedModsCategoryFilter);
            var effectiveRequestedKinds = GetEffectiveRequestedCatalogContentKinds();
            var wantsMods = requestedKinds.Contains(RecommendedCatalogContentKind.Mod);
            var hasLoader = TryGetSupportedModsLoader(_selectedVersionChoice, out var loaderKind, out var requiresAutoInstall);
            var contextKey = BuildRecommendedModCatalogContextKey();

            if (!hasLoader &&
                requestedKinds.Length == 1 &&
                requestedKinds[0] == RecommendedCatalogContentKind.Mod)
            {
                _recommendedModCatalogContextKey = contextKey;
                SetRecommendedCatalogLoadingState(false);
                ModCatalogSummaryTextBlock.Text =
                    $"{_selectedVersionChoice.BaseVersionId}: на vanilla и чистом OptiFine каталог модов скрыт, переключись на Forge, Fabric или уже готовую комбинированную версию.";
                ModCatalogEmptyStateTextBlock.Text =
                    "Для модов переключись в списке версий на Forge, Fabric или готовую комбинированную версию, если она уже есть.";
                ModCatalogEmptyStateTextBlock.Visibility = Visibility.Visible;
                return;
            }

            ModsSearchLabelTextBlock.Visibility = Visibility.Visible;
            ModsSearchTextBox.Visibility = Visibility.Visible;
            var loadingSummary = BuildCatalogLoadingSummary(requestedKinds);
            ModCatalogSummaryTextBlock.Text = loadingSummary;
            SetRecommendedCatalogLoadingState(true, loadingSummary);
            await Dispatcher.Yield(DispatcherPriority.Background);

            var catalogTasks = effectiveRequestedKinds
                .Select(async kind => await _launcherService.GetRecommendedCatalogAsync(
                    kind,
                    kind == RecommendedCatalogContentKind.Mod ? loaderKind : null,
                    _selectedVersionChoice.BaseVersionId))
                .ToArray();
            var catalogGroups = catalogTasks.Length == 0
                ? Array.Empty<IReadOnlyList<RecommendedModCatalogItem>>()
                : await Task.WhenAll(catalogTasks);
            var combinedCatalog = catalogGroups
                .SelectMany(items => items)
                .GroupBy(item => $"{item.ContentKind}:{item.ProjectId}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => GetCatalogContentSortOrder(item.ContentKind))
                .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var mappedCatalog = combinedCatalog
                .Select(item => item with
                {
                    SourceIconUrl = item.IconUrl,
                    IconUrl = ResolveSafeRecommendedModIconUrl(item.ProjectId, item.IconUrl),
                    BadgeText = BuildRecommendedModBadgeText(item.DisplayName),
                    BadgeBackgroundHex = GetRecommendedModBadgeBackgroundHex(item.ProjectId, item.DisplayName)
                })
                .ToArray();

            SetRecommendedCatalogLoadingState(true, "Готовлю картинки модов...");
            var preparedInitialCatalog = await PrepareRecommendedModIconsAsync(
                mappedCatalog,
                InitialRecommendedModIconPrepareCount);

            _recommendedModCatalog = preparedInitialCatalog
                .Select(ApplyFavoriteState)
                .ToArray();
            _recommendedModCatalogContextKey = contextKey;
            _loadedRecommendedCatalogKinds.UnionWith(effectiveRequestedKinds);

            if (_recommendedModCatalog.Count == 0)
            {
                SetRecommendedCatalogLoadingState(false);
                RecommendedModsListBorder.Visibility = Visibility.Collapsed;
                ModCatalogSummaryTextBlock.Text =
                    BuildCatalogEmptySummary(requestedKinds, hasLoader ? loaderKind : null);
                ModCatalogEmptyStateTextBlock.Text =
                    BuildCatalogEmptyStateText(requestedKinds, hasLoader);
                ModCatalogEmptyStateTextBlock.Visibility = Visibility.Visible;
                return;
            }

            SetRecommendedCatalogLoadingState(false);
            RecommendedModsListBorder.Visibility = Visibility.Visible;
            ModCatalogSummaryTextBlock.Text = BuildCatalogLoadedSummary(
                _recommendedModCatalog,
                hasLoader ? loaderKind : null,
                requiresAutoInstall,
                wantsMods && !hasLoader);
            ApplyRecommendedModsFilter();
            _ = WarmRecommendedModIconsAsync(_recommendedModCatalog, ++_recommendedModIconLoadGeneration);
        }
        catch (Exception ex)
        {
            _recommendedModCatalogContextKey = null;
            _loadedRecommendedCatalogKinds.Clear();
            SetRecommendedCatalogLoadingState(false);
            ModCatalogSummaryTextBlock.Text = "Не удалось загрузить каталог модов.";
            ModCatalogEmptyStateTextBlock.Text = "Попробуй обновить каталог чуть позже.";
            ModCatalogEmptyStateTextBlock.Visibility = Visibility.Visible;
            RecommendedModsListBorder.Visibility = Visibility.Collapsed;
            ShowError(ex, "Ошибка каталога модов");
        }
        finally
        {
            _isRefreshingRecommendedModCatalog = false;
            _isRecommendedModCatalogLoadQueued = false;
            UpdateSelectedModsState();
        }
    }

    private void SetRecommendedCatalogLoadingState(bool isLoading, string? message = null)
    {
        ModsCatalogLoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Hidden;
        UpdateModsCatalogLoadingAnimationState(isLoading);
        if (!string.IsNullOrWhiteSpace(message))
        {
            ModsCatalogLoadingTextBlock.Text = message;
        }

        if (isLoading)
        {
            RecommendedModsListBorder.Visibility = Visibility.Collapsed;
            ModCatalogEmptyStateTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateModsCatalogLoadingAnimationState(bool isLoading)
    {
        if (!isLoading)
        {
            if (_modsCatalogLoadingSpinnerRenderingSubscribed)
            {
                CompositionTarget.Rendering -= ModsCatalogLoadingSpinnerOnRendering;
                _modsCatalogLoadingSpinnerRenderingSubscribed = false;
            }

            _modsCatalogLoadingSpinnerFrame = 0;
            _modsCatalogLoadingSpinnerLastRenderTime = TimeSpan.Zero;
            ApplyModsCatalogLoadingSpinnerFrame(_modsCatalogLoadingSpinnerFrame);
            return;
        }

        _modsCatalogLoadingSpinnerLastRenderTime = TimeSpan.Zero;
        _modsCatalogLoadingSpinnerFrame = (_modsCatalogLoadingSpinnerFrame + 1) % 8;
        ApplyModsCatalogLoadingSpinnerFrame(_modsCatalogLoadingSpinnerFrame);
        if (!_modsCatalogLoadingSpinnerRenderingSubscribed)
        {
            CompositionTarget.Rendering += ModsCatalogLoadingSpinnerOnRendering;
            _modsCatalogLoadingSpinnerRenderingSubscribed = true;
        }
    }

    private void ModsCatalogLoadingSpinnerOnRendering(object? sender, EventArgs e)
    {
        if (!_modsCatalogLoadingSpinnerRenderingSubscribed ||
            ModsCatalogLoadingPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (e is not RenderingEventArgs renderingArgs)
        {
            return;
        }

        if (_modsCatalogLoadingSpinnerLastRenderTime == TimeSpan.Zero)
        {
            _modsCatalogLoadingSpinnerLastRenderTime = renderingArgs.RenderingTime;
            return;
        }

        if (renderingArgs.RenderingTime - _modsCatalogLoadingSpinnerLastRenderTime < TimeSpan.FromMilliseconds(72))
        {
            return;
        }

        _modsCatalogLoadingSpinnerLastRenderTime = renderingArgs.RenderingTime;
        _modsCatalogLoadingSpinnerFrame = (_modsCatalogLoadingSpinnerFrame + 1) % 8;
        ApplyModsCatalogLoadingSpinnerFrame(_modsCatalogLoadingSpinnerFrame);
    }

    private void ApplyModsCatalogLoadingSpinnerFrame(int leadIndex)
    {
        if (ModsCatalogSpinnerDot0 is null ||
            ModsCatalogSpinnerDot1 is null ||
            ModsCatalogSpinnerDot2 is null ||
            ModsCatalogSpinnerDot3 is null ||
            ModsCatalogSpinnerDot4 is null ||
            ModsCatalogSpinnerDot5 is null ||
            ModsCatalogSpinnerDot6 is null ||
            ModsCatalogSpinnerDot7 is null)
        {
            return;
        }

        var dots = new UIElement[]
        {
            ModsCatalogSpinnerDot0,
            ModsCatalogSpinnerDot1,
            ModsCatalogSpinnerDot2,
            ModsCatalogSpinnerDot3,
            ModsCatalogSpinnerDot4,
            ModsCatalogSpinnerDot5,
            ModsCatalogSpinnerDot6,
            ModsCatalogSpinnerDot7
        };
        var opacities = new[] { 1d, 0.88d, 0.74d, 0.58d, 0.44d, 0.32d, 0.22d, 0.16d };
        for (var offset = 0; offset < dots.Length; offset++)
        {
            var dotIndex = (leadIndex + offset) % dots.Length;
            dots[dotIndex].Opacity = opacities[offset];
        }
    }

    private void BeginOpenModsSection()
    {
        RecommendedModsListBorder.Visibility = Visibility.Hidden;
        ModCatalogEmptyStateTextBlock.Visibility = Visibility.Hidden;

        if (_isRefreshingRecommendedModCatalog)
        {
            SetRecommendedCatalogLoadingState(true, ModCatalogSummaryTextBlock.Text);
        }
        else
        {
            SetRecommendedCatalogLoadingState(
                true,
                _selectedVersionChoice is null
                    ? "Подготавливаю раздел модов..."
                    : BuildCatalogLoadingSummary(GetRequestedCatalogContentKinds(_selectedModsCategoryFilter)));
        }

        Dispatcher.BeginInvoke(new Action(() => _ = OpenModsSectionAfterRenderAsync()), DispatcherPriority.Loaded);
    }

    private async Task OpenModsSectionAfterRenderAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Render);

        if (_activeSidePanelSection != SidePanelSection.Mods ||
            ModsSectionPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var shouldRefreshCatalog = _selectedVersionChoice is not null && ShouldRefreshRecommendedModCatalog();
        if (!shouldRefreshCatalog && !_isRefreshingRecommendedModCatalog)
        {
            RefreshModsSection(refreshCatalog: false, refreshCardStates: false);
            await Dispatcher.Yield(DispatcherPriority.Background);
            ApplyRecommendedModsFilter();
            UpdateSelectedModsState();
            return;
        }

        RefreshModsSection(refreshCatalog: true, refreshCardStates: false);
    }

    private void QueueRecommendedModCatalogRefresh(bool forceRefresh = false)
    {
        if (_isRecommendedModCatalogLoadQueued)
        {
            return;
        }

        _isRecommendedModCatalogLoadQueued = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (_activeSidePanelSection != SidePanelSection.Mods ||
                    ModsSectionPanel.Visibility != Visibility.Visible)
                {
                    return;
                }

                if (forceRefresh)
                {
                    await RefreshRecommendedModCatalogAsync(forceRefresh: true);
                }
                else
                {
                    await EnsureRecommendedModCatalogAsync();
                }
            }
            finally
            {
                _isRecommendedModCatalogLoadQueued = false;
            }
        }), DispatcherPriority.Loaded);
    }

    private void UpdateSelectedModsState()
    {
        var hasSelection = RecommendedModsListBox.SelectedItem is not null;
        InstallSelectedModsButton.IsEnabled = !_isBusy && hasSelection;
        ClearSelectedModsButton.IsEnabled = !_isBusy && hasSelection;
    }

    private static string GetCatalogContentLabel(RecommendedCatalogContentKind contentKind)
    {
        return contentKind switch
        {
            RecommendedCatalogContentKind.Mod => "Моды",
            RecommendedCatalogContentKind.Shader => "Шейдеры",
            RecommendedCatalogContentKind.ResourcePack => "Ресурспаки",
            RecommendedCatalogContentKind.Modpack => "Сборки",
            _ => "Каталог"
        };
    }

    private static string GetCatalogInstallActionText(RecommendedCatalogContentKind contentKind)
    {
        return contentKind == RecommendedCatalogContentKind.Modpack
            ? "Скачать"
            : "Установить";
    }

    private static int GetCatalogContentSortOrder(RecommendedCatalogContentKind contentKind)
    {
        return contentKind switch
        {
            RecommendedCatalogContentKind.Mod => 0,
            RecommendedCatalogContentKind.Shader => 1,
            RecommendedCatalogContentKind.ResourcePack => 2,
            RecommendedCatalogContentKind.Modpack => 3,
            _ => 99
        };
    }

    private static bool TryGetCatalogContentKindFromFilter(
        string? categoryFilter,
        out RecommendedCatalogContentKind contentKind)
    {
        switch (categoryFilter)
        {
            case "Моды":
                contentKind = RecommendedCatalogContentKind.Mod;
                return true;
            case "Шейдеры":
                contentKind = RecommendedCatalogContentKind.Shader;
                return true;
            case "Ресурспаки":
                contentKind = RecommendedCatalogContentKind.ResourcePack;
                return true;
            case "Сборки":
                contentKind = RecommendedCatalogContentKind.Modpack;
                return true;
            default:
                contentKind = default;
                return false;
        }
    }

    private static RecommendedCatalogContentKind[] GetRequestedCatalogContentKinds(string? categoryFilter)
    {
        return TryGetCatalogContentKindFromFilter(categoryFilter, out var contentKind)
            ? [contentKind]
            : [
                RecommendedCatalogContentKind.Mod,
                RecommendedCatalogContentKind.Shader,
                RecommendedCatalogContentKind.ResourcePack,
                RecommendedCatalogContentKind.Modpack
            ];
    }

    private string? BuildRecommendedModCatalogContextKey()
    {
        if (_selectedVersionChoice is null)
        {
            return null;
        }

        var loaderToken = TryGetSupportedModsLoader(_selectedVersionChoice, out var loaderKind, out _)
            ? loaderKind.ToString()
            : "none";
        return $"{_selectedVersionChoice.BaseVersionId.Trim()}|{loaderToken}";
    }

    private RecommendedCatalogContentKind[] GetEffectiveRequestedCatalogContentKinds()
    {
        var requestedKinds = GetRequestedCatalogContentKinds(_selectedModsCategoryFilter);
        if (_selectedVersionChoice is null)
        {
            return requestedKinds;
        }

        var hasLoader = TryGetSupportedModsLoader(_selectedVersionChoice, out _, out _);
        return requestedKinds
            .Where(kind => kind != RecommendedCatalogContentKind.Mod || hasLoader)
            .Distinct()
            .ToArray();
    }

    private bool ShouldRefreshRecommendedModCatalog(bool forceRefresh = false)
    {
        if (forceRefresh)
        {
            return true;
        }

        var contextKey = BuildRecommendedModCatalogContextKey();
        if (!string.Equals(_recommendedModCatalogContextKey, contextKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var effectiveRequestedKinds = GetEffectiveRequestedCatalogContentKinds();
        return effectiveRequestedKinds.Any(kind => !_loadedRecommendedCatalogKinds.Contains(kind));
    }

    private Task EnsureRecommendedModCatalogAsync(bool forceRefresh = false)
    {
        if (_isRefreshingRecommendedModCatalog)
        {
            return Task.CompletedTask;
        }

        if (!ShouldRefreshRecommendedModCatalog(forceRefresh))
        {
            if (_recommendedModCatalog.Count > 0)
            {
                ApplyRecommendedModsFilter();
            }

            UpdateSelectedModsState();
            return Task.CompletedTask;
        }

        return RefreshRecommendedModCatalogAsync(forceRefresh);
    }

    private static string BuildCatalogLoadingSummary(IReadOnlyList<RecommendedCatalogContentKind> requestedKinds)
    {
        return requestedKinds.Count == 1
            ? $"Загружаю раздел: {GetCatalogContentLabel(requestedKinds[0]).ToLowerInvariant()}..."
            : "Загружаю общий каталог: моды, шейдеры, ресурспаки и сборки...";
    }

    private string BuildCatalogEmptySummary(
        IReadOnlyList<RecommendedCatalogContentKind> requestedKinds,
        ModLoaderKind? loaderKind)
    {
        if (requestedKinds.Count == 1)
        {
            var label = GetCatalogContentLabel(requestedKinds[0]).ToLowerInvariant();
            return $"Для {_selectedVersionChoice?.BaseVersionId} пока не найдено: {label}.";
        }

        return loaderKind is null
            ? $"{_selectedVersionChoice?.BaseVersionId}: моды пока скрыты без Fabric/Forge, а других совместимых материалов не нашлось."
            : $"{_selectedVersionChoice?.BaseVersionId}: пока не нашлось совместимых материалов для каталога.";
    }

    private static string BuildCatalogEmptyStateText(
        IReadOnlyList<RecommendedCatalogContentKind> requestedKinds,
        bool hasLoader)
    {
        if (requestedKinds.Count == 1 && requestedKinds[0] == RecommendedCatalogContentKind.Mod && !hasLoader)
        {
            return "Для каталога модов переключись на Forge, Fabric или комбинированную версию в списке версий.";
        }

        if (!hasLoader && requestedKinds.Contains(RecommendedCatalogContentKind.Mod))
        {
            return "Шейдеры, ресурспаки и сборки доступны уже сейчас. Для модов выбери Forge, Fabric или комбинированную версию в списке.";
        }

        return "Попробуй другую версию Minecraft или обнови каталог чуть позже.";
    }

    private static string BuildCatalogLoadedSummary(
        IReadOnlyList<RecommendedModCatalogItem> catalog,
        ModLoaderKind? loaderKind,
        bool requiresAutoInstall,
        bool skippedModsBecauseNoLoader)
    {
        var summary = string.Join(", ",
            catalog
                .GroupBy(item => item.ContentKind)
                .OrderBy(group => GetCatalogContentSortOrder(group.Key))
                .Select(group => $"{GetCatalogContentLabel(group.Key).ToLowerInvariant()}: {group.Count()}"));

        if (skippedModsBecauseNoLoader)
        {
            return $"{summary}. Для модов переключись в списке версий на Forge, Fabric или готовую комбинированную версию.";
        }

        if (loaderKind is not null && catalog.Any(item => item.ContentKind == RecommendedCatalogContentKind.Mod))
        {
            var loaderLabel = GetLoaderDisplayName(loaderKind.Value);
            return requiresAutoInstall
                ? $"{summary}. Каталог модов работает через {loaderLabel}, модлоадер поставится автоматически."
                : $"{summary}. Каталог модов работает через {loaderLabel}.";
        }

        return summary;
    }

    private void UpdateRecommendedCatalogTargetFolderHint()
    {
        var currentProfileDirectory = ResolveCurrentProfileDirectory();
        var sharedProfileDirectory = _launcherService.GetGameDirectory(GetSelectedProfile());

        ModsTargetFolderTextBlock.Text = _selectedModsCategoryFilter switch
        {
            "Моды" => $"Папка mods: {Path.Combine(currentProfileDirectory, "mods")}",
            "Шейдеры" => $"Папка shaderpacks: {Path.Combine(currentProfileDirectory, "shaderpacks")}",
            "Ресурспаки" => $"Папка resourcepacks: {Path.Combine(currentProfileDirectory, "resourcepacks")}",
            "Сборки" => $"Папка modpacks: {Path.Combine(sharedProfileDirectory, "modpacks")}",
            _ => $"Папка версии: {currentProfileDirectory}"
        };
    }

    private RecommendedModCatalogItem ApplyFavoriteState(RecommendedModCatalogItem item)
    {
        var updatedItem = item with
        {
            IsFavorite = _favoriteModProjectIds.Contains(item.ProjectId)
        };

        if (item.IsInstalled &&
            !string.IsNullOrWhiteSpace(item.InstalledFilePath) &&
            File.Exists(item.InstalledFilePath))
        {
            return updatedItem with
            {
                IsInstalled = true,
                InstalledFilePath = item.InstalledFilePath,
                ActionText = "Удалить"
            };
        }

        var installedFilePath = TryResolveInstalledCatalogAssetPath(item.ContentKind, item.ProjectId, item.ResolvedFileName);
        return updatedItem with
        {
            IsInstalled = !string.IsNullOrWhiteSpace(installedFilePath),
            InstalledFilePath = installedFilePath,
            ActionText = string.IsNullOrWhiteSpace(installedFilePath) ? GetCatalogInstallActionText(item.ContentKind) : "Удалить"
        };
    }

    private void RefreshRecommendedCatalogCardStates()
    {
        if (_recommendedModCatalog.Count == 0)
        {
            return;
        }

        _recommendedModCatalog = _recommendedModCatalog
            .Select(ApplyFavoriteState)
            .ToArray();
        ApplyRecommendedModsFilter();
    }

    private void MarkRecommendedCatalogModsInstalled(IEnumerable<InstalledModProjectInfo> projects)
    {
        var installedProjectMap = projects
            .SelectMany(project => EnumerateInstalledCatalogProjectIds(project)
                .Select(projectId => (ProjectId: projectId, Project: project)))
            .GroupBy(entry => entry.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Project, StringComparer.OrdinalIgnoreCase);
        if (installedProjectMap.Count == 0 || _recommendedModCatalog.Count == 0)
        {
            return;
        }

        _recommendedModCatalog = _recommendedModCatalog
            .Select(item =>
            {
                if (item.ContentKind != RecommendedCatalogContentKind.Mod ||
                    !installedProjectMap.TryGetValue(item.ProjectId, out var installedProject))
                {
                    return item;
                }

                return item with
                {
                    IsInstalled = true,
                    InstalledFilePath = installedProject.FilePath,
                    ActionText = "Удалить"
                };
            })
            .ToArray();
        ApplyRecommendedModsFilter();
    }

    private void MarkRecommendedCatalogModRemoved(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || _recommendedModCatalog.Count == 0)
        {
            return;
        }

        _recommendedModCatalog = _recommendedModCatalog
            .Select(item =>
                item.ContentKind == RecommendedCatalogContentKind.Mod &&
                string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                    ? item with
                    {
                        IsInstalled = false,
                        InstalledFilePath = null,
                        ActionText = "Установить"
                    }
                    : item)
            .ToArray();
        ApplyRecommendedModsFilter();
    }

    private void MarkRecommendedCatalogItemInstalled(
        RecommendedCatalogContentKind contentKind,
        string? projectId,
        string? installedFilePath,
        string? resolvedFileName)
    {
        if (string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(installedFilePath) ||
            _recommendedModCatalog.Count == 0)
        {
            return;
        }

        _recommendedModCatalog = _recommendedModCatalog
            .Select(item =>
                item.ContentKind == contentKind &&
                string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                    ? item with
                    {
                        IsInstalled = true,
                        InstalledFilePath = installedFilePath,
                        ResolvedFileName = string.IsNullOrWhiteSpace(item.ResolvedFileName) ? resolvedFileName : item.ResolvedFileName,
                        ActionText = "Удалить"
                    }
                    : item)
            .ToArray();
        ApplyRecommendedModsFilter();
    }

    private void MarkRecommendedCatalogItemRemoved(RecommendedCatalogContentKind contentKind, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || _recommendedModCatalog.Count == 0)
        {
            return;
        }

        _recommendedModCatalog = _recommendedModCatalog
            .Select(item =>
                item.ContentKind == contentKind &&
                string.Equals(item.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)
                    ? item with
                    {
                        IsInstalled = false,
                        InstalledFilePath = null,
                        ActionText = GetCatalogInstallActionText(item.ContentKind)
                    }
                    : item)
            .ToArray();
        ApplyRecommendedModsFilter();
    }

    private void ApplyRecommendedModsFilter()
    {
        SetRecommendedCatalogLoadingState(false);
        var query = ModsSearchTextBox.Text?.Trim();
        IEnumerable<RecommendedModCatalogItem> filtered = _recommendedModCatalog;
        if (TryGetCatalogContentKindFromFilter(_selectedModsCategoryFilter, out var selectedKind))
        {
            filtered = filtered.Where(item => item.ContentKind == selectedKind);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(item =>
                item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.PackSummary.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _filteredRecommendedModCatalog = filtered
            .OrderBy(item => GetCatalogContentSortOrder(item.ContentKind))
            .ThenByDescending(item => item.IsFavorite)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        RecommendedModsListBox.ItemsSource = null;
        RecommendedModsListBox.ItemsSource = _filteredRecommendedModCatalog;
        RecommendedModsListBox.SelectedItem = null;

        if (_filteredRecommendedModCatalog.Count > 0)
        {
            RecommendedModsListBorder.Visibility = Visibility.Visible;
            ModCatalogEmptyStateTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        RecommendedModsListBorder.Visibility = Visibility.Collapsed;
        ModCatalogEmptyStateTextBlock.Visibility = Visibility.Visible;
        ModCatalogEmptyStateTextBlock.Text = !string.IsNullOrWhiteSpace(query)
            ? "По этому запросу ничего не найдено."
            : string.Equals(_selectedModsCategoryFilter, AllModsCategoryFilter, StringComparison.OrdinalIgnoreCase)
                ? "Для этой версии пока ничего не найдено."
                : $"В категории \"{_selectedModsCategoryFilter}\" пока ничего не найдено.";
    }

    private void RefreshRecommendedModsCategoryFilters()
    {
        ModsCategoryFilterPanel.Children.Clear();

        if (_selectedVersionChoice is null)
        {
            ModsCategoriesLabelTextBlock.Visibility = Visibility.Collapsed;
            ModsCategoryFiltersScrollViewer.Visibility = Visibility.Collapsed;
            _selectedModsCategoryFilter = AllModsCategoryFilter;
            return;
        }

        if (!RecommendedModsCategoryOrder.Contains(_selectedModsCategoryFilter, StringComparer.OrdinalIgnoreCase))
        {
            _selectedModsCategoryFilter = AllModsCategoryFilter;
        }

        foreach (var category in RecommendedModsCategoryOrder)
        {
            var button = new ToggleButton
            {
                Content = category,
                Tag = category,
                Style = (Style)FindResource("ModCategoryFilterToggleStyle"),
                IsChecked = string.Equals(category, _selectedModsCategoryFilter, StringComparison.OrdinalIgnoreCase)
            };
            button.Click += ModsCategoryFilterButton_OnClick;
            ModsCategoryFilterPanel.Children.Add(button);
        }

        ModsCategoriesLabelTextBlock.Visibility = Visibility.Visible;
        ModsCategoryFiltersScrollViewer.Visibility = Visibility.Visible;
    }

    private static string? ResolveSafeRecommendedModIconUrl(string projectId, string? sourceIconUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceIconUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(sourceIconUrl, UriKind.Absolute, out var iconUri))
        {
            return null;
        }

        if (iconUri.IsFile && File.Exists(iconUri.LocalPath))
        {
            return sourceIconUrl;
        }

        if (iconUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var cachePath = GetRecommendedModIconCachePath(projectId, sourceIconUrl);
        return File.Exists(cachePath) ? new Uri(cachePath).AbsoluteUri : null;
    }

    private async Task WarmRecommendedModIconsAsync(IReadOnlyList<RecommendedModCatalogItem> catalog, int generation)
    {
        try
        {
            var preparedCatalog = await PrepareRecommendedModIconsAsync(catalog);
            if (generation != _recommendedModIconLoadGeneration)
            {
                return;
            }

            var hasIconChanges = preparedCatalog.Count == catalog.Count &&
                                 preparedCatalog.Where((item, index) =>
                                         !string.Equals(item.IconUrl, catalog[index].IconUrl, StringComparison.OrdinalIgnoreCase))
                                     .Any();
            if (!hasIconChanges)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _recommendedModIconLoadGeneration)
                {
                    return;
                }

                _recommendedModCatalog = preparedCatalog;
                ApplyRecommendedModsFilter();
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка кэша иконок модов");
        }
    }

    private async Task<IReadOnlyList<RecommendedModCatalogItem>> PrepareRecommendedModIconsAsync(
        IReadOnlyList<RecommendedModCatalogItem> catalog,
        int maxCount = int.MaxValue)
    {
        var updated = new RecommendedModCatalogItem[catalog.Count];
        using var semaphore = new SemaphoreSlim(6);

        var tasks = catalog.Select(async (item, index) =>
        {
            if (index >= maxCount)
            {
                updated[index] = item;
                return;
            }

            if (string.IsNullOrWhiteSpace(item.SourceIconUrl))
            {
                updated[index] = item;
                return;
            }

            await semaphore.WaitAsync();
            try
            {
                var localIconUrl = await EnsureRecommendedModIconCachedAsync(item.ProjectId, item.SourceIconUrl);
                updated[index] = string.IsNullOrWhiteSpace(localIconUrl)
                    ? item
                    : item with { IconUrl = localIconUrl };
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return updated;
    }

    private static async Task<string?> EnsureRecommendedModIconCachedAsync(string projectId, string? sourceIconUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceIconUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(sourceIconUrl, UriKind.Absolute, out var iconUri) ||
            iconUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var extension = GetSupportedRecommendedModIconExtension(iconUri);
        if (extension is null)
        {
            return null;
        }

        var normalizedCachePath = GetRecommendedModIconCachePath(projectId, sourceIconUrl, ".cover.v6.png");
        if (File.Exists(normalizedCachePath) && new FileInfo(normalizedCachePath).Length > 0)
        {
            return new Uri(normalizedCachePath).AbsoluteUri;
        }

        var rawCachePath = GetRecommendedModIconCachePath(projectId, sourceIconUrl, ".raw" + extension);
        var legacyCachePath = GetRecommendedModIconCachePath(projectId, sourceIconUrl, extension);
        if (File.Exists(rawCachePath) && new FileInfo(rawCachePath).Length > 0)
        {
            if (TryNormalizeRecommendedModIcon(rawCachePath, normalizedCachePath))
            {
                return new Uri(normalizedCachePath).AbsoluteUri;
            }

            return new Uri(rawCachePath).AbsoluteUri;
        }

        if (File.Exists(legacyCachePath) && new FileInfo(legacyCachePath).Length > 0)
        {
            if (TryNormalizeRecommendedModIcon(legacyCachePath, normalizedCachePath))
            {
                return new Uri(normalizedCachePath).AbsoluteUri;
            }

            return new Uri(legacyCachePath).AbsoluteUri;
        }

        try
        {
            using var response = await ModIconHttp.GetAsync(iconUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(rawCachePath);
            await input.CopyToAsync(output);
            await output.FlushAsync();

            if (TryNormalizeRecommendedModIcon(rawCachePath, normalizedCachePath))
            {
                return new Uri(normalizedCachePath).AbsoluteUri;
            }

            return File.Exists(rawCachePath) && new FileInfo(rawCachePath).Length > 0
                ? new Uri(rawCachePath).AbsoluteUri
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNormalizeRecommendedModIcon(string sourcePath, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath) ||
            !File.Exists(sourcePath))
        {
            return false;
        }

        try
        {
            BitmapSource bitmap = LoadBitmapFromFile(sourcePath, decodePixelWidth: 192);
            bitmap = ApplyBitmapFileOrientation(sourcePath, bitmap);
            var normalizedBitmap = CreateRecommendedModIconBitmap(bitmap, 96);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(normalizedBitmap));

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Некорректный путь иконки мода."));
            using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource ApplyBitmapFileOrientation(string path, BitmapSource source)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return source;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0 ||
                decoder.Frames[0].Metadata is not BitmapMetadata metadata ||
                !TryGetBitmapOrientation(metadata, out var orientation) ||
                orientation is <= 1 or > 8)
            {
                return source;
            }

            var oriented = orientation switch
            {
                2 => TransformBitmap(source, flipX: true),
                3 => TransformBitmap(source, rotationDegrees: 180),
                4 => TransformBitmap(source, flipY: true),
                5 => TransformBitmap(source, rotationDegrees: 90, flipX: true),
                6 => TransformBitmap(source, rotationDegrees: 90),
                7 => TransformBitmap(source, rotationDegrees: 270, flipX: true),
                8 => TransformBitmap(source, rotationDegrees: 270),
                _ => source
            };

            return oriented;
        }
        catch
        {
            return source;
        }
    }

    private static bool TryGetBitmapOrientation(BitmapMetadata metadata, out ushort orientation)
    {
        orientation = 1;

        try
        {
            object? value = null;
            if (metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
            {
                value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            }
            else if (metadata.ContainsQuery("/ifd/{ushort=274}"))
            {
                value = metadata.GetQuery("/ifd/{ushort=274}");
            }

            if (value is ushort u16)
            {
                orientation = u16;
                return true;
            }

            if (value is short s16 && s16 > 0)
            {
                orientation = (ushort)s16;
                return true;
            }

            if (value is byte u8 && u8 > 0)
            {
                orientation = u8;
                return true;
            }
        }
        catch
        {
            // Ignore malformed metadata and keep source orientation.
        }

        return false;
    }

    private static BitmapSource TransformBitmap(
        BitmapSource source,
        int rotationDegrees = 0,
        bool flipX = false,
        bool flipY = false)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            return source;
        }

        var rotatedBy90 = Math.Abs(rotationDegrees % 180) == 90;
        var outputWidth = rotatedBy90 ? source.PixelHeight : source.PixelWidth;
        var outputHeight = rotatedBy90 ? source.PixelWidth : source.PixelHeight;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(outputWidth / 2d, outputHeight / 2d));

            if (rotationDegrees != 0)
            {
                context.PushTransform(new RotateTransform(rotationDegrees));
            }

            if (flipX || flipY)
            {
                context.PushTransform(new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1));
            }

            context.DrawImage(
                source,
                new Rect(-source.PixelWidth / 2d, -source.PixelHeight / 2d, source.PixelWidth, source.PixelHeight));

            if (flipX || flipY)
            {
                context.Pop();
            }

            if (rotationDegrees != 0)
            {
                context.Pop();
            }

            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(
            outputWidth,
            outputHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string? GetSupportedRecommendedModIconExtension(Uri iconUri)
    {
        var extension = Path.GetExtension(iconUri.AbsolutePath)?.ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp"
            ? extension
            : null;
    }

    private static string GetRecommendedModIconCachePath(string projectId, string sourceIconUrl, string? extensionOverride = null)
    {
        var extension = extensionOverride;
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = GetSupportedRecommendedModIconExtension(new Uri(sourceIconUrl, UriKind.Absolute)) ?? ".png";
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes($"{projectId}|{sourceIconUrl}"));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(GetModIconsDirectory(), hash + extension);
    }

    private static string BuildRecommendedModBadgeText(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "M";
        }

        var parts = displayName
            .Split([' ', '-', '_', '.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length >= 2)
        {
            return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
        }

        var letters = new string(displayName
            .Where(char.IsLetterOrDigit)
            .Take(2)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return string.IsNullOrWhiteSpace(letters) ? "M" : letters;
    }

    private static string GetRecommendedModBadgeBackgroundHex(string projectId, string displayName)
    {
        var source = string.IsNullOrWhiteSpace(projectId) ? displayName : projectId;
        var index = GetDeterministicColorIndex(source, RecommendedModBadgePalette.Length);
        return RecommendedModBadgePalette[index];
    }

    private static int GetDeterministicColorIndex(string? source, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        unchecked
        {
            var hash = 17;
            foreach (var character in source ?? string.Empty)
            {
                hash = (hash * 31) + char.ToUpperInvariant(character);
            }

            return Math.Abs(hash) % length;
        }
    }

    private void RefreshModsSection(bool refreshCatalog = true, bool refreshCardStates = true)
    {
        PruneInstalledCatalogModState();
        var isModsSectionVisible = _activeSidePanelSection == SidePanelSection.Mods &&
                                   ModsSectionPanel.Visibility == Visibility.Visible;
        var shouldRefreshCatalog = _selectedVersionChoice is not null && ShouldRefreshRecommendedModCatalog();

        var profilePath = ResolveCurrentProfileDirectory();
        var modsDirectory = Path.Combine(profilePath, "mods");
        Directory.CreateDirectory(modsDirectory);

        ModsInstalledSectionPanel.Visibility = Visibility.Collapsed;

        var modsCount = Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Count();
        if (isModsSectionVisible)
        {
            if (refreshCardStates)
            {
                RefreshRecommendedCatalogCardStates();
            }

            UpdateRecommendedCatalogTargetFolderHint();
            if (_isRefreshingRecommendedModCatalog)
            {
                SetRecommendedCatalogLoadingState(true, ModCatalogSummaryTextBlock.Text);
            }
            else if (!shouldRefreshCatalog)
            {
                SetRecommendedCatalogLoadingState(false);
            }
        }

        if (_selectedVersionChoice is null)
        {
            ModsSummaryTextBlock.Text = "Сначала выбери версию Minecraft, а затем открывай каталог модов.";
        }
        else if (!string.IsNullOrWhiteSpace(_selectedVersionChoice.AvailabilityNote))
        {
            var autoLoaders = GetAutoInstallLoaders(_selectedVersionChoice);
            var comboName = autoLoaders.Count > 0
                ? BuildLoaderCombinationDisplayName(autoLoaders)
                : _selectedVersionChoice.DisplayName;
            ModsSummaryTextBlock.Text =
                $"{_selectedVersionChoice.BaseVersionId}: {comboName} сейчас недоступен для этой версии Minecraft. Выбери Forge, Fabric или более старую версию игры.";
        }
        else if (TryGetSupportedModsLoader(_selectedVersionChoice, out var loaderKind, out var requiresAutoInstall))
        {
            var loaderLabel = GetLoaderDisplayName(loaderKind);
            ModsSummaryTextBlock.Text = requiresAutoInstall
                ? $"{_selectedVersionChoice.BaseVersionId}: выбран {loaderLabel}, лаунчер сам подготовит модовую сборку и покажет только совместимые моды."
                : $"{_selectedVersionChoice.BaseVersionId}: установлено модов {modsCount}, ниже доступен каталог совместимых модов для {loaderLabel}.";
        }
        else
        {
            ModsSummaryTextBlock.Text =
                $"{_selectedVersionChoice.BaseVersionId}: для модов переключись в списке версий на Forge, Fabric или комбинированную версию. Шейдеры, ресурспаки и сборки доступны прямо в каталоге.";
        }

        if (refreshCatalog && isModsSectionVisible)
        {
            if (!_isRefreshingRecommendedModCatalog && shouldRefreshCatalog)
            {
                SetRecommendedCatalogLoadingState(
                    true,
                    BuildCatalogLoadingSummary(GetRequestedCatalogContentKinds(_selectedModsCategoryFilter)));

                QueueRecommendedModCatalogRefresh();
            }
        }
    }

    private void DeleteInstalledModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledModEntry entry })
        {
            return;
        }

        try
        {
            if (File.Exists(entry.FullPath))
            {
                File.Delete(entry.FullPath);
            }

            RemoveInstalledCatalogModByPath(entry.FullPath);
            RefreshRecommendedCatalogCardStates();
            RefreshModsSection(refreshCatalog: false);
            SetStatus($"Мод удалён: {entry.FileName}.");
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка удаления мода");
        }
    }

    private void OpenModsFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var modsDirectory = Path.Combine(ResolveCurrentProfileDirectory(), "mods");
        OpenFolderInExplorer(modsDirectory, $"Открыта папка модов: {modsDirectory}");
        RefreshModsSection();
    }

    private void RefreshModsButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshModsSection();
        SetStatus("Список модов обновлен.");
    }

    private async void RefreshModCatalogButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshRecommendedModCatalogAsync(forceRefresh: true);
        SetStatus("Каталог модов обновлен.");
    }

    private void ClearSelectedModsButton_OnClick(object sender, RoutedEventArgs e)
    {
        RecommendedModsListBox.SelectedItem = null;
        UpdateSelectedModsState();
        SetStatus("Выбор модов очищен.");
    }

    private void RecommendedModsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRecommendedModCatalog)
        {
            return;
        }

        UpdateSelectedModsState();
    }

    private async void ModsCategoryFilterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string category })
        {
            return;
        }

        _selectedModsCategoryFilter = category;
        foreach (var child in ModsCategoryFilterPanel.Children.OfType<ToggleButton>())
        {
            child.IsChecked = string.Equals(child.Tag as string, category, StringComparison.OrdinalIgnoreCase);
        }

        UpdateRecommendedCatalogTargetFolderHint();
        await EnsureRecommendedModCatalogAsync();
    }

    private async void InstallCatalogModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecommendedModCatalogItem item })
        {
            return;
        }

        if (item.ContentKind == RecommendedCatalogContentKind.Mod)
        {
            if (item.IsInstalled)
            {
                try
                {
                    var installedFilePath = item.InstalledFilePath ?? TryResolveInstalledCatalogModPath(item.ProjectId, item.ResolvedFileName);
                    if (string.IsNullOrWhiteSpace(installedFilePath) || !File.Exists(installedFilePath))
                    {
                        RemoveInstalledCatalogMod(ResolveCurrentProfileVersionId(), item.ProjectId);
                        RemoveInstalledCatalogModByPath(installedFilePath);
                        MarkRecommendedCatalogModRemoved(item.ProjectId);
                        RefreshRecommendedCatalogCardStates();
                        RefreshModsSection(refreshCatalog: false);
                        SetStatus($"Мод уже не найден и убран из списка: {item.DisplayName}.");
                        return;
                    }

                    File.Delete(installedFilePath);
                    RemoveInstalledCatalogMod(ResolveCurrentProfileVersionId(), item.ProjectId);
                    RemoveInstalledCatalogModByPath(installedFilePath);
                    MarkRecommendedCatalogModRemoved(item.ProjectId);
                    RefreshRecommendedCatalogCardStates();
                    RefreshModsSection(refreshCatalog: false);
                    UpdateProfilePathLabel();
                    SetStatus($"Мод удалён: {item.DisplayName}.");
                }
                catch (Exception ex)
                {
                    ShowError(ex, "Ошибка удаления мода");
                    SetStatus("Ошибка удаления мода.");
                }

                return;
            }

            await InstallSelectedModsAsync(
            [
                new RecommendedModProject(
                    item.ProjectId,
                    item.DisplayName,
                    item.Description,
                    item.ResolvedFileName,
                    item.ResolvedDownloadUrl,
                    item.ResolvedFileSha1,
                    item.RequiredDependencyProjectIds)
            ]);
            return;
        }

        if (_isBusy)
        {
            return;
        }

        if (_selectedVersionChoice is null)
        {
            MessageBox.Show(this, "Сначала выбери версию Minecraft.", "Нет версии", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (item.IsInstalled)
        {
            try
            {
                var installedFilePath = item.InstalledFilePath ?? TryResolveInstalledCatalogAssetPath(
                    item.ContentKind,
                    item.ProjectId,
                    item.ResolvedFileName);
                if (string.IsNullOrWhiteSpace(installedFilePath) || !File.Exists(installedFilePath))
                {
                    MarkRecommendedCatalogItemRemoved(item.ContentKind, item.ProjectId);
                    RefreshRecommendedCatalogCardStates();
                    RefreshModsSection(refreshCatalog: false);
                    SetStatus($"Файл уже не найден и убран из списка: {item.DisplayName}.");
                    return;
                }

                File.Delete(installedFilePath);
                MarkRecommendedCatalogItemRemoved(item.ContentKind, item.ProjectId);
                RefreshRecommendedCatalogCardStates();
                RefreshModsSection(refreshCatalog: false);
                UpdateProfilePathLabel();
                SetStatus($"Удалено: {item.DisplayName}.");
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка удаления: {item.DisplayName}.");
                ShowError(ex, $"Ошибка удаления: {GetCatalogContentLabel(item.ContentKind)}");
            }

            return;
        }

        try
        {
            SetBusy(true, $"Подготовка: {item.DisplayName}...");
            ButtonProgressBar.Value = 0;

            var progress = new Progress<LauncherProgress>(UpdateProgress);
            var result = await _launcherService.InstallCatalogAssetAsync(
                item.ContentKind,
                item.ProjectId,
                item.DisplayName,
                _selectedVersionChoice.BaseVersionId,
                ResolveCurrentProfileVersionId(),
                GetSelectedProfile(),
                progress);

            var label = GetCatalogContentLabel(item.ContentKind);
            var installedFilePath = Path.Combine(result.DestinationDirectory, result.FileName);
            MarkRecommendedCatalogItemInstalled(item.ContentKind, item.ProjectId, installedFilePath, result.FileName);
            RefreshRecommendedCatalogCardStates();
            RefreshModsSection(refreshCatalog: false);
            SetStatus($"{label}: {item.DisplayName} {(result.Downloaded ? "скачан" : "уже был")}.");
            ProgressLabelTextBlock.Text = $"{label}: {result.DestinationDirectory}";
            UpdateRecommendedCatalogTargetFolderHint();
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка загрузки: {item.DisplayName}.");
            ShowError(ex, $"Ошибка: {GetCatalogContentLabel(item.ContentKind)}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ToggleFavoriteModButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RecommendedModCatalogItem item })
        {
            return;
        }

        var isFavorite = !_favoriteModProjectIds.Add(item.ProjectId);
        if (isFavorite)
        {
            _favoriteModProjectIds.Remove(item.ProjectId);
        }

        SaveFavoriteMods();
        _recommendedModCatalog = _recommendedModCatalog
            .Select(entry => string.Equals(entry.ProjectId, item.ProjectId, StringComparison.OrdinalIgnoreCase)
                ? ApplyFavoriteState(entry with { IsFavorite = !isFavorite })
                : ApplyFavoriteState(entry))
            .ToArray();
        ApplyRecommendedModsFilter();
        SetStatus(isFavorite
            ? $"Мод убран из рекомендаций: {item.DisplayName}."
            : $"Мод добавлен в рекомендации: {item.DisplayName}.");
        e.Handled = true;
    }

    private void ModsSearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRefreshingRecommendedModCatalog)
        {
            return;
        }

        ApplyRecommendedModsFilter();
    }

    private async void InstallSelectedModsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (RecommendedModsListBox.SelectedItems.Count == 0)
        {
            SetStatus("Выбери моды для установки.");
            return;
        }

        var selectedMods = RecommendedModsListBox.SelectedItems
            .OfType<RecommendedModCatalogItem>()
            .Select(item => new RecommendedModProject(
                item.ProjectId,
                item.DisplayName,
                item.Description,
                item.ResolvedFileName,
                item.ResolvedDownloadUrl,
                item.ResolvedFileSha1,
                item.RequiredDependencyProjectIds))
            .ToArray();
        await InstallSelectedModsAsync(selectedMods);
    }

    private async Task InstallSelectedModsAsync(IReadOnlyList<RecommendedModProject> projects)
    {
        if (_isBusy)
        {
            return;
        }

        if (_selectedVersionChoice is null)
        {
            MessageBox.Show(this, "Сначала выбери версию Minecraft.", "Нет версии", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetSupportedModsLoader(_selectedVersionChoice, out var loaderKind, out _))
        {
            MessageBox.Show(
                this,
                "Для каталога модов выбери версию с Fabric или Forge. Пункт с автоустановкой тоже подходит.",
                "Нет модлоадера",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            SetBusy(true, "Подготовка модов...");
            ButtonProgressBar.Value = 0;

            var progress = new Progress<LauncherProgress>(UpdateProgress);
            var launchVersion = await ResolveLaunchVersionAsync(_selectedVersionChoice, progress);
            if (!SelectVersionByVersionId(launchVersion.Id, allowFallback: false))
            {
                throw new InvalidOperationException(
                    $"Не удалось переключить лаунчер на установленную модовую версию {launchVersion.Id}.");
            }

            var result = await _launcherService.InstallRecommendedModsAsync(
                _selectedVersionChoice.BaseVersionId,
                launchVersion.Id,
                loaderKind,
                projects,
                GetSelectedProfile(),
                progress);

            var missingDownloadedFiles = result.DownloadedFiles
                .Where(fileName => !File.Exists(Path.Combine(result.ModsDirectory, fileName)))
                .ToArray();
            if (missingDownloadedFiles.Length > 0)
            {
                throw new IOException(
                    "После установки не удалось найти файлы модов в папке mods: " +
                    string.Join(", ", missingDownloadedFiles));
            }

            RegisterInstalledCatalogMods(launchVersion.Id, result.InstalledProjects);
            MarkRecommendedCatalogModsInstalled(result.InstalledProjects);
            RefreshRecommendedCatalogCardStates();
            RefreshModsSection(refreshCatalog: false);
            UpdateProfilePathLabel();
            UpdateSelectedModsState();

            var statusParts = new List<string>
            {
                $"скачано: {result.DownloadedCount}"
            };
            if (result.SkippedCount > 0)
            {
                statusParts.Add($"уже были: {result.SkippedCount}");
            }

            if (result.MissingCount > 0)
            {
                statusParts.Add($"не удалось подобрать: {result.MissingCount}");
            }

            SetStatus($"Моды установлены, {string.Join(", ", statusParts)}.");
            ProgressLabelTextBlock.Text = $"Папка модов: {result.ModsDirectory}";
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка установки модов.");
            ShowError(ex, "Ошибка модов");
        }
        finally
        {
            SetBusy(false);
            _ = EnsureRecommendedModCatalogAsync();
        }
    }

    private void RefreshFriendsSection(bool triggerBackgroundRefresh = true)
    {
        var profileNickname = GetActiveNickname() ?? UsernameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileNickname))
        {
            profileNickname = "Player123";
        }

        if (string.IsNullOrWhiteSpace(_accountState?.AccessToken))
        {
            ClearCloudFriendsSnapshot();
        }

        FriendsProfileNicknameTextBlock.Text = $"Ник: {profileNickname}";
        FriendsProfileTypeTextBlock.Text = HasAuthenticatedCloudSession()
            ? "Тип входа: облачный аккаунт"
            : HasIncognitoIdentity()
                ? "Тип входа: инкогнито"
            : HasRegisteredAccount()
                ? "Тип входа: требуется вход"
                : "Тип входа: оффлайн";
        RefreshFriendsProfileAvatarPreview();
        RefreshFriendsList();
        RefreshIncomingFriendRequestsList();
        UpdateVesperNetStatusText();
        UpdateFriendsCloudStatusText(HasAuthenticatedCloudSession()
            ? "Загружаю облачных друзей..."
            : HasIncognitoIdentity()
                ? "В режиме инкогнито облачные друзья недоступны."
                : HasRegisteredAccount()
                ? "Войди снова, чтобы пользоваться облачными друзьями."
                : "Войди в аккаунт, чтобы пользоваться облачными друзьями.");

        if (triggerBackgroundRefresh)
        {
            _ = RefreshVesperNetStatusAsync();
            _ = SyncPresenceAndFriendsAsync();
        }

        UpdateFriendsSectionResponsiveLayout();
    }

    private void RefreshFriendsList()
    {
        _isRefreshingFriendsList = true;
        try
        {
            FriendsListBox.ItemsSource = null;
            FriendsListBox.ItemsSource = _friendEntries.ToList();
            FriendsListBox.SelectedItem = null;
        }
        finally
        {
            _isRefreshingFriendsList = false;
        }

        UpdateFriendsSectionResponsiveLayout();
    }

    private void AddFriendButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = AddFriendButton_OnClickAsync();
    }

    private async Task AddFriendButton_OnClickAsync()
    {
        var enteredNickname = FriendNicknameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(enteredNickname))
        {
            SetStatus("Укажи ник друга.");
            return;
        }

        var normalizedNickname = NormalizeMinecraftUsername(enteredNickname);
        if (!UsernameRegex.IsMatch(normalizedNickname))
        {
            MessageBox.Show(
                this,
                "Ник друга должен быть 3-16 символов и содержать только A-Z, a-z, 0-9 или _.",
                "Некорректный ник",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Отправляю заявку в друзья...");
        var result = await SendFriendRequestAsync(normalizedNickname);
        if (result.Success)
        {
            FriendNicknameTextBox.Text = string.Empty;
            await RefreshCloudFriendsAsync();
            SetStatus($"Заявка отправлена: {normalizedNickname}");
        }
        else
        {
            MessageBox.Show(
                this,
                result.ErrorMessage ?? "Не удалось отправить заявку в друзья.",
                "Ошибка друзей",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("Ошибка отправки заявки в друзья.");
        }

        SetBusy(false);
    }

    private void RemoveFriendButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = RemoveFriendButton_OnClickAsync();
    }

    private async Task RemoveFriendButton_OnClickAsync()
    {
        string? targetUsername = null;
        if (FriendsListBox.SelectedItem is CloudFriendListItem selectedFriend)
        {
            targetUsername = selectedFriend.Username;
        }
        else
        {
            var typedFriend = FriendNicknameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(typedFriend))
            {
                SetStatus("Выбери друга из списка или введи ник для удаления.");
                return;
            }

            targetUsername = NormalizeMinecraftUsername(typedFriend);
        }

        await RemoveFriendByUsernameAsync(targetUsername);
    }

    private async Task RemoveFriendByUsernameAsync(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            SetStatus("Не удалось определить друга для удаления.");
            return;
        }

        SetBusy(true, "Удаляю друга...");
        var result = await RemoveFriendAsync(username);
        if (!result.Success)
        {
            MessageBox.Show(
                this,
                result.ErrorMessage ?? "Не удалось удалить друга.",
                "Ошибка друзей",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("Ошибка удаления друга.");
            SetBusy(false);
            return;
        }

        await RefreshCloudFriendsAsync();
        FriendNicknameTextBox.Text = string.Empty;
        SetStatus($"Друг удален: {username}");
        SetBusy(false);
    }

    private void RemoveFriendEntryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string username } || string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        _ = RemoveFriendByUsernameAsync(username);
    }

    private async void ConnectToFriendButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CloudFriendListItem friend })
        {
            return;
        }

        if (!friend.CanConnect)
        {
            SetStatus("У друга нет доступного подключения к миру.");
            return;
        }

        if (_gameProcessMonitor.IsRunning)
        {
            MessageBox.Show(
                this,
                "Сначала закрой текущую игру, потом подключайся к другу.",
                "Игра уже запущена",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!TrySelectFriendLaunchVersion(friend, out var friendVersionError))
        {
            SetStatus(friendVersionError ?? "Не удалось подобрать версию для подключения к другу.");
            MessageBox.Show(
                this,
                friendVersionError ?? "Не удалось подобрать версию для подключения к другу.",
                "Подключение к другу",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var hasReachableDirectJoin = !string.IsNullOrWhiteSpace(friend.JoinHost) &&
                                     friend.JoinPort is int directJoinPort &&
                                     directJoinPort > 0 &&
                                     directJoinPort <= 65535 &&
                                     CanReachJoinHost(friend.JoinHost, friend.JoinPort, isJoinable: true);
        var hasRelayEndpoint = HasRelayEndpoint(friend.RelayRoomId, friend.RelayTransportMode);
        var preferOverlay = false;

        if (!hasRelayEndpoint && hasReachableDirectJoin && friend.JoinPort is int confirmedDirectJoinPort)
        {
            _pendingDirectConnectServerAddress = friend.JoinHost;
            _pendingDirectConnectServerPort = confirmedDirectJoinPort;
            _pendingDirectConnectLabel = string.IsNullOrWhiteSpace(friend.ActivityText)
                ? $"{friend.JoinHost}:{confirmedDirectJoinPort}"
                : $"{friend.Username} - {friend.ActivityText}";

            LaunchButton_OnClick(LaunchButton, new RoutedEventArgs(Button.ClickEvent));
            return;
        }

        if (!hasRelayEndpoint)
        {
            SetStatus("У друга нет доступного подключения к миру.");
            return;
        }

        try
        {
            var accessToken = GetCurrentCloudAccessToken();
            var config = LoadAccountSyncConfig();
            var requestUrl = ResolveRelayConnectUrl(config);
            TryWriteLauncherDiagnosticLog(
                $"Join friend: target={friend.Username}, mode={friend.RelayTransportMode ?? "none"}, room={friend.RelayRoomId ?? "none"}, joinHost={friend.JoinHost ?? "none"}, joinPort={friend.JoinPort?.ToString() ?? "none"}");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(requestUrl))
            {
                SetStatus("Не удалось подготовить Vesper Relay.");
                return;
            }

            if (preferOverlay)
            {
                if (friend.JoinPort is not int overlayJoinPort || overlayJoinPort <= 0 || overlayJoinPort > 65535)
                {
                    SetStatus("У друга нет корректного порта для подключения через VesperNet.");
                    return;
                }

                if (!await CanUseVesperNetOverlayLocallyAsync())
                {
                    SetStatus("Для подключения к этому другу нужен локальный VesperNet Service.");
                    MessageBox.Show(
                        this,
                        "У друга мир опубликован через VesperNet Overlay. На этом ПК служба VesperNet пока не отвечает.",
                        "VesperNet недоступен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                SetBusy(true, "Поднимаю VesperNet Overlay...");
                var guestConnection = await VesperFriendRelay.CreateGuestConnectionAsync(
                    AccountSyncHttp,
                    requestUrl,
                    accessToken,
                    friend.RelayRoomId!);
                TryWriteLauncherDiagnosticLog(
                    $"VesperNet: guest connection created connectionId={guestConnection.ConnectionId}, room={guestConnection.RoomId}, transport={guestConnection.TransportMode}");
                var overlayResponse = await ConnectVesperNetGuestPeerAsync(
                    accessToken,
                    guestConnection.WebSocketUrl,
                    guestConnection.ConnectionId);
                if (overlayResponse is null || !overlayResponse.Ok || string.IsNullOrWhiteSpace(overlayResponse.PeerIp))
                {
                    throw new InvalidOperationException("VesperNet Overlay guest connect did not return the peer address.");
                }

                _pendingDirectConnectServerAddress = overlayResponse.PeerIp.Trim();
                _pendingDirectConnectServerPort = overlayJoinPort;
                _pendingDirectConnectLabel = $"{friend.Username} - VesperNet";
                TryWriteLauncherDiagnosticLog(
                    $"VesperNet: guest connect ready peerIp={_pendingDirectConnectServerAddress}, port={overlayJoinPort}, localIp={overlayResponse.LocalIp}");
            }
            else
            {
                SetBusy(true, "Готовлю Vesper Relay...");
                var guestTunnel = await VesperFriendRelay.CreateGuestTunnelAsync(
                    AccountSyncHttp,
                    requestUrl,
                    accessToken,
                    friend.RelayRoomId!);
                TrackGuestRelayTunnel(guestTunnel);

                _pendingDirectConnectServerAddress = IPAddress.Loopback.ToString();
                _pendingDirectConnectServerPort = guestTunnel.LocalPort;
                _pendingDirectConnectLabel = $"{friend.Username} - Vesper Relay";
                TryWriteLauncherDiagnosticLog(
                    $"Vesper Relay: guest tunnel ready localPort={guestTunnel.LocalPort}, friend={friend.Username}");
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, $"Ошибка подключения к другу через Vesper Relay ({friend.Username})");
            SetBusy(false);
            MessageBox.Show(
                this,
                "Не удалось подготовить подключение через Vesper Relay.",
                "Подключение к другу",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        LaunchButton_OnClick(LaunchButton, new RoutedEventArgs(Button.ClickEvent));
    }

    private void FriendsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingFriendsList)
        {
            return;
        }

        if (FriendsListBox.SelectedItem is not CloudFriendListItem selectedFriend)
        {
            return;
        }

        if (!_isRefreshingIncomingFriendRequestsList)
        {
            IncomingFriendRequestsListBox.SelectedItem = null;
            AcceptFriendRequestButton.IsEnabled = false;
            DeclineFriendRequestButton.IsEnabled = false;
        }

        FriendNicknameTextBox.Text = selectedFriend.Username;
    }

    private void IncomingFriendRequestsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingIncomingFriendRequestsList)
        {
            return;
        }

        if (IncomingFriendRequestsListBox.SelectedItem is not CloudIncomingFriendRequestItem selectedRequest)
        {
            AcceptFriendRequestButton.IsEnabled = false;
            DeclineFriendRequestButton.IsEnabled = false;
            return;
        }

        if (!_isRefreshingFriendsList)
        {
            FriendsListBox.SelectedItem = null;
        }

        AcceptFriendRequestButton.IsEnabled = true;
        DeclineFriendRequestButton.IsEnabled = true;
        FriendNicknameTextBox.Text = selectedRequest.Username;
    }

    private void AcceptFriendRequestButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = RespondToSelectedFriendRequestAsync("accept");
    }

    private void DeclineFriendRequestButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = RespondToSelectedFriendRequestAsync("decline");
    }

    private async Task RespondToSelectedFriendRequestAsync(string action)
    {
        if (IncomingFriendRequestsListBox.SelectedItem is not CloudIncomingFriendRequestItem selectedRequest)
        {
            SetStatus("Выбери входящую заявку.");
            return;
        }

        await RespondToFriendRequestAsync(selectedRequest, action);
    }

    private async Task RespondToFriendRequestAsync(CloudIncomingFriendRequestItem selectedRequest, string action)
    {
        var busyText = action == "accept" ? "Принимаю заявку..." : "Отклоняю заявку...";
        SetBusy(true, busyText);
        var result = await RespondFriendRequestAsync(selectedRequest.RequestId, action);
        if (result.Success)
        {
            await RefreshCloudFriendsAsync();
            SetStatus(action == "accept"
                ? $"Друг добавлен: {selectedRequest.Username}"
                : $"Заявка отклонена: {selectedRequest.Username}");
        }
        else
        {
            MessageBox.Show(
                this,
                result.ErrorMessage ?? "Не удалось обработать заявку в друзья.",
                "Ошибка друзей",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetStatus("Ошибка обработки заявки в друзья.");
        }

        SetBusy(false);
    }

    private void LoadFriends()
    {
        _friends.Clear();
        _friendEntries.Clear();
        if (!File.Exists(_friendsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_friendsPath);
            var state = JsonSerializer.Deserialize<FriendsState>(json);
            if (state?.Friends is null || state.Friends.Count == 0)
            {
                return;
            }

            var knownFriends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var savedFriend in state.Friends)
            {
                if (string.IsNullOrWhiteSpace(savedFriend))
                {
                    continue;
                }

                var normalizedFriend = NormalizeMinecraftUsername(savedFriend);
                if (!UsernameRegex.IsMatch(normalizedFriend))
                {
                    continue;
                }

                if (!knownFriends.Add(normalizedFriend))
                {
                    continue;
                }

                _friends.Add(normalizedFriend);
                if (_friends.Count >= MaxFriends)
                {
                    break;
                }
            }

            RebuildFriendEntriesFromSavedFriends();
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки списка друзей");
        }
    }

    private void SaveFriendsToDisk()
    {
        try
        {
            var settingsDirectory = Path.GetDirectoryName(_friendsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            var state = new FriendsState
            {
                Friends = _friends.ToList()
            };

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_friendsPath, json);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка сохранения списка друзей");
        }
    }

    private static string GetAssetsDirectory()
    {
        var assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        Directory.CreateDirectory(assetsDirectory);
        return assetsDirectory;
    }

    private async void OpenFolderInExplorer(string folderPath, string successStatus)
    {
        try
        {
            if (await PlatformService.Processes.OpenFolderAsync(folderPath).ConfigureAwait(true))
            {
                SetStatus(successStatus);
                return;
            }

            SetStatus($"Не удалось открыть папку: {folderPath}");
        }
        catch (Exception ex)
        {
            ShowError(ex, "Ошибка открытия папки");
        }
    }

    private void SavedUsernamesButton_OnClick(object sender, RoutedEventArgs e)
    {
        SavedUsernamesPanel.Visibility = Visibility.Collapsed;
        ShowSidePanelSection(SidePanelSection.Account);
        SetStatus(HasAuthenticatedCloudSession()
            ? "Открыто меню игрока."
            : "Открыто меню входа и регистрации.");
    }

    private void AddUsernameButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TrySaveCurrentUsername())
        {
            RefreshSavedUsernamesList();
            SetStatus($"Ник сохранен: {UsernameTextBox.Text.Trim()}");
        }
    }

    private void RemoveUsernameButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SavedUsernamesListBox.SelectedItem is not string selectedUsername)
        {
            return;
        }

        _savedUsernames.RemoveAll(existing =>
            string.Equals(existing, selectedUsername, StringComparison.OrdinalIgnoreCase));
        SaveSavedUsernamesToDisk();
        RefreshSavedUsernamesList();
        SetStatus($"Ник удален: {selectedUsername}");
    }

    private void SavedUsernamesListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSavedUsernames)
        {
            return;
        }

        if (SavedUsernamesListBox.SelectedItem is not string selectedUsername)
        {
            return;
        }

        ApplySavedUsernameSelection(selectedUsername);
    }

    private void AccountRecentUsernamesListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSavedUsernames)
        {
            return;
        }

        if (AccountRecentUsernamesListBox?.SelectedItem is not string selectedUsername)
        {
            return;
        }

        ApplySavedUsernameSelection(selectedUsername);
    }

    private void ApplySavedUsernameSelection(string selectedUsername)
    {
        var normalizedUsername = NormalizeMinecraftUsername(selectedUsername);
        UsernameTextBox.Text = normalizedUsername;
        if (AccountNicknameTextBox is not null)
        {
            AccountNicknameTextBox.Text = normalizedUsername;
        }

        AddOrPromoteSavedUsername(normalizedUsername);
        RefreshSavedUsernamesList(normalizedUsername);
        SetStatus($"Выбран ник: {normalizedUsername}");
    }

    private void RefreshSavedUsernamesList(string? selectedUsername = null)
    {
        _isRefreshingSavedUsernames = true;
        try
        {
            var usernames = _savedUsernames.ToList();
            var accountRecentUsernames = usernames.Take(MaxAccountRecentUsernames).ToList();
            SavedUsernamesListBox.ItemsSource = null;
            SavedUsernamesListBox.ItemsSource = usernames;
            if (AccountRecentUsernamesListBox is not null)
            {
                AccountRecentUsernamesListBox.ItemsSource = null;
                AccountRecentUsernamesListBox.ItemsSource = accountRecentUsernames;
            }

            if (!string.IsNullOrWhiteSpace(selectedUsername))
            {
                var normalizedUsername = NormalizeMinecraftUsername(selectedUsername);
                var selectedItem = _savedUsernames.FirstOrDefault(entry =>
                    string.Equals(entry, normalizedUsername, StringComparison.OrdinalIgnoreCase));
                SavedUsernamesListBox.SelectedItem = selectedItem;
                if (AccountRecentUsernamesListBox is not null)
                {
                    var accountSelectedItem = accountRecentUsernames.FirstOrDefault(entry =>
                        string.Equals(entry, normalizedUsername, StringComparison.OrdinalIgnoreCase));
                    AccountRecentUsernamesListBox.SelectedItem = accountSelectedItem;
                }
            }
            else
            {
                SavedUsernamesListBox.SelectedItem = null;
                if (AccountRecentUsernamesListBox is not null)
                {
                    AccountRecentUsernamesListBox.SelectedItem = null;
                }
            }
        }
        finally
        {
            _isRefreshingSavedUsernames = false;
        }

    }

    private bool TrySaveCurrentUsername()
    {
        var enteredUsername = UsernameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(enteredUsername))
        {
            MessageBox.Show(this, "Укажи ник.", "Нет ника", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var normalizedUsername = NormalizeMinecraftUsername(enteredUsername);
        if (!UsernameRegex.IsMatch(normalizedUsername))
        {
            MessageBox.Show(
                this,
                "Ник должен быть 3-16 символов и содержать только A-Z, a-z, 0-9 или _.",
                "Некорректный ник",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        UsernameTextBox.Text = normalizedUsername;
        AddOrPromoteSavedUsername(normalizedUsername);
        return true;
    }

    private void AddOrPromoteSavedUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        var normalizedUsername = NormalizeMinecraftUsername(username);
        if (!UsernameRegex.IsMatch(normalizedUsername))
        {
            return;
        }

        _savedUsernames.RemoveAll(existing =>
            string.Equals(existing, normalizedUsername, StringComparison.OrdinalIgnoreCase));
        _savedUsernames.Insert(0, normalizedUsername);

        if (_savedUsernames.Count > MaxSavedUsernames)
        {
            _savedUsernames.RemoveRange(MaxSavedUsernames, _savedUsernames.Count - MaxSavedUsernames);
        }

        SaveSavedUsernamesToDisk();
    }

    private void LoadSavedUsernames()
    {
        _savedUsernames.Clear();

        if (!File.Exists(_savedUsernamesPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_savedUsernamesPath);
            var state = JsonSerializer.Deserialize<SavedUsernamesState>(json);
            if (state?.Usernames is null || state.Usernames.Count == 0)
            {
                return;
            }

            var knownUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var savedUsername in state.Usernames)
            {
                if (string.IsNullOrWhiteSpace(savedUsername))
                {
                    continue;
                }

                var normalizedUsername = NormalizeMinecraftUsername(savedUsername);
                if (!UsernameRegex.IsMatch(normalizedUsername))
                {
                    continue;
                }

                if (!knownUsernames.Add(normalizedUsername))
                {
                    continue;
                }

                _savedUsernames.Add(normalizedUsername);
                if (_savedUsernames.Count >= MaxSavedUsernames)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки списка ников");
        }
    }

    private void SaveSavedUsernamesToDisk()
    {
        try
        {
            var settingsDirectory = Path.GetDirectoryName(_savedUsernamesPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            var state = new SavedUsernamesState
            {
                Usernames = _savedUsernames.ToList()
            };

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_savedUsernamesPath, json);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка сохранения списка ников");
        }
    }

    private void LoadFavoriteMods()
    {
        _favoriteModProjectIds.Clear();

        if (!File.Exists(_modFavoritesPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_modFavoritesPath);
            var favorites = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            foreach (var projectId in favorites)
            {
                if (!string.IsNullOrWhiteSpace(projectId))
                {
                    _favoriteModProjectIds.Add(projectId.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки избранных модов");
        }
    }

    private void SaveFavoriteMods()
    {
        try
        {
            var settingsDirectory = Path.GetDirectoryName(_modFavoritesPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            var favorites = _favoriteModProjectIds
                .OrderBy(projectId => projectId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var json = JsonSerializer.Serialize(favorites, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_modFavoritesPath, json);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка сохранения избранных модов");
        }
    }

    private void LoadInstalledCatalogMods()
    {
        _installedCatalogModPaths.Clear();

        if (!File.Exists(_installedCatalogModsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_installedCatalogModsPath);
            var installedMods = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in installedMods)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
                {
                    _installedCatalogModPaths[entry.Key.Trim()] = entry.Value.Trim();
                }
            }

            PruneInstalledCatalogModState(saveIfChanged: false);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка загрузки состояния модов из каталога");
        }
    }

    private void SaveInstalledCatalogMods()
    {
        try
        {
            var settingsDirectory = Path.GetDirectoryName(_installedCatalogModsPath);
            if (!string.IsNullOrWhiteSpace(settingsDirectory))
            {
                Directory.CreateDirectory(settingsDirectory);
            }

            var state = _installedCatalogModPaths
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_installedCatalogModsPath, json);
        }
        catch (Exception ex)
        {
            TryWriteErrorToLog(ex, "Ошибка сохранения состояния модов из каталога");
        }
    }

    private static string BuildInstalledCatalogModKey(LauncherProfile profile, string versionId, string projectId)
        => $"{profile}:{versionId.Trim()}:{projectId.Trim()}";

    private static IReadOnlyList<string> EnumerateInstalledCatalogProjectIds(InstalledModProjectInfo project)
    {
        return (project.ProjectAliases ?? [])
            .Append(project.ProjectId)
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Select(projectId => projectId!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void RegisterInstalledCatalogMods(string versionId, IEnumerable<InstalledModProjectInfo> projects)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return;
        }

        var profile = GetSelectedProfile();
        var changed = false;
        foreach (var project in projects)
        {
            if (string.IsNullOrWhiteSpace(project.FilePath) ||
                !File.Exists(project.FilePath))
            {
                continue;
            }

            foreach (var projectId in EnumerateInstalledCatalogProjectIds(project))
            {
                _installedCatalogModPaths[BuildInstalledCatalogModKey(profile, versionId, projectId)] = project.FilePath;
                changed = true;
            }
        }

        if (changed)
        {
            SaveInstalledCatalogMods();
        }
    }

    private void RemoveInstalledCatalogMod(string? versionId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var key = BuildInstalledCatalogModKey(GetSelectedProfile(), versionId, projectId);
        if (_installedCatalogModPaths.Remove(key))
        {
            SaveInstalledCatalogMods();
        }
    }

    private void RemoveInstalledCatalogModByPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(filePath);
        var removed = _installedCatalogModPaths
            .Where(entry => string.Equals(Path.GetFullPath(entry.Value), normalizedPath, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToArray();

        if (removed.Length == 0)
        {
            return;
        }

        foreach (var key in removed)
        {
            _installedCatalogModPaths.Remove(key);
        }

        SaveInstalledCatalogMods();
    }

    private string ResolveCatalogContentDirectory(RecommendedCatalogContentKind contentKind)
    {
        var currentProfileDirectory = ResolveCurrentProfileDirectory();
        var sharedProfileDirectory = _launcherService.GetGameDirectory(GetSelectedProfile());

        return contentKind switch
        {
            RecommendedCatalogContentKind.Mod => Path.Combine(currentProfileDirectory, "mods"),
            RecommendedCatalogContentKind.Shader => Path.Combine(currentProfileDirectory, "shaderpacks"),
            RecommendedCatalogContentKind.ResourcePack => Path.Combine(currentProfileDirectory, "resourcepacks"),
            RecommendedCatalogContentKind.Modpack => Path.Combine(sharedProfileDirectory, "modpacks"),
            _ => currentProfileDirectory
        };
    }

    private void PruneInstalledCatalogModState(bool saveIfChanged = true)
    {
        var removed = _installedCatalogModPaths
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !File.Exists(entry.Value))
            .Select(entry => entry.Key)
            .ToArray();

        if (removed.Length == 0)
        {
            return;
        }

        foreach (var key in removed)
        {
            _installedCatalogModPaths.Remove(key);
        }

        if (saveIfChanged)
        {
            SaveInstalledCatalogMods();
        }
    }

    private string? TryResolveInstalledCatalogModPath(string? projectId, string? resolvedFileName)
    {
        var currentVersionId = ResolveCurrentProfileVersionId();
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(currentVersionId))
        {
            return null;
        }

        var profile = GetSelectedProfile();
        var key = BuildInstalledCatalogModKey(profile, currentVersionId, projectId);
        if (_installedCatalogModPaths.TryGetValue(key, out var savedPath))
        {
            if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
            {
                return savedPath;
            }

            _installedCatalogModPaths.Remove(key);
            SaveInstalledCatalogMods();
        }

        var currentModsDirectory = Path.GetFullPath(Path.Combine(ResolveCurrentProfileDirectory(), "mods"));
        var fallbackSavedPath = _installedCatalogModPaths
            .Where(entry =>
                entry.Key.StartsWith($"{profile}:", StringComparison.OrdinalIgnoreCase) &&
                entry.Key.EndsWith($":{projectId.Trim()}", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .FirstOrDefault(path =>
                !string.IsNullOrWhiteSpace(path) &&
                File.Exists(path) &&
                string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(path)),
                    currentModsDirectory,
                    StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(fallbackSavedPath))
        {
            _installedCatalogModPaths[key] = fallbackSavedPath;
            SaveInstalledCatalogMods();
            return fallbackSavedPath;
        }

        var normalizedResolvedFileName = string.IsNullOrWhiteSpace(resolvedFileName)
            ? null
            : Path.GetFileName(resolvedFileName).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedResolvedFileName))
        {
            var savedPathByFileName = _installedCatalogModPaths.Values
                .FirstOrDefault(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    File.Exists(path) &&
                    string.Equals(
                        Path.GetDirectoryName(Path.GetFullPath(path)),
                        currentModsDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Path.GetFileName(path),
                        normalizedResolvedFileName,
                        StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(savedPathByFileName))
            {
                _installedCatalogModPaths[key] = savedPathByFileName;
                SaveInstalledCatalogMods();
                return savedPathByFileName;
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedFileName))
        {
            var guessedPath = TryResolveInstalledCatalogModPathFromDirectory(projectId, null);
            if (!string.IsNullOrWhiteSpace(guessedPath))
            {
                _installedCatalogModPaths[key] = guessedPath;
                SaveInstalledCatalogMods();
            }

            return guessedPath;
        }

        var candidatePath = Path.Combine(ResolveCurrentProfileDirectory(), "mods", resolvedFileName);
        if (!File.Exists(candidatePath))
        {
            var guessedPath = TryResolveInstalledCatalogModPathFromDirectory(projectId, resolvedFileName);
            if (string.IsNullOrWhiteSpace(guessedPath))
            {
                return null;
            }

            _installedCatalogModPaths[key] = guessedPath;
            SaveInstalledCatalogMods();
            return guessedPath;
        }

        _installedCatalogModPaths[key] = candidatePath;
        SaveInstalledCatalogMods();
        return candidatePath;
    }

    private string? TryResolveInstalledCatalogAssetPath(
        RecommendedCatalogContentKind contentKind,
        string? projectId,
        string? resolvedFileName)
    {
        if (contentKind == RecommendedCatalogContentKind.Mod)
        {
            return TryResolveInstalledCatalogModPath(projectId, resolvedFileName);
        }

        if (string.IsNullOrWhiteSpace(resolvedFileName))
        {
            return null;
        }

        var contentDirectory = ResolveCatalogContentDirectory(contentKind);
        if (!Directory.Exists(contentDirectory))
        {
            return null;
        }

        var normalizedResolvedFileName = Path.GetFileName(resolvedFileName).Trim();
        var candidatePath = Path.Combine(contentDirectory, normalizedResolvedFileName);
        if (File.Exists(candidatePath))
        {
            return candidatePath;
        }

        return Directory.EnumerateFiles(contentDirectory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileName(path),
                    normalizedResolvedFileName,
                    StringComparison.OrdinalIgnoreCase));
    }

    private string? TryResolveInstalledCatalogModPathFromDirectory(string projectId, string? resolvedFileName)
    {
        var modsDirectory = Path.Combine(ResolveCurrentProfileDirectory(), "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return null;
        }

        var normalizedProjectId = projectId.Trim().ToLowerInvariant();
        var normalizedResolvedFileName = string.IsNullOrWhiteSpace(resolvedFileName)
            ? null
            : Path.GetFileName(resolvedFileName).Trim().ToLowerInvariant();

        return Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
            {
                var fileName = Path.GetFileName(path).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(normalizedResolvedFileName) &&
                    string.Equals(fileName, normalizedResolvedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return fileName.Contains(normalizedProjectId, StringComparison.OrdinalIgnoreCase);
            });
    }

    private void TryWriteErrorToLog(Exception ex, string title)
    {
        _launcherLogger.Error(ex, title);
    }

    private void TryWriteLauncherDiagnosticLog(string message)
    {
        _launcherLogger.Info(message);
    }

    private void ShowError(Exception ex, string title)
    {
        TryWriteErrorToLog(ex, title);

        var text = $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Лог: {_launcherLogPath}";
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private sealed class SavedUsernamesState
    {
        public List<string> Usernames { get; init; } = [];
    }

    private sealed class FriendsState
    {
        public List<string> Friends { get; init; } = [];
    }

    private static string NormalizeMinecraftUsername(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "Player123";
        }

        var filtered = new string(input
            .Trim()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
            .ToArray());

        if (filtered.Length > 16)
        {
            filtered = filtered[..16];
        }

        if (filtered.Length < 3)
        {
            return "Player123";
        }

        return filtered;
    }


    private static bool IsInsideInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase ||
                source is ToggleButton ||
                source is Slider ||
                source is TextBox ||
                source is ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var childIndex = 0; childIndex < childCount; childIndex++)
        {
            var child = VisualTreeHelper.GetChild(root, childIndex);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void ScrollableChild_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject source || SettingsScrollViewer is null)
        {
            return;
        }

        var childScrollViewer = FindVisualDescendant<ScrollViewer>(source);
        if (childScrollViewer is null)
        {
            return;
        }

        var canScrollUp = childScrollViewer.VerticalOffset > 0.5;
        var canScrollDown = childScrollViewer.VerticalOffset < childScrollViewer.ScrollableHeight - 0.5;
        var shouldDelegateToParent =
            childScrollViewer.ScrollableHeight <= 0.5 ||
            (e.Delta > 0 && !canScrollUp) ||
            (e.Delta < 0 && !canScrollDown);

        if (!shouldDelegateToParent)
        {
            return;
        }

        if (_activeSidePanelSection is SidePanelSection.Mods or SidePanelSection.Skin)
        {
            e.Handled = true;
            return;
        }

        var targetOffset = Math.Clamp(
            SettingsScrollViewer.VerticalOffset - (e.Delta / 3.0),
            0.0,
            SettingsScrollViewer.ScrollableHeight);
        if (Math.Abs(targetOffset - SettingsScrollViewer.VerticalOffset) <= 0.01)
        {
            return;
        }

        SettingsScrollViewer.ScrollToVerticalOffset(targetOffset);
        e.Handled = true;
    }

    private void SettingsScrollViewer_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_activeSidePanelSection is SidePanelSection.Mods or SidePanelSection.Skin)
        {
            e.Handled = true;
        }
    }

    private void SettingsScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_activeSidePanelSection is not (SidePanelSection.Mods or SidePanelSection.Skin))
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source &&
            (IsDescendantOf(source, RecommendedModsListBox) ||
             IsDescendantOf(source, ModsListBox)))
        {
            return;
        }

        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject? ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void UsernameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCurrentNicknameDisplay();

        if (FriendsProfileNicknameTextBlock != null)
        {
            var nickname = GetActiveNickname() ?? UsernameTextBox.Text.Trim();
            FriendsProfileNicknameTextBlock.Text = $"Ник: {(string.IsNullOrWhiteSpace(nickname) ? "Player123" : nickname)}";
        }

        RefreshFriendsProfileAvatarPreview();
    }
}



