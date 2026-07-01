using System.IO;
using System.Text.Json;
using System.Threading;
using System.Net.Http;
using VesperLauncher.Launcher;
using System.Linq;
using VesperLauncher.Core;
using VesperLauncher.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace VesperLauncher.PhotinoHost;

internal sealed class LauncherFallbackBackendHost : ILauncherBackendHost
{
    private const int MaxConcurrentLoaderMetadataRequests = 8;
    private static readonly SemaphoreSlim LoaderMetadataRequestGate = new(MaxConcurrentLoaderMetadataRequests);
    private readonly IPlatformService _platform;
    private readonly AccountFriendsService _accountFriends;
    private readonly MinecraftLauncherService _minecraftLauncher = new();
    private readonly VersionStateMachine _versionStateMachine = new();
    private readonly TaskCompletionSource<bool> _launcherReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private bool _launcherReady;
    private string _activeSection = "none";
    private string _activeSettingsTab = "launcher";
    private string _statusText;
    private string _progressText;
    private string _selectedVersionKey = string.Empty;
    private MinecraftVersionCatalog _versionCatalog;
    private IReadOnlyList<LauncherVersionOption> _versionOptions = Array.Empty<LauncherVersionOption>();
    private string? _installingVersionKey;
    private double? _installingVersionProgress;
    private int _loaderMetadataRefreshStarted;
    private int _versionOptionsRevision;
    private int _memoryMb = 4096;
    private bool _autoMemory = true;
    private bool _showSnapshotVersions;
    private bool _showBetaVersions;
    private bool _showAlphaVersions;
    private string _javaRuntimeMode = "auto";
    private bool _showJvmArgs;
    private string _javaPath = string.Empty;
    private string _extraJvmArgs = string.Empty;
    private string _modsSearch = string.Empty;
    private string _modsCategory = "Все";
    private readonly HashSet<string> _favoriteProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedProjectIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isModsLoading;
    private string _modsProvider = "modrinth";
    private VesperGuestRelayTunnel? _activeGuestTunnel;
    private string _modelPreferenceId = "auto";
    private string? _selectedSkinFileName;
    private LauncherUpdateUiState _updateState = new()
    {
        Message = "Проверяем обновления...",
        DetailMessage = "Подготовка лаунчера...",
        IsIndeterminate = true,
        ProgressText = "Проверка..."
    };
    private readonly ProcessMonitor _gameProcessMonitor = new();

    public LauncherFallbackBackendHost(IPlatformService platform)
    {
        _platform = platform;
        _accountFriends = new AccountFriendsService(platform);
        _versionCatalog = MinecraftVersionManifestService
            .LoadFallbackCatalogForStartup();
        _versionOptions = BuildFallbackVersionOptions(_versionCatalog);
        _statusText = "Запуск лаунчера...";
        _progressText = "Проверяем обновления перед стартом.";

        _gameProcessMonitor.ProcessExited += (sender, e) =>
        {
            _ = DisposeActiveGuestTunnelAsync();
            lock (_sync)
            {
                _statusText = "Готов к запуску";
                _progressText = "Ожидание...";
            }
            BroadcastSnapshot();
        };
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            await RunStartupPipelineAsync().ConfigureAwait(false);
            BroadcastSnapshot();
        });
    }

    public Task<bool> WaitForLauncherReadyAsync()
    {
        return _launcherReadyTcs.Task;
    }

    public Task<object> GetSnapshotAsync()
    {
        lock (_sync)
        {
            return Task.FromResult<object>(new
            {
                phase = _launcherReady ? "ready" : "startup",
                errorMessage = (string?)null,
                update = new
                {
                    message = _updateState.Message,
                    detailMessage = _updateState.DetailMessage,
                    progressPercent = _updateState.ProgressPercent,
                    isIndeterminate = _updateState.IsIndeterminate,
                    progressText = _updateState.ProgressText
                },
                launcher = BuildLauncherSnapshot()
            });
        }
    }

    public Task ExecuteCommandAsync(string command, JsonElement payload)
    {
        if (string.Equals((command ?? string.Empty).Trim(), "account.submit", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteAccountSubmitAsync(payload);
        }

        if (string.Equals((command ?? string.Empty).Trim(), "account.logout", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteAccountLogoutAsync();
        }

        if (string.Equals((command ?? string.Empty).Trim(), "main.launch", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteInstallSelectedVersionAsync();
        }

        if (string.Equals((command ?? string.Empty).Trim(), "friends.connect", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteFriendsConnectAsync(payload);
        }

        lock (_sync)
        {
            ApplyCommand(command, payload);
            BroadcastSnapshot();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _accountFriends.StopSyncLoop();
        _ = DisposeActiveGuestTunnelAsync();
        _launcherReadyTcs.TrySetResult(false);
    }

    public event Action? SnapshotBroadcasted;
    private void BroadcastSnapshot() { SnapshotBroadcasted?.Invoke(); }
    private async Task RunStartupPipelineAsync()
    {
        try
        {
            await RefreshVersionCatalogAsync(includeLoaderOptions: false).ConfigureAwait(false);

            var autoUpdateService = new LauncherAutoUpdateService(LogError, LogInfo);
            autoUpdateService.UiStateChanged += state =>
            {
                lock (_sync)
                {
                    _updateState = state;
                    _statusText = state.Message;
                    _progressText = state.DetailMessage ?? string.Empty;
                }

                BroadcastSnapshot();
            };
            autoUpdateService.FallbackLaunchRequested += () =>
            {
                lock (_sync)
                {
                    _launcherReady = true;
                }

                _launcherReadyTcs.TrySetResult(true);
                StartLoaderOptionsRefreshInBackground();
                BroadcastSnapshot();
            };

            var shouldLaunch = await autoUpdateService.RunBeforeLaunchAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _launcherReady = shouldLaunch;
            }

            _launcherReadyTcs.TrySetResult(shouldLaunch);
            if (shouldLaunch)
            {
                StartLoaderOptionsRefreshInBackground();
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "Cross-platform startup update pipeline failed.");
            lock (_sync)
            {
                _updateState = new LauncherUpdateUiState
                {
                    Message = "Запускаем лаунчер...",
                    DetailMessage = "Проверка обновлений не удалась.",
                    ProgressPercent = 100,
                    IsIndeterminate = false,
                    ProgressText = "Готово"
                };
                _statusText = _updateState.Message;
                _progressText = _updateState.DetailMessage ?? string.Empty;
                _launcherReady = true;
            }

            _launcherReadyTcs.TrySetResult(true);
            BroadcastSnapshot();
        }
    }

    private static void LogInfo(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "photino-host.log"), $"[{DateTime.Now:O}] INFO {message}{Environment.NewLine}"); } catch { }
    }

    private static void LogError(Exception exception, string title)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "photino-host.log"), $"[{DateTime.Now:O}] ERROR {title}{Environment.NewLine}{exception}{Environment.NewLine}"); } catch { }
    }

    private object BuildLauncherSnapshot()
    {
        var friends = _accountFriends.CreateFriendsSnapshot();
        return new
        {
            activeSection = _activeSection,
            activeSettingsTab = _activeSettingsTab,
            isBusy = false,
            isGameRunning = false,
            canAccessFriends = false,
            notificationsCount = friends.IncomingRequests.Count,
            theme = BuildThemeSnapshot(),
            main = BuildMainSnapshot(),
            account = BuildAccountSnapshot(),
            settings = BuildSettingsSnapshot(),
            skin = BuildSkinSnapshot(),
            background = BuildBackgroundSnapshot(),
            mods = BuildModsSnapshot(),
            friends
        };
    }

    private object BuildThemeSnapshot()
    {
        var assetsRoot = ResolveAssetsRoot();
        var hour = DateTime.Now.Hour;
        string preferredBg;
        if (hour >= 6 && hour < 12)
        {
            preferredBg = "bg-sunrise.png";
        }
        else if (hour >= 12 && hour < 18)
        {
            preferredBg = "bg-day.png";
        }
        else if (hour >= 18 && hour < 22)
        {
            preferredBg = "bg-sunset.png";
        }
        else
        {
            preferredBg = "bg-night.png";
        }

        return new
        {
            title = "Vesper Launcher",
            iconUrl = ToLauncherFileUrl(FindFirstExisting(assetsRoot, "vesper-app.ico")),
            logoUrl = ToLauncherFileUrl(FindFirstExisting(assetsRoot, "vesper-logo.png", "V.png", "vesper-app.png")),
            wordmarkUrl = ToLauncherFileUrl(FindFirstExisting(assetsRoot, "vesper-launcher-wordmark.png", "vesper-logo.png")),
            backgroundUrl = ToLauncherFileUrl(FindFirstExisting(assetsRoot, preferredBg, "bg-day.png", "vesper-menu-art.jpg", "background.png")),
            glassTone = "light"
        };
    }

    private object BuildMainSnapshot()
    {
        var account = _accountFriends.CreateAccountSnapshot();
        var nickname = account.CurrentNickname;
        var selectedVersion = ResolveSelectedVersionOption();
        var selectedVersionId = selectedVersion.MinecraftVersionId;
        var selectedVersionKey = selectedVersion.Key;
        var selectedVersionLabel = selectedVersion.DisplayName;
        var profilePath = _minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId);
        return new
        {
            nickname,
            usernameText = nickname,
            launchButtonText = BuildLaunchButtonText(selectedVersion),
            statusText = _statusText,
            progressText = _progressText,
            progressOverlayText = "Готово",
            progressPercent = _installingVersionProgress ?? 0,
            isProgressIndeterminate = _installingVersionKey is not null && !_installingVersionProgress.HasValue,
            selectedVersionKey,
            selectedVersionId,
            selectedVersionLabel,
            inlineVersionLabel = selectedVersionLabel,
            quickVersionHint = "Photino shell",
            canLaunch = true,
            canOpenProfileFolder = true,
            hasLaunchIdentity = !string.IsNullOrWhiteSpace(nickname),
            profilePath,
            savedUsernames = account.RecentUsernames,
            displayedMemoryMb = _memoryMb,
            availableVersions = _versionOptions.Select(option =>
            {
                var isSelected = string.Equals(option.Key, selectedVersionKey, StringComparison.OrdinalIgnoreCase);
                var isDownloading = string.Equals(_installingVersionKey, option.Key, StringComparison.OrdinalIgnoreCase);
                var state = isSelected || isDownloading
                    ? GetVersionState(option)
                    : VersionState.NotInstalled;
                return new
                {
                    key = option.Key,
                    displayName = option.DisplayName,
                    baseVersionId = option.MinecraftVersionId,
                    versionId = option.InstalledVersionId,
                    availabilityNote = option.AvailabilityNote,
                    isSelected,
                    isInstalled = state.State == VersionInstallState.Installed,
                    installState = state.State.ToString(),
                    actionText = state.ButtonText,
                    loaders = option.LoaderKind is null ? Array.Empty<string>() : new[] { option.LoaderKind },
                    subtitle = option.Subtitle
                };
            }).ToArray()
        };
    }

    private object BuildAccountSnapshot()
    {
        return _accountFriends.CreateAccountSnapshot();
    }

    private object BuildSettingsSnapshot()
    {
        return new
        {
            activeTab = _activeSettingsTab,
            tabs = new[]
            {
                new { id = "launcher", label = "Лаунчер" },
                new { id = "java", label = "Java" },
                new { id = "vesper", label = "Vesper" },
                new { id = "launch", label = "Запуск" },
                new { id = "language", label = "Язык" },
                new { id = "glass", label = "Стекло" }
            },
            useSystemJava = _javaRuntimeMode == "system",
            javaPath = _javaPath,
            effectiveJavaPath = _javaRuntimeMode == "custom" ? _javaPath : (_javaRuntimeMode == "system" ? "java" : "download"),
            memoryMb = _memoryMb,
            displayedMemoryMb = _memoryMb,
            minimumMemoryMb = 1024,
            maximumMemoryMb = 12288,
            showJvmArgs = _showJvmArgs,
            extraJvmArgs = _extraJvmArgs,
            autoOptimizeMemory = _autoMemory,
            autoMinimizeOnLaunch = false,
            restoreLauncherAfterGameExit = true,
            clickSoundEnabled = false,
            showSnapshotVersions = _showSnapshotVersions,
            showBetaVersions = _showBetaVersions,
            showAlphaVersions = _showAlphaVersions,
            minecraftLanguageCode = "auto",
            loginFormPlacementId = "center",
            launcherDirectoryViewId = "current",
            javaRuntimeMode = _javaRuntimeMode,
            javaModeHint = _javaRuntimeMode == "auto" ? "Лаунчер автоматически скачает совместимую Java Temurin (OpenJDK) для запуска выбранной версии Minecraft." : (_javaRuntimeMode == "system" ? "Будет использоваться Java из PATH (переменных окружения)." : "Будет использована Java по указанному пути."),
            jvmArgsHint = _showJvmArgs ? "Поле открыто." : "Поле скрыто.",
            autoMemoryHint = _autoMemory ? "Память подбирается автоматически." : "Память задана вручную.",
            autoMinimizeHint = "Будет подключено через MinecraftLauncher.",
            restoreHint = "Будет подключено через ProcessMonitor.",
            displayedGameDirectory = _platform.Paths.MinecraftDirectory,
            languageOptions = new[]
            {
                new { id = "auto", label = "Авто" },
                new { id = "ru_ru", label = "Русский" },
                new { id = "en_us", label = "English" }
            },
            loginPlacementOptions = new[]
            {
                new { id = "center", label = "Центр" },
                new { id = "left", label = "Слева" }
            },
            directoryViewOptions = new[]
            {
                new { id = "current", label = "Текущая" },
                new { id = "minecraft", label = ".minecraft" }
            },
            javaRuntimeOptions = new[]
            {
                new { id = "system", label = "Системная Java" },
                new { id = "custom", label = "Свой путь" }
            },
            memoryPresets = new[] { 4096, 6144, 8192 }
        };
    }

    private object BuildSkinSnapshot()
    {
        var skinsDir = _platform.Paths.GetSkinsCacheDirectory();
        var files = Directory.Exists(skinsDir)
            ? Directory.GetFiles(skinsDir, "*.png")
            : Array.Empty<string>();

        var availableSkins = files.Select(filePath =>
        {
            var fileName = Path.GetFileName(filePath);
            return new
            {
                fileName,
                isSelected = string.Equals(fileName, _selectedSkinFileName, StringComparison.OrdinalIgnoreCase)
            };
        }).ToArray();



        string selectedSkinUrl = string.Empty;
        string selectedSkinLabel = "Скин не выбран.";
        bool isSlim = false;

        if (!string.IsNullOrWhiteSpace(_selectedSkinFileName))
        {
            var selectedPath = Path.Combine(skinsDir, _selectedSkinFileName);
            if (File.Exists(selectedPath))
            {
                selectedSkinUrl = ToLauncherFileUrl(selectedPath);
                selectedSkinLabel = _selectedSkinFileName;
            }
        }
        else
        {
            var defaultStevePath = Path.Combine(ResolveAssetsRoot(), "steve.png");
            if (File.Exists(defaultStevePath))
            {
                selectedSkinUrl = "/launcher-assets/steve.png";
                selectedSkinLabel = "Скин не выбран.";
            }
        }

        if (_modelPreferenceId == "slim")
        {
            isSlim = true;
        }
        else if (_modelPreferenceId == "classic")
        {
            isSlim = false;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_selectedSkinFileName))
            {
                var selectedPath = Path.Combine(skinsDir, _selectedSkinFileName);
                isSlim = IsSlimSkin(selectedPath);
            }
        }

        return new
        {
            selectedSkinFileName = _selectedSkinFileName ?? string.Empty,
            selectedSkinUrl,
            selectedSkinPreviewUrl = selectedSkinUrl,
            selectedSkinLabel,
            selectedSkinIsSlim = isSlim,
            modelPreferenceId = _modelPreferenceId,
            skinsDirectory = skinsDir,
            availableSkins,
            modelOptions = new[]
            {
                new { id = "auto", label = "Авто", isSelected = _modelPreferenceId == "auto" },
                new { id = "classic", label = "Classic", isSelected = _modelPreferenceId == "classic" },
                new { id = "slim", label = "Slim", isSelected = _modelPreferenceId == "slim" }
            }
        };
    }

    private static bool IsSlimSkin(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            using var image = Image.Load<Rgba32>(filePath);
            if (image.Width == 64 && image.Height == 64)
            {
                var pixel = image[47, 24];
                return pixel.A == 0;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private object BuildBackgroundSnapshot()
    {
        return new
        {
            currentPresetId = "default",
            currentPresetLabel = "Стандартный",
            appliedBackgroundUrl = string.Empty,
            backgroundsDirectory = Path.Combine(ResolveAssetsRoot(), "Backgrounds"),
            items = Array.Empty<object>()
        };
    }

    private IReadOnlyList<RecommendedModCatalogItem> GetModsCatalogItems(
        string category,
        LauncherVersionOption selectedVersion,
        string searchQuery)
    {
        var loaderKindStr = selectedVersion.LoaderKind;
        if (string.IsNullOrWhiteSpace(loaderKindStr))
        {
            return Array.Empty<RecommendedModCatalogItem>();
        }

        ModLoaderKind loaderKind = loaderKindStr.Equals("Fabric", StringComparison.OrdinalIgnoreCase) 
            ? ModLoaderKind.Fabric 
            : ModLoaderKind.Forge;

        RecommendedCatalogContentKind contentKind = category switch
        {
            "Ресурспаки" => RecommendedCatalogContentKind.ResourcePack,
            "Сборки" => RecommendedCatalogContentKind.Modpack,
            "Шейдеры" => RecommendedCatalogContentKind.Shader,
            _ => RecommendedCatalogContentKind.Mod
        };

        if (_modsProvider == "curseforge")
        {
            // Return curated CurseForge items
            var cfItems = new List<RecommendedModCatalogItem>();
            if (contentKind == RecommendedCatalogContentKind.Mod)
            {
                cfItems.AddRange(new[]
                {
                    new RecommendedModCatalogItem("cf-jei", "Just Enough Items (JEI)", "Позволяет просматривать рецепты крафта и все доступные предметы прямо в инвентаре.", null, "CurseForge • Инвентарь", RecommendedCatalogContentKind.Mod, SourceIconUrl: "https://cdn.modrinth.com/data/u6dRKJwZ/4a3f18ac0d096c9f8e9176984c44be4e58f94c89_96.webp", BadgeText: "CurseForge", Downloads: 248500000, Followers: 154000),
                    new RecommendedModCatalogItem("cf-journeymap", "JourneyMap", "Отображает карту мира в реальном времени, миникарту на экране и позволяет ставить метки.", null, "CurseForge • Навигация", RecommendedCatalogContentKind.Mod, SourceIconUrl: "https://cdn.modrinth.com/data/lfHFW1mp/a1c571a21a88f6fa59eab67829f216f65ab393ee_96.webp", BadgeText: "CurseForge", Downloads: 142300000, Followers: 92000),
                    new RecommendedModCatalogItem("cf-bop", "Biomes O' Plenty", "Добавляет огромное количество уникальных биомов с новыми деревьями, цветами и блоками.", null, "CurseForge • Мир", RecommendedCatalogContentKind.Mod, SourceIconUrl: "https://cdn.modrinth.com/data/HXF82T3G/ffb870e12c325b795d54833f8f899126553ef06f.png", BadgeText: "CurseForge", Downloads: 118700000, Followers: 78000),
                    new RecommendedModCatalogItem("cf-ironchests", "Iron Chests", "Добавляет улучшенные сундуки (железный, золотой, алмазный) с увеличенной вместимостью.", null, "CurseForge • Хранение", RecommendedCatalogContentKind.Mod, SourceIconUrl: "https://cdn.modrinth.com/data/n2de3t2z/6a17c192e399211a9a0b5c31ec75f5fc073ca7b6.png", BadgeText: "CurseForge", Downloads: 94200000, Followers: 54000),
                    new RecommendedModCatalogItem("cf-tweaks", "Mouse Tweaks", "Облегчает сортировку инвентаря и перемещение предметов с помощью зажатой кнопки мыши.", null, "CurseForge • Удобство", RecommendedCatalogContentKind.Mod, SourceIconUrl: "https://cdn.modrinth.com/data/aC3cM3Vq/6c0eaa4e60a9c87f4766f222ff63286f09da32c0_96.webp", BadgeText: "CurseForge", Downloads: 128400000, Followers: 68000),
                    new RecommendedModCatalogItem("cf-clumps", "Clumps", "Объединяет висящие сферы опыта в одну крупную, существенно уменьшая лаги в игре.", null, "CurseForge • Оптимизация", RecommendedCatalogContentKind.Mod, SourceIconUrl: "https://cdn.modrinth.com/data/Wnxd13zP/6a965bb7974c3e759a53a1c89c35de4acd4cf86a_96.webp", BadgeText: "CurseForge", Downloads: 105100000, Followers: 61000)
                });
            }
            else if (contentKind == RecommendedCatalogContentKind.ResourcePack)
            {
                cfItems.AddRange(new[]
                {
                    new RecommendedModCatalogItem("cf-faithful", "Faithful 32x", "Оригинальные текстуры игры в более высоком и детализированном качестве.", null, "CurseForge • Ресурспаки", RecommendedCatalogContentKind.ResourcePack, SourceIconUrl: "https://cdn.modrinth.com/data/w0TnApzs/e8403d1fb2f55321ae74402c1e8c90a3a5670856.png", BadgeText: "CurseForge", Downloads: 85400000, Followers: 42000),
                    new RecommendedModCatalogItem("cf-sphax", "PureBDcraft", "Комиксовый и яркий стиль текстур, полностью преображающий мир Minecraft.", null, "CurseForge • Ресурспаки", RecommendedCatalogContentKind.ResourcePack, SourceIconUrl: "https://bdcraft.net/favicon.ico", BadgeText: "CurseForge", Downloads: 46100000, Followers: 31000)
                });
            }
            else if (contentKind == RecommendedCatalogContentKind.Shader)
            {
                cfItems.AddRange(new[]
                {
                    new RecommendedModCatalogItem("cf-bsl", "BSL Shaders", "Популярные шейдеры с мягким реалистичным освещением, туманом и красивой водой.", null, "CurseForge • Шейдеры", RecommendedCatalogContentKind.Shader, SourceIconUrl: "https://cdn.modrinth.com/data/Q1vvjJYV/2a611a3cb434fb52fb81fa5dace13c5d8b67e55d_96.webp", BadgeText: "CurseForge", Downloads: 38200000, Followers: 29000),
                    new RecommendedModCatalogItem("cf-complimentary", "Complementary Shaders", "Идеальный баланс производительности и красоты, сохраняющий дух ванили.", null, "CurseForge • Шейдеры", RecommendedCatalogContentKind.Shader, SourceIconUrl: "https://cdn.modrinth.com/data/HVnmMxH1/79cb7c8123bbc54945305b2ebad6b8881efdf5f8_96.webp", BadgeText: "CurseForge", Downloads: 52700000, Followers: 38000)
                });
            }
            else if (contentKind == RecommendedCatalogContentKind.Modpack)
            {
                cfItems.AddRange(new[]
                {
                    new RecommendedModCatalogItem("cf-rlcraft", "RLCraft", "Сверхсложная сборка на выживание с драконами, фэнтези и реалистичной физикой.", null, "CurseForge • Сборки", RecommendedCatalogContentKind.Modpack, SourceIconUrl: "https://cdn.modrinth.com/data/Qx4KOI2G/6bce4b7f4a25a49e23d57fcc6838a1c46b0aff72_96.webp", BadgeText: "CurseForge", Downloads: 18500000, Followers: 19000),
                    new RecommendedModCatalogItem("cf-skyfactory", "SkyFactory 4", "Популярный индустриальный скайблок с огромным деревом достижений.", null, "CurseForge • Сборки", RecommendedCatalogContentKind.Modpack, SourceIconUrl: "https://static.wikia.nocookie.net/minecraft_gamepedia/images/1/15/Grass_Block_JE4.png", BadgeText: "CurseForge", Downloads: 14200000, Followers: 15000)
                });
            }

            var destinationDirCF = contentKind switch
            {
                RecommendedCatalogContentKind.Shader => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "shaderpacks"),
                RecommendedCatalogContentKind.ResourcePack => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "resourcepacks"),
                RecommendedCatalogContentKind.Modpack => Path.Combine(_minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla), "modpacks"),
                _ => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods")
            };

            var cfItemsFiltered = new List<RecommendedModCatalogItem>();
            foreach (var item in cfItems)
            {
                if (!string.IsNullOrWhiteSpace(searchQuery))
                {
                    var query = searchQuery.Trim();
                    bool matches = item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   item.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!matches) continue;
                }

                // For mock, set resolved properties if missing
                var resolvedFileName = item.ProjectId + ".jar";
                if (contentKind == RecommendedCatalogContentKind.Shader) resolvedFileName = item.ProjectId + ".zip";
                if (contentKind == RecommendedCatalogContentKind.ResourcePack) resolvedFileName = item.ProjectId + ".zip";
                if (contentKind == RecommendedCatalogContentKind.Modpack) resolvedFileName = item.ProjectId + ".zip";

                var itemWithResolved = item with
                {
                    ResolvedFileName = resolvedFileName,
                    ResolvedDownloadUrl = "https://mediafilez.forgecdn.net/files/mock/" + item.ProjectId,
                    ResolvedFileSha1 = string.Empty
                };

                bool isInstalled = false;
                if (!string.IsNullOrWhiteSpace(itemWithResolved.ResolvedFileName))
                {
                    var filePath = Path.Combine(destinationDirCF, itemWithResolved.ResolvedFileName);
                    isInstalled = File.Exists(filePath);
                }

                var actionText = isInstalled 
                    ? "Удалить" 
                    : (contentKind == RecommendedCatalogContentKind.Modpack ? "Установить" : "Скачать");

                cfItemsFiltered.Add(itemWithResolved with
                {
                    IsInstalled = isInstalled,
                    ActionText = actionText,
                    IsFavorite = _favoriteProjectIds.Contains(item.ProjectId)
                });
            }

            return cfItemsFiltered;
        }

        try
        {
            var task = _minecraftLauncher.GetRecommendedCatalogAsync(
                contentKind,
                loaderKind,
                selectedVersion.MinecraftVersionId,
                CancellationToken.None);
            
            var catalogItems = task.GetAwaiter().GetResult();

            var destinationDir = contentKind switch
            {
                RecommendedCatalogContentKind.Shader => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "shaderpacks"),
                RecommendedCatalogContentKind.ResourcePack => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "resourcepacks"),
                RecommendedCatalogContentKind.Modpack => Path.Combine(_minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla), "modpacks"),
                _ => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods")
            };

            var items = new List<RecommendedModCatalogItem>();
            foreach (var item in catalogItems)
            {
                if (!string.IsNullOrWhiteSpace(searchQuery))
                {
                    var query = searchQuery.Trim();
                    bool matches = item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                   item.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        continue;
                    }
                }

                bool isInstalled = false;
                if (!string.IsNullOrWhiteSpace(item.ResolvedFileName))
                {
                    var filePath = Path.Combine(destinationDir, item.ResolvedFileName);
                    isInstalled = File.Exists(filePath);
                }

                var actionText = isInstalled 
                    ? "Удалить" 
                    : (contentKind == RecommendedCatalogContentKind.Modpack ? "Установить" : "Скачать");

                items.Add(item with 
                { 
                    IsInstalled = isInstalled,
                    ActionText = actionText,
                    IsFavorite = _favoriteProjectIds.Contains(item.ProjectId)
                });
            }

            return items;
        }
        catch (Exception ex)
        {
            LogError(ex, "Failed to load mods catalog.");
            return Array.Empty<RecommendedModCatalogItem>();
        }
    }

    private object BuildModsSnapshot()
    {
        var selectedVersion = ResolveSelectedVersionOption();
        var selectedState = GetVersionState(selectedVersion);
        var isInstalled = selectedState.State == VersionInstallState.Installed;
        
        var category = string.IsNullOrWhiteSpace(_modsCategory) ? "Моды" : _modsCategory;

        var catalogItems = GetModsCatalogItems(category, selectedVersion, _modsSearch);

        var destDir = Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods");
        var installedFiles = new List<string>();
        try
        {
            if (Directory.Exists(destDir))
            {
                installedFiles.AddRange(Directory.GetFiles(destDir).Select(Path.GetFileName));
            }
            var shadersDir = Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "shaderpacks");
            if (Directory.Exists(shadersDir))
            {
                installedFiles.AddRange(Directory.GetFiles(shadersDir).Select(Path.GetFileName));
            }
            var resourcepacksDir = Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "resourcepacks");
            if (Directory.Exists(resourcepacksDir))
            {
                installedFiles.AddRange(Directory.GetFiles(resourcepacksDir).Select(Path.GetFileName));
            }
        }
        catch { }

        return new
        {
            summary = isInstalled ? "Каталог модов загружен." : "Выберите и установите Fabric/Forge версию.",
            catalogSummary = isInstalled ? "Каталог готов к установке." : "Не установлен modloader.",
            targetFolderHint = Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods"),
            searchQuery = _modsSearch,
            selectedCategory = _modsCategory,
            categories = new[] { "Моды", "Ресурспаки", "Сборки", "Шейдеры" },
            isRefreshing = false,
            isCatalogLoading = _isModsLoading,
            canInstallSelected = false,
            installedModsCount = catalogItems.Count(i => i.IsInstalled),
            selectedProjectIds = _selectedProjectIds.ToArray(),
            modsDirectory = Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods"),
            provider = _modsProvider,
            installedFileNames = installedFiles.ToArray(),
            items = catalogItems.Select(item => new {
                projectId = item.ProjectId,
                displayName = item.DisplayName,
                description = item.Description,
                iconUrl = item.ProjectId.StartsWith("cf-") ? GetCachedIconPath(item.ProjectId, item.SourceIconUrl) : item.IconUrl,
                packSummary = item.PackSummary,
                contentKind = item.ContentKind.ToString().ToLowerInvariant(),
                actionText = item.ActionText,
                sourceIconUrl = item.SourceIconUrl,
                badgeText = item.BadgeText,
                badgeBackgroundHex = item.BadgeBackgroundHex,
                badgeForegroundHex = item.BadgeForegroundHex,
                isFavorite = item.IsFavorite,
                isInstalled = item.IsInstalled,
                downloads = item.Downloads,
                followers = item.Followers
            }).ToArray()
        };
    }

    private object BuildFriendsSnapshot()
    {
        return _accountFriends.CreateFriendsSnapshot();
    }

    private async Task RefreshVersionCatalogAsync(bool includeLoaderOptions)
    {
        var service = new MinecraftVersionManifestService(_platform);
        var catalog = await service.LoadAsync().ConfigureAwait(false);
        var filteredCatalog = FilterVersionCatalog(catalog);
        var options = await BuildVersionOptionsAsync(filteredCatalog, includeLoaderOptions).ConfigureAwait(false);
        lock (_sync)
        {
            _versionCatalog = catalog;
            _versionOptions = options;
            if (string.IsNullOrWhiteSpace(_selectedVersionKey) ||
                !_versionOptions.Any(option => string.Equals(option.Key, _selectedVersionKey, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedVersionKey = _versionOptions.FirstOrDefault()?.Key ?? BuildVersionKey(filteredCatalog.LatestRelease);
            }
        }

        BroadcastSnapshot();
    }

    private async Task<IReadOnlyList<LauncherVersionOption>> BuildVersionOptionsAsync(
        MinecraftVersionCatalog catalog,
        bool includeLoaderOptions)
    {
        var result = new List<LauncherVersionOption>();
        foreach (var version in catalog.Versions)
        {
            result.Add(CreateVanillaOption(version));
        }

        if (!includeLoaderOptions)
        {
            return result.Count > 0 ? result : BuildFallbackVersionOptions(catalog);
        }

        var loaderTasks = catalog.Releases
            .SelectMany(minecraftVersionId => new[]
            {
                TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.Fabric),
                TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.Forge),
                TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.NeoForge),
                TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.OptiFine)
            })
            .ToArray();

        var loaderOptions = await Task.WhenAll(loaderTasks).ConfigureAwait(false);
        var loaderOptionsByMinecraftVersion = BuildLoaderOptionMap(loaderOptions.OfType<LauncherVersionOption>());

        result = BuildVersionOptionsFromLoaderMap(catalog, loaderOptionsByMinecraftVersion).ToList();

        return result.Count > 0 ? result : BuildFallbackVersionOptions(catalog);
    }

    private static IReadOnlyList<LauncherVersionOption> BuildVersionOptionsFromLoaderMap(
        MinecraftVersionCatalog catalog,
        IReadOnlyDictionary<string, List<LauncherVersionOption>> loaderOptionsByMinecraftVersion)
    {
        var result = new List<LauncherVersionOption>();
        foreach (var version in catalog.Versions)
        {
            result.Add(CreateVanillaOption(version));
            if (loaderOptionsByMinecraftVersion.TryGetValue(version.Id, out var matchingLoaderOptions))
            {
                result.AddRange(matchingLoaderOptions.OrderBy(option => GetLoaderSortOrder(option.LoaderKind)));
            }
        }

        return result;
    }

    private IReadOnlyDictionary<string, List<LauncherVersionOption>> BuildLoaderOptionMap(
        IEnumerable<LauncherVersionOption> loaderOptions)
    {
        return loaderOptions
            .GroupBy(option => option.MinecraftVersionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => ExpandCompositeLoaderOptions(group.Key, group)
                    .OrderBy(option => GetLoaderSortOrder(option.LoaderKind))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<LauncherVersionOption> ExpandCompositeLoaderOptions(
        string minecraftVersionId,
        IEnumerable<LauncherVersionOption> loaderOptions)
    {
        var options = loaderOptions
            .Where(option => option is not null)
            .ToList();

        foreach (var option in options)
        {
            yield return option;
        }

        var optiFine = options.FirstOrDefault(option => IsLoaderKind(option, "OptiFine"));
        if (optiFine is null || string.IsNullOrWhiteSpace(optiFine.LoaderVersionId))
        {
            yield break;
        }

        var forge = options.FirstOrDefault(option => IsLoaderKind(option, "Forge"));
        if (forge is not null && !string.IsNullOrWhiteSpace(forge.LoaderVersionId))
        {
            yield return CreateCompositeLoaderOption(
                minecraftVersionId,
                forge,
                optiFine,
                "Forge+OptiFine",
                "Forge + OptiFine",
                "Forge + OptiFine");
        }

        var fabric = _minecraftLauncher.IsFabricOptiFineSupported(minecraftVersionId)
            ? options.FirstOrDefault(option => IsLoaderKind(option, "Fabric"))
            : null;
        if (fabric is not null && !string.IsNullOrWhiteSpace(fabric.LoaderVersionId))
        {
            yield return CreateCompositeLoaderOption(
                minecraftVersionId,
                fabric,
                optiFine,
                "Fabric+OptiFine",
                "Fabric + OptiFine",
                "Fabric + OptiFine");
        }
    }

    private static bool IsLoaderKind(LauncherVersionOption option, string loaderKind)
    {
        return string.Equals(option.LoaderKind, loaderKind, StringComparison.OrdinalIgnoreCase);
    }

    private static LauncherVersionOption CreateCompositeLoaderOption(
        string minecraftVersionId,
        LauncherVersionOption hostLoader,
        LauncherVersionOption optiFine,
        string loaderKind,
        string displayLoaderName,
        string subtitle)
    {
        var hostLoaderKey = (hostLoader.LoaderKind ?? displayLoaderName).ToLowerInvariant().Replace("+", "-");
        return new LauncherVersionOption(
            $"{hostLoaderKey}-optifine-{minecraftVersionId}-{hostLoader.LoaderVersionId}-{optiFine.LoaderVersionId}",
            minecraftVersionId,
            "release",
            string.Empty,
            string.Empty,
            hostLoader.ReleaseTime > optiFine.ReleaseTime ? hostLoader.ReleaseTime : optiFine.ReleaseTime,
            loaderKind,
            CombineCompositeLoaderVersionIds(hostLoader.LoaderVersionId ?? string.Empty, optiFine.LoaderVersionId ?? string.Empty),
            hostLoader.InstalledVersionId,
            hostLoader.AlternateInstalledVersionId,
            $"{minecraftVersionId} - {displayLoaderName}",
            subtitle,
            $"{displayLoaderName}: {hostLoader.LoaderVersionId} + OptiFine {optiFine.LoaderVersionId}.");
    }

    private static string CombineCompositeLoaderVersionIds(string hostLoaderVersionId, string optiFineVersionId)
    {
        return $"{hostLoaderVersionId}||{optiFineVersionId}";
    }

    private static int GetLoaderSortOrder(string? loaderKind)
    {
        return loaderKind?.ToLowerInvariant() switch
        {
            "fabric" => 1,
            "forge" => 2,
            "neoforge" => 3,
            "optifine" => 4,
            "forge+optifine" => 5,
            "fabric+optifine" => 6,
            _ => 4
        };
    }

    private MinecraftVersionCatalog FilterVersionCatalog(MinecraftVersionCatalog catalog)
    {
        var versions = catalog.Versions
            .Where(ShouldShowVersionType)
            .ToArray();

        if (versions.Length == 0)
        {
            versions = catalog.Versions
                .Where(version => version.IsRelease)
                .ToArray();
        }

        if (versions.Length == 0)
        {
            return MinecraftVersionManifestService.LoadFallbackCatalogForStartup();
        }

        var latestRelease = versions.FirstOrDefault(
                version => string.Equals(version.Id, catalog.LatestRelease, StringComparison.OrdinalIgnoreCase))
            ?.Id ?? versions.First().Id;

        return new MinecraftVersionCatalog(latestRelease, versions);
    }

    private bool ShouldShowVersionType(MinecraftVersionCatalogEntry version)
    {
        return version.Type.ToLowerInvariant() switch
        {
            "release" => true,
            "snapshot" => _showSnapshotVersions,
            "old_beta" => _showBetaVersions,
            "old_alpha" => _showAlphaVersions,
            _ => false
        };
    }

    private void StartLoaderOptionsRefreshInBackground()
    {
        if (Interlocked.Exchange(ref _loaderMetadataRefreshStarted, 1) == 1)
        {
            return;
        }

        var revision = Volatile.Read(ref _versionOptionsRevision);
        _ = Task.Run(async () =>
        {
            try
            {
                MinecraftVersionCatalog catalog;
                string selectedMcVersion = "1.16.5";
                lock (_sync)
                {
                    catalog = FilterVersionCatalog(_versionCatalog);
                    var selectedVersion = ResolveSelectedVersionOption();
                    if (selectedVersion != null)
                    {
                        selectedMcVersion = selectedVersion.MinecraftVersionId;
                    }
                }

                var targetVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    selectedMcVersion,
                    catalog.LatestRelease,
                    "1.21.1", "1.21", "1.20.1", "1.19.4", "1.19.2", "1.18.2", "1.16.5", "1.12.2", "1.8.9", "1.7.10"
                };

                try
                {
                    var instancesDir = Path.Combine(_platform.Paths.MinecraftDirectory, "instances");
                    if (Directory.Exists(instancesDir))
                    {
                        foreach (var dir in Directory.GetDirectories(instancesDir))
                        {
                            var name = Path.GetFileName(dir);
                            if (name.Contains('.') || Version.TryParse(name, out _))
                            {
                                targetVersions.Add(name);
                            }
                        }
                    }
                }
                catch { }

                var loaderOptionsByMinecraftVersion = new Dictionary<string, List<LauncherVersionOption>>(StringComparer.OrdinalIgnoreCase);
                foreach (var minecraftVersionId in catalog.Releases)
                {
                    if (!targetVersions.Contains(minecraftVersionId))
                    {
                        continue;
                    }

                    var loaderOptions = await Task.WhenAll(
                            TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.Fabric),
                            TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.Forge),
                            TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.NeoForge),
                            TryCreatePreferredLoaderOptionAsync(minecraftVersionId, ModLoaderKind.OptiFine))
                        .ConfigureAwait(false);

                    var foundOptions = ExpandCompositeLoaderOptions(minecraftVersionId, loaderOptions.OfType<LauncherVersionOption>())
                        .OrderBy(option => GetLoaderSortOrder(option.LoaderKind))
                        .ToList();

                    if (foundOptions.Count == 0)
                    {
                        continue;
                    }

                    loaderOptionsByMinecraftVersion[minecraftVersionId] = foundOptions;
                    lock (_sync)
                    {
                        if (revision != _versionOptionsRevision)
                        {
                            return;
                        }

                        _versionOptions = BuildVersionOptionsFromLoaderMap(catalog, loaderOptionsByMinecraftVersion);
                        if (string.IsNullOrWhiteSpace(_selectedVersionKey) ||
                            !_versionOptions.Any(option => string.Equals(option.Key, _selectedVersionKey, StringComparison.OrdinalIgnoreCase)))
                        {
                            _selectedVersionKey = _versionOptions.FirstOrDefault()?.Key ?? BuildVersionKey(catalog.LatestRelease);
                        }
                    }

                    BroadcastSnapshot();
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "Loader metadata refresh failed.");
            }
        });
    }

    private void RebuildVersionOptionsForCurrentFilters()
    {
        Interlocked.Increment(ref _versionOptionsRevision);
        Interlocked.Exchange(ref _loaderMetadataRefreshStarted, 0);

        var filteredCatalog = FilterVersionCatalog(_versionCatalog);
        _versionOptions = BuildFallbackVersionOptions(filteredCatalog);
        if (string.IsNullOrWhiteSpace(_selectedVersionKey) ||
            !_versionOptions.Any(option => string.Equals(option.Key, _selectedVersionKey, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedVersionKey = _versionOptions.FirstOrDefault()?.Key ?? BuildVersionKey(filteredCatalog.LatestRelease);
        }

        StartLoaderOptionsRefreshInBackground();
    }

    private async Task<LauncherVersionOption?> TryCreatePreferredLoaderOptionAsync(
        string minecraftVersionId,
        ModLoaderKind loaderKind)
    {
        if (loaderKind == ModLoaderKind.NeoForge &&
            !minecraftVersionId.StartsWith("1.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await LoaderMetadataRequestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var loaderVersions = await _minecraftLauncher.GetAvailableModLoaderVersionsAsync(
                minecraftVersionId,
                loaderKind).ConfigureAwait(false);
            var preferred = loaderKind == ModLoaderKind.OptiFine
                ? _minecraftLauncher.SelectPreferredOptiFineVersion(loaderVersions, ModLoaderKind.OptiFine)
                : loaderVersions.FirstOrDefault();
            if (preferred is null)
            {
                return null;
            }

            return CreateLoaderOption(minecraftVersionId, loaderKind, preferred);
        }
        catch
        {
            // Loader metadata can be unavailable; vanilla must still be usable.
            return null;
        }
        finally
        {
            LoaderMetadataRequestGate.Release();
        }
    }

    private static IReadOnlyList<LauncherVersionOption> BuildFallbackVersionOptions(MinecraftVersionCatalog catalog)
    {
        return catalog.Versions
            .Select(CreateVanillaOption)
            .ToArray();
    }

    private async Task ExecuteAccountSubmitAsync(JsonElement payload)
    {
        var mode = GetString(payload, "mode", "login");
        var username = GetString(payload, "username", string.Empty);
        var password = GetString(payload, "password", string.Empty);

        AccountSubmitResult submitResult;
        try
        {
            submitResult = await _accountFriends.SubmitAccountAsync(mode, username, password).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            submitResult = new AccountSubmitResult(false, "Ошибка входа Vesper: " + ex.Message);
        }

        lock (_sync)
        {
            _statusText = submitResult.Message;
            _progressText = submitResult.Success ? "Аккаунт Vesper подключен." : "Проверьте ник, пароль или доступность сервера.";
            if (submitResult.Success)
            {
                _activeSection = "none";
            }

            BroadcastSnapshot();
        }
    }

    private async Task ExecuteAccountLogoutAsync()
    {
        var result = await _accountFriends.LogoutAsync().ConfigureAwait(false);
        lock (_sync)
        {
            _statusText = result.Message;
            _progressText = "Сессия Vesper сброшена.";
            _activeSection = "account";
            BroadcastSnapshot();
        }
    }

    private async Task ExecuteInstallSelectedVersionAsync(string? directConnectAddress = null, int? directConnectPort = null)
    {
        if (_gameProcessMonitor.IsRunning)
        {
            lock (_sync)
            {
                _statusText = "Останавливаем игру...";
                _progressText = "Завершение процесса Minecraft...";
            }
            BroadcastSnapshot();

            await _gameProcessMonitor.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return;
        }

        LauncherVersionOption selectedVersion;
        VersionState selectedState;
        lock (_sync)
        {
            selectedVersion = ResolveSelectedVersionOption();
            selectedState = GetVersionState(selectedVersion);
            if (_installingVersionKey is not null)
            {
                return;
            }

            _installingVersionKey = selectedVersion.Key;
            _installingVersionProgress = null;
            _statusText = selectedState.State == VersionInstallState.Installed
                ? "Запускаем " + selectedVersion.DisplayName + "..."
                : "Подготавливаем " + selectedVersion.DisplayName + "...";
            _progressText = selectedVersion.AvailabilityNote;
            BroadcastSnapshot();
        }

        try
        {
            var progress = new Progress<LauncherProgress>(value =>
            {
                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(value.Stage))
                    {
                        _statusText = value.Stage;
                    }

                    _installingVersionProgress = value.Total > 0
                        ? Math.Clamp(value.Current / value.Total * 100d, 0d, 100d)
                        : null;
                    BroadcastSnapshot();
                }
            });

            var installedVersionId = selectedState.State == VersionInstallState.Installed
                ? ResolveInstalledVersionId(selectedVersion)
                : (selectedVersion.LoaderKind switch
                {
                    null => await _minecraftLauncher.InstallVanillaVersionAsync(
                        selectedVersion.MinecraftVersionId,
                        LauncherProfile.Vanilla,
                        progress).ConfigureAwait(false),
                    "Forge" => (await _minecraftLauncher.InstallModLoaderAsync(
                        selectedVersion.MinecraftVersionId,
                        ModLoaderKind.Forge,
                        selectedVersion.LoaderVersionId ?? string.Empty,
                        LauncherProfile.Vanilla,
                        progress).ConfigureAwait(false)).InstalledVersionId,
                    "Fabric" => (await _minecraftLauncher.InstallModLoaderAsync(
                        selectedVersion.MinecraftVersionId,
                        ModLoaderKind.Fabric,
                        selectedVersion.LoaderVersionId ?? string.Empty,
                        LauncherProfile.Vanilla,
                        progress).ConfigureAwait(false)).InstalledVersionId,
                    "NeoForge" => (await _minecraftLauncher.InstallNeoForgeVersionAsync(
                        selectedVersion.MinecraftVersionId,
                        selectedVersion.LoaderVersionId ?? string.Empty,
                        LauncherProfile.Vanilla,
                        progress).ConfigureAwait(false)).InstalledVersionId,
                    "OptiFine" => (await _minecraftLauncher.InstallModLoaderAsync(
                        selectedVersion.MinecraftVersionId,
                        ModLoaderKind.OptiFine,
                        selectedVersion.LoaderVersionId ?? string.Empty,
                        LauncherProfile.Vanilla,
                        progress).ConfigureAwait(false)).InstalledVersionId,
                    "Forge+OptiFine" => await InstallForgeOptiFineVersionAsync(
                        selectedVersion,
                        progress).ConfigureAwait(false),
                    "Fabric+OptiFine" => await InstallFabricOptiFineVersionAsync(
                        selectedVersion,
                        progress).ConfigureAwait(false),
                    _ => throw new NotSupportedException("Неизвестный загрузчик: " + selectedVersion.LoaderKind)
                });

            if (selectedState.State == VersionInstallState.Installed)
            {
                var nickname = _accountFriends.CreateAccountSnapshot().CurrentNickname.Trim();
                if (string.IsNullOrWhiteSpace(nickname))
                {
                    throw new InvalidOperationException("Введите ник перед запуском Minecraft.");
                }

                var gameDirectory = _minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla);
                var metadataPath = GetInstalledVersionJsonPath(installedVersionId);
                string? skinPath = null;
                bool isSlim = false;

                lock (_sync)
                {
                    if (!string.IsNullOrWhiteSpace(_selectedSkinFileName))
                    {
                        var skinsDir = _platform.Paths.GetSkinsCacheDirectory();
                        var fullPath = Path.Combine(skinsDir, _selectedSkinFileName);
                        if (File.Exists(fullPath))
                        {
                            skinPath = fullPath;
                            if (_modelPreferenceId == "slim")
                            {
                                isSlim = true;
                            }
                            else if (_modelPreferenceId == "classic")
                            {
                                isSlim = false;
                            }
                            else
                            {
                                isSlim = IsSlimSkin(fullPath);
                            }
                        }
                    }
                }

                var launchResult = await _minecraftLauncher.DownloadAndLaunchAsync(
                    new LaunchOptions
                    {
                        Username = nickname,
                        JavaExecutable = _javaRuntimeMode == "custom" ? (string.IsNullOrWhiteSpace(_javaPath) ? "download" : _javaPath) : (_javaRuntimeMode == "system" ? "java" : "download"),
                        Version = new MinecraftVersionEntry(
                            installedVersionId,
                            selectedVersion.MinecraftVersionType,
                            File.Exists(metadataPath)
                                ? new DateTimeOffset(File.GetLastWriteTimeUtc(metadataPath), TimeSpan.Zero)
                                : selectedVersion.ReleaseTime,
                            string.IsNullOrWhiteSpace(selectedVersion.MetadataUrl) ? "local" : selectedVersion.MetadataUrl,
                            selectedVersion.MetadataSha1,
                            LocalMetadataPath: metadataPath,
                            SourceGameDirectory: gameDirectory,
                            BaseVersionId: selectedVersion.MinecraftVersionId),
                        Profile = LauncherProfile.Vanilla,
                        MemoryMb = _memoryMb,
                        ExtraJvmArgs = _extraJvmArgs,
                        SelectedSkinPath = skinPath,
                        SelectedSkinIsSlim = isSlim,
                        DirectConnectServerAddress = directConnectAddress,
                        DirectConnectServerPort = directConnectPort
                    },
                    progress).ConfigureAwait(false);

                try
                {
                    if (launchResult.ProcessId.HasValue)
                    {
                        var process = System.Diagnostics.Process.GetProcessById(launchResult.ProcessId.Value);
                        _gameProcessMonitor.Attach(process);
                    }
                }
                catch (Exception ex)
                {
                    LogInfo("Не удалось отследить запущенную игру: " + ex.Message);
                }
            }

            lock (_sync)
            {
                _statusText = selectedState.State == VersionInstallState.Installed
                    ? selectedVersion.DisplayName + " запущен."
                    : selectedVersion.DisplayName + " установлен.";
                _progressText = selectedState.State == VersionInstallState.Installed
                    ? "Minecraft запущен из профиля: " + installedVersionId
                    : "Версия готова: " + installedVersionId;
                _installingVersionKey = null;
                _installingVersionProgress = null;
                BroadcastSnapshot();
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(directConnectAddress))
            {
                _ = DisposeActiveGuestTunnelAsync();
            }

            lock (_sync)
            {
                _statusText = "Не удалось установить " + selectedVersion.DisplayName + ".";
                if (selectedState.State == VersionInstallState.Installed)
                {
                    _statusText = "Не удалось запустить " + selectedVersion.DisplayName + ".";
                }
                _progressText = ex.Message;
                _installingVersionKey = null;
                _installingVersionProgress = null;
                BroadcastSnapshot();
            }

            try
            {
                var logFilePath = Path.Combine(_minecraftLauncher.GetBaseStorageDirectory(), "vesper-launch-error.txt");
                var logLines = new List<string>
                {
                    "==================================================",
                    " VESPER LAUNCHER ERROR LOG",
                    $" Time: {DateTime.Now:O}",
                    $" Version: {selectedVersion.DisplayName} ({selectedVersion.MinecraftVersionId})",
                    $" Username: {_accountFriends.CreateAccountSnapshot().CurrentNickname}",
                    $" Java Runtime Mode: {_javaRuntimeMode}",
                    $" Java Path Configured: {_javaPath}",
                    "==================================================",
                    "",
                    "Error Message:",
                    ex.Message,
                    "",
                    "Stack Trace:",
                    ex.ToString(),
                    ""
                };

                var lastJavaStdOutPath = Path.Combine(AppContext.BaseDirectory, "_last_java_stdout.log");
                var lastJavaStdErrPath = Path.Combine(AppContext.BaseDirectory, "_last_java_stderr.log");
                if (File.Exists(lastJavaStdOutPath))
                {
                    var stdoutContent = File.ReadAllText(lastJavaStdOutPath);
                    if (!string.IsNullOrWhiteSpace(stdoutContent))
                    {
                        logLines.Add("--- Game Output (STDOUT) ---");
                        logLines.Add(stdoutContent);
                        logLines.Add("");
                    }
                }
                if (File.Exists(lastJavaStdErrPath))
                {
                    var stderrContent = File.ReadAllText(lastJavaStdErrPath);
                    if (!string.IsNullOrWhiteSpace(stderrContent))
                    {
                        logLines.Add("--- Game Output (STDERR) ---");
                        logLines.Add(stderrContent);
                        logLines.Add("");
                    }
                }

                File.WriteAllLines(logFilePath, logLines);
                _ = _platform.Processes.OpenFileAsync(logFilePath);
            }
            catch (Exception logEx)
            {
                LogError(logEx, "Не удалось записать или открыть файл лога запуска.");
            }
        }
    }

    private async Task<string> InstallForgeOptiFineVersionAsync(
        LauncherVersionOption selectedVersion,
        IProgress<LauncherProgress>? progress)
    {
        if (!TrySplitCompositeLoaderVersionIds(
                selectedVersion.LoaderVersionId,
                out var forgeVersionId,
                out var optiFineVersionId))
        {
            throw new InvalidOperationException("Некорректная версия Forge + OptiFine.");
        }

        var installedVersionId = ResolveInstalledVersionId(selectedVersion);
        if (!IsInstalledVersionJsonPresent(installedVersionId))
        {
            installedVersionId = (await _minecraftLauncher.InstallModLoaderAsync(
                selectedVersion.MinecraftVersionId,
                ModLoaderKind.Forge,
                forgeVersionId,
                LauncherProfile.Vanilla,
                progress).ConfigureAwait(false)).InstalledVersionId;
        }

        await _minecraftLauncher.InstallOptiFineModAsync(
            selectedVersion.MinecraftVersionId,
            installedVersionId,
            optiFineVersionId,
            LauncherProfile.Vanilla,
            progress).ConfigureAwait(false);

        return installedVersionId;
    }

    private async Task<string> InstallFabricOptiFineVersionAsync(
        LauncherVersionOption selectedVersion,
        IProgress<LauncherProgress>? progress)
    {
        if (!TrySplitCompositeLoaderVersionIds(
                selectedVersion.LoaderVersionId,
                out var fabricVersionId,
                out var optiFineVersionId))
        {
            throw new InvalidOperationException("Некорректная версия Fabric + OptiFine.");
        }

        var installedVersionId = ResolveInstalledVersionId(selectedVersion);
        if (!IsInstalledVersionJsonPresent(installedVersionId))
        {
            installedVersionId = (await _minecraftLauncher.InstallModLoaderAsync(
                selectedVersion.MinecraftVersionId,
                ModLoaderKind.Fabric,
                fabricVersionId,
                LauncherProfile.Vanilla,
                progress).ConfigureAwait(false)).InstalledVersionId;
        }

        await _minecraftLauncher.InstallOptiFineModAsync(
            selectedVersion.MinecraftVersionId,
            installedVersionId,
            optiFineVersionId,
            LauncherProfile.Vanilla,
            progress).ConfigureAwait(false);

        await _minecraftLauncher.InstallOptiFabricModAsync(
            selectedVersion.MinecraftVersionId,
            installedVersionId,
            LauncherProfile.Vanilla,
            progress).ConfigureAwait(false);

        return installedVersionId;
    }

    private static bool TrySplitCompositeLoaderVersionIds(
        string? loaderVersionId,
        out string hostLoaderVersionId,
        out string optiFineVersionId)
    {
        hostLoaderVersionId = string.Empty;
        optiFineVersionId = string.Empty;
        var parts = (loaderVersionId ?? string.Empty).Split(
            "||",
            StringSplitOptions.None);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        hostLoaderVersionId = parts[0].Trim();
        optiFineVersionId = parts[1].Trim();
        return true;
    }

    private async Task ExecuteFriendsConnectAsync(JsonElement payload)
    {
        if (_gameProcessMonitor.IsRunning)
        {
            lock (_sync)
            {
                _statusText = "Minecraft уже запущен.";
                _progressText = "Закройте текущую игру перед подключением к другу.";
            }
            BroadcastSnapshot();
            return;
        }

        var username = GetString(payload, "username", string.Empty);
        if (string.IsNullOrWhiteSpace(username))
        {
            lock (_sync)
            {
                _statusText = "Не выбран друг для подключения.";
                _progressText = "Откройте список друзей и нажмите «Подключиться».";
            }
            BroadcastSnapshot();
            return;
        }

        lock (_sync)
        {
            var selectedVersion = ResolveSelectedVersionOption();
            var selectedState = GetVersionState(selectedVersion);
            if (_installingVersionKey is not null)
            {
                return;
            }

            if (selectedState.State != VersionInstallState.Installed)
            {
                _statusText = selectedVersion.DisplayName + " ещё не установлена.";
                _progressText = "Сначала установите выбранную версию, затем подключитесь к другу.";
                BroadcastSnapshot();
                return;
            }

            _statusText = "Подключаемся к " + username + "...";
            _progressText = "Создаём защищённый relay-туннель VesperNet.";
            BroadcastSnapshot();
        }

        var roomId = _accountFriends.TryGetFriendRelayRoomId(username);
        if (string.IsNullOrWhiteSpace(roomId))
        {
            lock (_sync)
            {
                _statusText = username + " сейчас не принимает подключения.";
                _progressText = "Друг должен быть в игре, а relay-сессия должна быть активна.";
            }
            BroadcastSnapshot();
            return;
        }

        var tunnel = await _accountFriends.CreateFriendTunnelAsync(roomId).ConfigureAwait(false);
        if (tunnel is null)
        {
            lock (_sync)
            {
                _statusText = "Не удалось открыть relay-туннель.";
                _progressText = "Проверьте вход в аккаунт Vesper и статус друга.";
            }
            BroadcastSnapshot();
            return;
        }

        await SetActiveGuestTunnelAsync(tunnel).ConfigureAwait(false);
        await ExecuteInstallSelectedVersionAsync("127.0.0.1", tunnel.LocalPort).ConfigureAwait(false);
    }

    private async Task SetActiveGuestTunnelAsync(VesperGuestRelayTunnel tunnel)
    {
        VesperGuestRelayTunnel? previousTunnel;
        lock (_sync)
        {
            previousTunnel = _activeGuestTunnel;
            _activeGuestTunnel = tunnel;
        }

        if (previousTunnel is not null)
        {
            await previousTunnel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeActiveGuestTunnelAsync()
    {
        VesperGuestRelayTunnel? tunnel;
        lock (_sync)
        {
            tunnel = _activeGuestTunnel;
            _activeGuestTunnel = null;
        }

        if (tunnel is not null)
        {
            await tunnel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private LauncherVersionOption ResolveSelectedVersionOption()
    {
        if (!string.IsNullOrWhiteSpace(_selectedVersionKey))
        {
            var selectedVersion = _versionOptions.FirstOrDefault(
                option => string.Equals(option.Key, _selectedVersionKey, StringComparison.OrdinalIgnoreCase));
            if (selectedVersion is not null)
            {
                return selectedVersion;
            }
        }

        return _versionOptions.FirstOrDefault() ?? CreateVanillaOption(
            string.IsNullOrWhiteSpace(_versionCatalog.LatestRelease)
                ? "1.21"
                : _versionCatalog.LatestRelease);
    }

    private VersionState GetVersionState(LauncherVersionOption option)
    {
        if (string.Equals(_installingVersionKey, option.Key, StringComparison.OrdinalIgnoreCase))
        {
            return new VersionState(VersionInstallState.Downloading, "Скачивание...", _installingVersionProgress);
        }

        if (option.LoaderKind is null)
        {
            var gameDirectory = _minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla);
            return _versionStateMachine.GetState(gameDirectory, new MinecraftVersionEntry(
                option.MinecraftVersionId,
                option.MinecraftVersionType,
                option.ReleaseTime,
                option.MetadataUrl,
                option.MetadataSha1));
        }

        var installed = IsInstalledVersionJsonPresent(option.InstalledVersionId);
        if (!installed && !string.IsNullOrWhiteSpace(option.AlternateInstalledVersionId))
        {
            installed = IsInstalledVersionJsonPresent(option.AlternateInstalledVersionId);
        }

        if (installed && string.Equals(option.LoaderKind, "Forge+OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            var installedVersionId = ResolveInstalledVersionId(option);
            installed = _minecraftLauncher.HasInstalledOptiFineMod(installedVersionId, LauncherProfile.Vanilla);
        }
        else if (installed && string.Equals(option.LoaderKind, "Fabric+OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            var installedVersionId = ResolveInstalledVersionId(option);
            installed = _minecraftLauncher.HasInstalledOptiFineMod(installedVersionId, LauncherProfile.Vanilla) &&
                        _minecraftLauncher.HasInstalledOptiFabricMod(installedVersionId, LauncherProfile.Vanilla);
        }

        return installed ? VersionState.Installed : VersionState.NotInstalled;
    }

    private bool IsInstalledVersionJsonPresent(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return false;
        }

        return File.Exists(GetInstalledVersionJsonPath(versionId));
    }

    private string GetInstalledVersionJsonPath(string versionId)
    {
        var gameDirectory = _minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla);
        return Path.Combine(gameDirectory, "versions", versionId, versionId + ".json");
    }

    private string ResolveInstalledVersionId(LauncherVersionOption option)
    {
        if (IsInstalledVersionJsonPresent(option.InstalledVersionId))
        {
            return option.InstalledVersionId;
        }

        return !string.IsNullOrWhiteSpace(option.AlternateInstalledVersionId) &&
               IsInstalledVersionJsonPresent(option.AlternateInstalledVersionId)
            ? option.AlternateInstalledVersionId
            : option.InstalledVersionId;
    }

    private string BuildLaunchButtonText(LauncherVersionOption selectedVersion)
    {
        if (_gameProcessMonitor.IsRunning)
        {
            return "Закрыть";
        }

        var state = GetVersionState(selectedVersion);
        return state.State switch
        {
            VersionInstallState.Downloading => "Скачивание...",
            VersionInstallState.Installed => "Играть",
            _ => "Установить"
        };
    }

    private static string NormalizeVersionKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith("vanilla-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)
            ? value.Trim()
            : BuildVersionKey(value.Trim());
    }

    private static string BuildVersionKey(string versionId)
    {
        return "vanilla-" + versionId;
    }

    private static string BuildVersionLabel(string versionId)
    {
        return versionId + " - ванильная";
    }

    private static string BuildVersionLabel(MinecraftVersionCatalogEntry version)
    {
        return version.Type.ToLowerInvariant() switch
        {
            "snapshot" => version.Id + " - snapshot",
            "old_beta" => version.Id + " - beta",
            "old_alpha" => version.Id + " - alpha",
            _ => BuildVersionLabel(version.Id)
        };
    }

    private static string BuildVersionSubtitle(MinecraftVersionCatalogEntry version)
    {
        return version.Type.ToLowerInvariant() switch
        {
            "snapshot" => "Snapshot",
            "old_beta" => "Beta",
            "old_alpha" => "Alpha",
            _ => "Vanilla"
        };
    }

    private static string BuildVersionNote(MinecraftVersionCatalogEntry version)
    {
        return version.Type.ToLowerInvariant() switch
        {
            "snapshot" => "Snapshot Mojang. Может быть нестабильной.",
            "old_beta" => "Старая beta-версия Mojang.",
            "old_alpha" => "Старая alpha-версия Mojang.",
            _ => "Официальная vanilla-версия Mojang."
        };
    }

    private static LauncherVersionOption CreateVanillaOption(string minecraftVersionId)
    {
        return new LauncherVersionOption(
            BuildVersionKey(minecraftVersionId),
            minecraftVersionId,
            "release",
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            null,
            null,
            minecraftVersionId,
            null,
            BuildVersionLabel(minecraftVersionId),
            "Vanilla",
            "Официальная vanilla-версия Mojang.");
    }

    private static LauncherVersionOption CreateVanillaOption(MinecraftVersionCatalogEntry version)
    {
        return new LauncherVersionOption(
            BuildVersionKey(version.Id),
            version.Id,
            version.Type,
            version.MetadataUrl,
            version.MetadataSha1,
            version.ReleaseTime,
            null,
            null,
            version.Id,
            null,
            BuildVersionLabel(version),
            BuildVersionSubtitle(version),
            "РћС„РёС†РёР°Р»СЊРЅР°СЏ vanilla-РІРµСЂСЃРёСЏ Mojang.");
    }

    private static LauncherVersionOption CreateLoaderOption(
        string minecraftVersionId,
        ModLoaderKind loaderKind,
        ModLoaderVersionEntry loaderVersion)
    {
        var loaderName = loaderKind.ToString();
        var prefix = loaderName.ToLowerInvariant();
        var installedVersionId = loaderKind switch
        {
            ModLoaderKind.Fabric => $"fabric-loader-{loaderVersion.Id}-{minecraftVersionId}",
            ModLoaderKind.Forge => $"{minecraftVersionId}-forge-{loaderVersion.Id}",
            ModLoaderKind.NeoForge => $"neoforge-{loaderVersion.Id}",
            ModLoaderKind.OptiFine => BuildOptiFineInstalledVersionId(minecraftVersionId, loaderVersion.Id),
            _ => $"{minecraftVersionId}-{prefix}-{loaderVersion.Id}"
        };

        var alternateInstalledVersionId = loaderKind == ModLoaderKind.Forge
            ? $"{minecraftVersionId}-forge-{loaderVersion.Id}"
            : null;

        return new LauncherVersionOption(
            $"{prefix}-{minecraftVersionId}-{loaderVersion.Id}",
            minecraftVersionId,
            "release",
            string.Empty,
            string.Empty,
            loaderVersion.ReleaseTime,
            loaderName,
            loaderVersion.Id,
            installedVersionId,
            alternateInstalledVersionId,
            $"{minecraftVersionId} - {loaderName}",
            loaderName,
            $"{loaderName} {loaderVersion.Id} + Minecraft {minecraftVersionId}.");
    }

    private static string BuildOptiFineInstalledVersionId(string minecraftVersionId, string optiFineLoaderVersionId)
    {
        return TrySplitOptiFineLoaderVersionId(optiFineLoaderVersionId, out var optiFineType, out var optiFinePatch)
            ? $"OptiFine {minecraftVersionId} {optiFineType} {optiFinePatch}"
            : $"{minecraftVersionId}-optifine-{optiFineLoaderVersionId}";
    }

    private static bool TrySplitOptiFineLoaderVersionId(string loaderVersionId, out string optiFineType, out string optiFinePatch)
    {
        optiFineType = string.Empty;
        optiFinePatch = string.Empty;
        var parts = (loaderVersionId ?? string.Empty).Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        optiFineType = parts[0];
        optiFinePatch = parts[1];
        return true;
    }

    private void ApplyCommand(string command, JsonElement payload)
    {
        switch ((command ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "host.closesplash":
            case "host.syncbounds":
            case "bridge.requestsnapshot":
                return;
            case "shell.opensection":
                _activeSection = GetString(payload, "section", "none");
                var tabId = GetString(payload, "tabId", string.Empty);
                if (_activeSection == "settings" && !string.IsNullOrWhiteSpace(tabId))
                {
                    _activeSettingsTab = tabId;
                }
                return;
            case "shell.closesection":
                _activeSection = "none";
                return;
            case "settings.selecttab":
                _activeSection = "settings";
                _activeSettingsTab = GetString(payload, "tabId", _activeSettingsTab);
                return;
            case "settings.setmemory":
                _memoryMb = Math.Clamp(GetInt(payload, "value", _memoryMb), 1024, 12288);
                _autoMemory = false;
                _statusText = $"Память: {_memoryMb} MB.";
                return;
            case "settings.settoggle":
                ApplyToggle(GetString(payload, "field", string.Empty), GetBool(payload, "value", false));
                return;
            case "settings.settext":
                ApplyText(GetString(payload, "field", string.Empty), GetString(payload, "value", string.Empty));
                return;
            case "settings.setoption":
                ApplyOption(GetString(payload, "field", string.Empty), GetString(payload, "value", string.Empty));
                return;
            case "main.selectversionkey":
                _selectedVersionKey = NormalizeVersionKey(GetString(payload, "key", _selectedVersionKey));
                return;
            case "main.openprofilefolder":
            case "settings.opengamedirectory":
                _ = _platform.Processes.OpenFolderAsync(_platform.Paths.MinecraftDirectory);
                return;
            case "account.setmode":
                _accountFriends.SetAccountMode(GetString(payload, "mode", "login"));
                _activeSection = "account";
                return;
            case "account.selectrecentusername":
                _accountFriends.SelectRecentUsername(GetString(payload, "username", string.Empty));
                return;
            case "friends.setnickname":
                _accountFriends.SetFriendNickname(GetString(payload, "value", string.Empty));
                return;
            case "friends.add":
                _ = ExecuteFriendsAddAsync();
                return;
            case "friends.respond":
                _ = ExecuteFriendsRespondAsync(payload);
                return;
            case "friends.remove":
                _ = ExecuteFriendsRemoveAsync(payload);
                return;
            case "mods.setsearch":
                _modsSearch = GetString(payload, "value", string.Empty);
                return;
            case "mods.selectcategory":
                _modsCategory = GetString(payload, "category", _modsCategory);
                return;
            case "mods.setselectedprojects":
                {
                    var projectIdsList = payload.TryGetProperty("projectIds", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array
                        ? idsProp.EnumerateArray().Select(el => el.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToList()
                        : new List<string>();
                    lock (_sync)
                    {
                        _selectedProjectIds.Clear();
                        foreach (var id in projectIdsList)
                        {
                            _selectedProjectIds.Add(id);
                        }
                    }
                    BroadcastSnapshot();
                }
                return;
            case "mods.togglefavorite":
                {
                    var projectId = GetString(payload, "projectId", string.Empty);
                    if (!string.IsNullOrWhiteSpace(projectId))
                    {
                        lock (_sync)
                        {
                            if (_favoriteProjectIds.Contains(projectId))
                            {
                                _favoriteProjectIds.Remove(projectId);
                            }
                            else
                            {
                                _favoriteProjectIds.Add(projectId);
                            }
                        }
                        BroadcastSnapshot();
                    }
                }
                return;
            case "mods.toggleitem":
                _ = ExecuteModsToggleItemAsync(payload);
                return;
            case "mods.installversion":
                _ = ExecuteModsInstallVersionAsync(payload);
                return;
            case "mods.setprovider":
                lock (_sync)
                {
                    _modsProvider = GetString(payload, "value", "modrinth");
                }
                BroadcastSnapshot();
                return;
            case "skin.importdialog":
                _ = ExecuteSkinImportDialogAsync();
                return;
            case "skin.openfolder":
                _ = _platform.Processes.OpenFolderAsync(_platform.Paths.GetSkinsCacheDirectory());
                return;
            case "skin.refresh":
                return;
            case "skin.clear":
                _selectedSkinFileName = null;
                return;
            case "skin.setmodel":
                _modelPreferenceId = GetString(payload, "modelId", "auto");
                return;
            case "skin.selectfile":
                _selectedSkinFileName = GetString(payload, "fileName", string.Empty);
                if (string.IsNullOrWhiteSpace(_selectedSkinFileName))
                {
                    _selectedSkinFileName = null;
                }
                return;
            case "host.logJsError":
                var errorText = GetString(payload, "error", string.Empty);
                LogError(new Exception(errorText), "JavaScript Console/Runtime Error");
                return;
            default:
                _statusText = "Команда пока не подключена: " + command;
                return;
        }
    }

    private void ApplyToggle(string field, bool value)
    {
        switch (field.ToLowerInvariant())
        {
            case "usesystemjava":
                _javaRuntimeMode = value ? "system" : "custom";
                return;
            case "showjvmargs":
                _showJvmArgs = value;
                return;
            case "autooptimizememory":
                _autoMemory = value;
                return;
            case "showsnapshotversions":
                _showSnapshotVersions = value;
                RebuildVersionOptionsForCurrentFilters();
                return;
            case "showbetaversions":
                _showBetaVersions = value;
                RebuildVersionOptionsForCurrentFilters();
                return;
            case "showalphaversions":
                _showAlphaVersions = value;
                RebuildVersionOptionsForCurrentFilters();
                return;
        }
    }

    private void ApplyText(string field, string value)
    {
        switch (field.ToLowerInvariant())
        {
            case "javapath":
                _javaPath = value;
                if (!string.IsNullOrWhiteSpace(value) && _javaRuntimeMode == "auto")
                {
                    _javaRuntimeMode = "custom";
                }
                return;
            case "extrajvmargs":
                _extraJvmArgs = value;
                return;
        }
    }

    private void ApplyOption(string field, string value)
    {
        if (field.Equals("javaruntimemode", StringComparison.OrdinalIgnoreCase))
        {
            _javaRuntimeMode = value;
        }
    }

    private static string ResolveAssetsRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets"))
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? AppContext.BaseDirectory;
    }

    private static string? FindFirstExisting(string directory, params string[] fileNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        return fileNames
            .Select(fileName => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }

    private static string ToLauncherFileUrl(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : LocalStaticFileServer.BuildLauncherFileUrl(path);
    }

    private static string BuildAvatarPlaceholder(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "AV" : value.Trim();
        return text.Length <= 2 ? text.ToUpperInvariant() : text[..2].ToUpperInvariant();
    }

    private async Task ExecuteFriendsAddAsync()
    {
        var result = await _accountFriends.AddFriendAsync().ConfigureAwait(false);
        if (result.Success)
        {
            _statusText = "Запрос отправлен.";
        }
        else
        {
            _statusText = result.Message;
        }
        BroadcastSnapshot();
    }

    private async Task ExecuteFriendsRespondAsync(JsonElement payload)
    {
        string requestId = GetString(payload, "requestId", string.Empty);
        string action = GetString(payload, "action", string.Empty);
        var result = await _accountFriends.RespondFriendRequestAsync(requestId, action).ConfigureAwait(false);
        if (result.Success)
        {
            _statusText = action == "accept" ? "Друг добавлен." : "Запрос отклонен.";
        }
        else
        {
            _statusText = result.Message;
        }
        BroadcastSnapshot();
    }

    private async Task ExecuteFriendsRemoveAsync(JsonElement payload)
    {
        string username = GetString(payload, "username", string.Empty);
        var result = await _accountFriends.RemoveFriendAsync(username).ConfigureAwait(false);
        if (result.Success)
        {
            _statusText = "Друг удален.";
        }
        else
        {
            _statusText = result.Message;
        }
        BroadcastSnapshot();
    }

    private static string GetString(JsonElement payload, string propertyName, string fallback)
    {
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static int GetInt(JsonElement payload, string propertyName, int fallback)
    {
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty(propertyName, out var value) &&
               value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static bool GetBool(JsonElement payload, string propertyName, bool fallback)
    {
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty(propertyName, out var value) &&
               (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;
    }

    private async Task ExecuteSkinImportDialogAsync()
    {
        await Task.Yield();
        try
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
#if !IS_CROSS_PLATFORM
            var thread = new Thread(() =>
            {
                try
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Filter = "Изображение скина (*.png)|*.png",
                        Title = "Выберите скин в формате PNG"
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        tcs.SetResult(dialog.FileName);
                    }
                    else
                    {
                        tcs.SetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
#else
            tcs.SetResult(null);
#endif

            var filePath = await tcs.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                var skinsDir = _platform.Paths.GetSkinsCacheDirectory();
                Directory.CreateDirectory(skinsDir);
                var fileName = Path.GetFileName(filePath);
                var targetPath = Path.Combine(skinsDir, fileName);
                File.Copy(filePath, targetPath, true);

                lock (_sync)
                {
                    _selectedSkinFileName = fileName;
                }
                BroadcastSnapshot();
            }
        }
        catch (Exception ex)
        {
            LogError(ex, "Ошибка импорта скина.");
        }
    }

    private static readonly HttpClient HttpClientInstance = new() { Timeout = TimeSpan.FromMinutes(2) };

    private string GetCachedIconPath(string projectId, string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return string.Empty;
        }

        try
        {
            var cacheDir = Path.Combine(_platform.Paths.MinecraftDirectory, ".launcher-cache", "cf-icons");
            Directory.CreateDirectory(cacheDir);

            var filePath = Path.Combine(cacheDir, $"{projectId}.png");
            if (File.Exists(filePath))
            {
                return filePath;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, remoteUrl);
                    request.Headers.Add("User-Agent", "VesperLauncher/1.0");
                    using var response = await HttpClientInstance.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                      {
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        await File.WriteAllBytesAsync(filePath, bytes);
                        BroadcastSnapshot();
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex, "Failed CF icon download.");
                }
            });
        }
        catch (Exception ex)
        {
            LogError(ex, "CF icon caching setup failed.");
        }

        return string.Empty;
    }

    private async Task ExecuteModsInstallVersionAsync(JsonElement payload)
    {
        var projectId = GetString(payload, "projectId", string.Empty);
        var url = GetString(payload, "url", string.Empty);
        var filename = GetString(payload, "filename", string.Empty);
        var contentKindStr = GetString(payload, "contentKind", "mod");

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(filename))
        {
            return;
        }

        var selectedVersion = _versionOptions.FirstOrDefault(v => v.Key == _selectedVersionKey);
        if (selectedVersion is null)
        {
            return;
        }

        RecommendedCatalogContentKind contentKind = contentKindStr.ToLowerInvariant() switch
        {
            "shader" => RecommendedCatalogContentKind.Shader,
            "resourcepack" => RecommendedCatalogContentKind.ResourcePack,
            "modpack" => RecommendedCatalogContentKind.Modpack,
            _ => RecommendedCatalogContentKind.Mod
        };

        var destinationDir = contentKind switch
        {
            RecommendedCatalogContentKind.Shader => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "shaderpacks"),
            RecommendedCatalogContentKind.ResourcePack => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "resourcepacks"),
            RecommendedCatalogContentKind.Modpack => Path.Combine(_minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla), "modpacks"),
            _ => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods")
        };

        lock (_sync)
        {
            _isModsLoading = true;
            _statusText = $"Скачиваю {filename}...";
            BroadcastSnapshot();
        }

        try
        {
            Directory.CreateDirectory(destinationDir);
            var filePath = Path.Combine(destinationDir, filename);

            if (url.StartsWith("https://mediafilez.forgecdn.net/files/mock/"))
            {
                await File.WriteAllTextAsync(filePath, "Mock file content");
            }
            else
            {
                using var response = await HttpClientInstance.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                await stream.CopyToAsync(fileStream);
            }
        }
        catch (Exception ex)
        {
            LogError(ex, $"Не удалось установить версию {filename}");
        }
        finally
        {
            lock (_sync)
            {
                _isModsLoading = false;
                _statusText = string.Empty;
                BroadcastSnapshot();
            }
        }
    }

    private async Task ExecuteModsToggleItemAsync(JsonElement payload)
    {
        var projectId = GetString(payload, "projectId", string.Empty);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        var selectedVersion = ResolveSelectedVersionOption();
        var selectedState = GetVersionState(selectedVersion);
        if (selectedState.State != VersionInstallState.Installed)
        {
            return;
        }

        var catalogItems = GetModsCatalogItems(_modsCategory, selectedVersion, string.Empty);
        var item = catalogItems.FirstOrDefault(i => string.Equals(i.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        RecommendedCatalogContentKind contentKind = _modsCategory switch
        {
            "Ресурспаки" => RecommendedCatalogContentKind.ResourcePack,
            "Сборки" => RecommendedCatalogContentKind.Modpack,
            "Шейдеры" => RecommendedCatalogContentKind.Shader,
            _ => RecommendedCatalogContentKind.Mod
        };

        var destinationDir = contentKind switch
        {
            RecommendedCatalogContentKind.Shader => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "shaderpacks"),
            RecommendedCatalogContentKind.ResourcePack => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "resourcepacks"),
            RecommendedCatalogContentKind.Modpack => Path.Combine(_minecraftLauncher.GetGameDirectory(LauncherProfile.Vanilla), "modpacks"),
            _ => Path.Combine(_minecraftLauncher.GetVersionInstanceDirectory(LauncherProfile.Vanilla, selectedVersion.InstalledVersionId), "mods")
        };

        if (item.IsInstalled)
        {
            if (!string.IsNullOrWhiteSpace(item.ResolvedFileName))
            {
                try
                {
                    var filePath = Path.Combine(destinationDir, item.ResolvedFileName);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    LogError(ex, $"Не удалось удалить файл: {item.ResolvedFileName}");
                }
            }
            BroadcastSnapshot();
        }
        else
        {
            lock (_sync)
            {
                _isModsLoading = true;
                _statusText = $"Скачиваю {item.DisplayName}...";
                BroadcastSnapshot();
            }

            try
            {
                if (contentKind == RecommendedCatalogContentKind.Mod)
                {
                    var loaderKindStr = selectedVersion.LoaderKind;
                    if (!string.IsNullOrWhiteSpace(loaderKindStr))
                    {
                        ModLoaderKind loaderKind = loaderKindStr.Equals("Fabric", StringComparison.OrdinalIgnoreCase) 
                            ? ModLoaderKind.Fabric 
                            : ModLoaderKind.Forge;

                        var projects = new[]
                        {
                            new RecommendedModProject(
                                item.ProjectId,
                                item.DisplayName,
                                item.Description,
                                item.ResolvedFileName,
                                item.ResolvedDownloadUrl,
                                item.ResolvedFileSha1,
                                item.RequiredDependencyProjectIds)
                        };

                        await _minecraftLauncher.InstallRecommendedModsAsync(
                            selectedVersion.MinecraftVersionId,
                            selectedVersion.InstalledVersionId,
                            loaderKind,
                            projects,
                            LauncherProfile.Vanilla,
                            null,
                            CancellationToken.None);
                    }
                }
                else
                {
                    await _minecraftLauncher.InstallCatalogAssetAsync(
                        contentKind,
                        item.ProjectId,
                        item.DisplayName,
                        selectedVersion.MinecraftVersionId,
                        selectedVersion.InstalledVersionId,
                        LauncherProfile.Vanilla,
                        null,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                LogError(ex, $"Не удалось установить {item.DisplayName}");
            }
            finally
            {
                lock (_sync)
                {
                    _isModsLoading = false;
                    _statusText = string.Empty;
                    BroadcastSnapshot();
                }
            }
        }
    }
}

internal sealed record LauncherVersionOption(
    string Key,
    string MinecraftVersionId,
    string MinecraftVersionType,
    string MetadataUrl,
    string MetadataSha1,
    DateTimeOffset ReleaseTime,
    string? LoaderKind,
    string? LoaderVersionId,
    string InstalledVersionId,
    string? AlternateInstalledVersionId,
    string DisplayName,
    string Subtitle,
    string AvailabilityNote);




