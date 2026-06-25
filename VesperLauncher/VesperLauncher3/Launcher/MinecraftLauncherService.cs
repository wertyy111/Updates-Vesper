using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VesperLauncher.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace VesperLauncher.Launcher;

public sealed class MinecraftLauncherService
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclApiBaseUrl = "https://bmclapi2.bangbang93.com";
    private const string BmclApiVersionManifestUrl = BmclApiBaseUrl + "/mc/game/version_manifest_v2.json";
    private const string ForgeVersionsByMinecraftUrlTemplate = "https://bmclapi2.bangbang93.com/forge/minecraft/{0}";
    private const string FabricLoaderVersionsUrlTemplate = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{0}";
    private const string FabricProfileUrlTemplate = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{0}/{1}/profile/json";
    private const string OptiFineVersionListUrlTemplate = "https://bmclapi2.bangbang93.com/optifine/{0}";
    private const string OptiFineDownloadUrlTemplate = "https://bmclapi2.bangbang93.com/optifine/{0}/{1}/{2}";
    private const string ForgeInstallerMavenUrlTemplate = "https://maven.minecraftforge.net/net/minecraftforge/forge/{0}-{1}/forge-{0}-{1}-installer.jar";
    private const string ForgeMavenBaseUrl = "https://maven.minecraftforge.net/";
    private const string ForgeInstallerHeadlessReleaseApiUrl = "https://api.github.com/repos/xfl03/ForgeInstallerHeadless/releases/latest";
    private const string ForgePromotionsUrl = "https://files.minecraftforge.net/maven/net/minecraftforge/forge/promotions_slim.json";
    private const string CurseForgeCdnDownloadUrlTemplate = "https://mediafilez.forgecdn.net/files/{0}/{1}/{2}";
    private const string ModrinthProjectUrlTemplate = "https://api.modrinth.com/v2/project/{0}";
    private const string ModrinthProjectVersionsUrlTemplate = "https://api.modrinth.com/v2/project/{0}/version?loaders={1}&game_versions={2}";
    private const string ModrinthProjectVersionsByLoaderUrlTemplate = "https://api.modrinth.com/v2/project/{0}/version?loaders={1}";
    private const string ModrinthProjectVersionsByGameVersionUrlTemplate = "https://api.modrinth.com/v2/project/{0}/version?game_versions={1}";
    private const string ModrinthProjectVersionsAllUrlTemplate = "https://api.modrinth.com/v2/project/{0}/version";
    private const string ModrinthProjectSearchUrlTemplate = "https://api.modrinth.com/v2/search?limit={0}&index=downloads&facets={1}";
    private const string BundledVersionsDirectoryName = "BundledVersions";
    private const string AuthlibInjectorReleaseApiUrl = "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";
    private const string ElyPrismMetadataUrl = "https://raw.githubusercontent.com/ElyPrismLauncher/ElyPrismLauncher/develop/epl_metadata.json";
    private const string MineSkinGenerateUrl = "https://api.mineskin.org/v2/generate";
    private const int MaxManifestReleaseVersions = 80;
    private const int ModrinthCatalogSearchLimit = 100;
    private const int VersionFileDownloadConcurrency = 10;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim JavaRuntimeInstallLock = new(1, 1);
    private static readonly SemaphoreSlim AuthlibInjectorInstallLock = new(1, 1);
    private static readonly SemaphoreSlim ModrinthApiRequestGate = new(4, 4);
    private static readonly object LocalSkinHostLock = new();
    private static readonly object VesperAuthServerLock = new();
    private static readonly object MineSkinCacheLock = new();
    private static readonly object ModrinthCatalogCacheLock = new();
    private static readonly object ModrinthVersionResolutionCacheLock = new();
    private static readonly Regex TokenRegex = new("\"([^\"]*)\"|(\\S+)", RegexOptions.Compiled);
    private static readonly Regex JavaVersionRegex = new("version\\s+\"(?<version>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MinecraftVersionRegex = new("\\d+\\.\\d+(?:\\.\\d+)?", RegexOptions.Compiled);
    private static readonly IPlatformPathService PlatformPaths = PlatformServiceFactory.CreateCurrent().Paths;
    private static readonly Lazy<string> BaseStorageDirectory = new(ResolveBaseStorageDirectory, isThreadSafe: true);
    private static readonly string VersionManifestCachePath = LauncherDataPaths.GetDataFilePath("version-manifest-cache.json");
    private static readonly string ModrinthCatalogCachePath = Path.Combine(
        BaseStorageDirectory.Value,
        ".launcher-cache",
        "modrinth-catalog-cache.json");
    private static readonly TimeSpan VersionManifestCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ModrinthCatalogCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan ModrinthVersionResolutionCacheTtl = TimeSpan.FromHours(6);
    private static readonly Dictionary<string, ModrinthVersionResolutionCacheEntry> ModrinthVersionResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> IgnoredModrinthCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric",
        "forge",
        "quilt",
        "neoforge",
        "babric",
        "mod"
    };
    private sealed record ModCatalogEntry(
        string ProjectId,
        string DisplayName,
        string Description,
        string Category);
    private sealed record OptiFabricDownloadInfo(
        int FileId,
        string FileName,
        IReadOnlyList<string> SupportedVersions);
    private sealed record VersionManifestCache(DateTimeOffset FetchedAt, List<MinecraftVersionEntry> Versions);
    private readonly record struct SkinRect(int X, int Y, int Width, int Height);
    private sealed record SkinTexture(int Width, int Height, byte[] Pixels);
    private static ModrinthCatalogCacheState? _modrinthCatalogCacheState;
    private static readonly IReadOnlyList<OptiFabricDownloadInfo> OptiFabricDownloads =
    [
        new OptiFabricDownloadInfo(
            5025647,
            "optifabric-1.14.3.jar",
            ["1.20.4", "1.20.2", "1.20.1", "1.20", "1.19.4", "1.19.3", "1.19.2", "1.19.1", "1.19"]),
        new OptiFabricDownloadInfo(
            4642832,
            "optifabric-1.13.25.jar",
            ["1.20.1", "1.20", "1.19.4", "1.19.3", "1.19.2", "1.19.1", "1.19"]),
        new OptiFabricDownloadInfo(
            3961344,
            "optifabric-1.13.16.jar",
            ["1.19.2", "1.19.1", "1.19", "1.18.2", "1.18.1", "1.18", "1.17.1", "1.17", "1.16.5", "1.16.4", "1.16.3", "1.16.2", "1.16.1"])
    ];
    private static readonly HashSet<string> FabricOptiFineIncompatibleModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "iris",
        "sodium",
        "sodium-extra",
        "reeses-sodium-options",
        "indium"
    };
    private static readonly string[] FabricOptiFineIncompatibleFileNameTokens =
    [
        "iris",
        "sodium",
        "indium",
        "reeses-sodium-options"
    ];
    private static readonly HashSet<string> FabricOptiFineIntrinsicModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "optifabric",
        "optifabric-libs",
        "mm"
    };

    private static readonly IReadOnlyList<ModCatalogEntry> FabricModCatalog =
    [
        new ModCatalogEntry("fabric-api", "Fabric API", "базовая библиотека для большинства Fabric-модов", "Библиотека"),
        new ModCatalogEntry("sodium", "Sodium", "ускоряет рендер чанков", "Оптимизация"),
        new ModCatalogEntry("lithium", "Lithium", "ускоряет игровые тики", "Оптимизация"),
        new ModCatalogEntry("ferrite-core", "FerriteCore", "уменьшает расход памяти", "Оптимизация"),
        new ModCatalogEntry("modernfix", "ModernFix", "убирает лаги и долгие загрузки", "Оптимизация"),
        new ModCatalogEntry("immediatelyfast", "ImmediatelyFast", "ускоряет интерфейс и частицы", "Оптимизация"),
        new ModCatalogEntry("c2me-fabric", "C2ME", "ускоряет генерацию мира и загрузку чанков", "Оптимизация"),
        new ModCatalogEntry("entityculling", "EntityCulling", "не рисует скрытые сущности", "Оптимизация"),
        new ModCatalogEntry("krypton", "Krypton", "ускоряет сетевой стек", "Сеть"),
        new ModCatalogEntry("lazydfu", "LazyDFU", "ускоряет запуск и загрузку данных", "Загрузка"),
        new ModCatalogEntry("dynamic-fps", "Dynamic FPS", "снижает нагрузку, когда игра в фоне", "Оптимизация"),
        new ModCatalogEntry("cull-leaves", "Cull Leaves", "оптимизация отрисовки листвы", "Оптимизация"),
        new ModCatalogEntry("exordium", "Exordium", "ускоряет меню и интерфейс", "Оптимизация"),
        new ModCatalogEntry("sodium-extra", "Sodium Extra", "расширенные графические настройки", "Графика"),
        new ModCatalogEntry("reeses-sodium-options", "Reese's Sodium Options", "удобное меню настроек Sodium", "Графика"),
        new ModCatalogEntry("indium", "Indium", "совместимость Sodium с Fabric API", "Совместимость"),
        new ModCatalogEntry("iris", "Iris", "шейдеры для Fabric", "Шейдеры"),
        new ModCatalogEntry("starlight", "Starlight", "быстрое освещение", "Освещение"),
        new ModCatalogEntry("continuity", "Continuity", "соединенные текстуры", "Графика"),
        new ModCatalogEntry("modmenu", "Mod Menu", "список установленных модов в игре", "Интерфейс"),
        new ModCatalogEntry("cloth-config", "Cloth Config", "настройки модов в меню", "Интерфейс"),
        new ModCatalogEntry("yet-another-config-lib", "YACL", "современная библиотека настроек", "Библиотека"),
        new ModCatalogEntry("appleskin", "AppleSkin", "индикаторы еды и насыщения", "Интерфейс"),
        new ModCatalogEntry("jade", "Jade", "подсказки по блокам и мобам", "Интерфейс"),
        new ModCatalogEntry("shulkerboxtooltip", "Shulker Box Tooltip", "просмотр содержимого шалкера", "Интерфейс"),
        new ModCatalogEntry("inventory-profiles-next", "Inventory Profiles Next", "сортировка инвентаря", "Интерфейс"),
        new ModCatalogEntry("mouse-tweaks", "Mouse Tweaks", "быстрое управление инвентарем", "Интерфейс"),
        new ModCatalogEntry("no-chat-reports", "No Chat Reports", "отключает chat report", "Безопасность"),
        new ModCatalogEntry("betterf3", "BetterF3", "улучшенный экран отладки", "Интерфейс"),
        new ModCatalogEntry("ok-zoomer", "Ok Zoomer", "плавный зум", "Интерфейс"),
        new ModCatalogEntry("zoomify", "Zoomify", "гибкие настройки зума", "Интерфейс"),
        new ModCatalogEntry("notenoughanimations", "Not Enough Animations", "оживляет анимации от третьего лица", "Анимации"),
        new ModCatalogEntry("better-mount-hud", "Better Mount HUD", "аккуратный HUD для ездовых животных", "HUD"),
        new ModCatalogEntry("raised", "Raised", "поднимает чат над хотбаром", "Интерфейс"),
        new ModCatalogEntry("status-effect-bars", "Status Effect Bars", "удобные индикаторы эффектов", "HUD"),
        new ModCatalogEntry("simple-voice-chat", "Simple Voice Chat", "голосовой чат", "Связь"),
        new ModCatalogEntry("presence-footsteps", "Presence Footsteps", "улучшенные звуки шагов", "Звук"),
        new ModCatalogEntry("dripsounds-fabric", "Drip Sounds", "звуки капель и окружения", "Звук"),
        new ModCatalogEntry("litematica", "Litematica", "шаблоны построек", "Строительство"),
        new ModCatalogEntry("minihud", "MiniHUD", "полезный HUD и оверлеи", "HUD"),
        new ModCatalogEntry("tweakeroo", "Tweakeroo", "набор полезных твиков", "Удобство"),
        new ModCatalogEntry("malilib", "MaLiLib", "библиотека для Litematica и MiniHUD", "Библиотека"),
        new ModCatalogEntry("carpet", "Carpet", "инструменты и правила для продвинутой игры", "Твики"),
        new ModCatalogEntry("emi", "EMI", "просмотр рецептов и предметов", "Инвентарь"),
        new ModCatalogEntry("rei", "REI", "поиск рецептов и предметов", "Инвентарь"),
        new ModCatalogEntry("xaeros-minimap", "Xaero's Minimap", "мини-карта в углу экрана", "Навигация"),
        new ModCatalogEntry("xaeros-world-map", "Xaero's World Map", "большая карта мира", "Навигация"),
        new ModCatalogEntry("journeymap", "JourneyMap", "альтернативная карта и мини-карта", "Навигация"),
        new ModCatalogEntry("chat-heads", "Chat Heads", "аватарки игроков в чате", "Интерфейс"),
        new ModCatalogEntry("eating-animation", "Eating Animation", "анимации еды от третьего лица", "Анимации"),
        new ModCatalogEntry("debugify", "Debugify", "фиксы множества багов Minecraft", "Исправления"),
        new ModCatalogEntry("enhancedblockentities", "Enhanced Block Entities", "ускоряет сундуки и таблички", "Оптимизация"),
        new ModCatalogEntry("entity-model-features", "Entity Model Features", "поддержка моделей и эмиссии сущностей", "Графика"),
        new ModCatalogEntry("entitytexturefeatures", "Entity Texture Features", "улучшенные текстуры сущностей", "Графика"),
        new ModCatalogEntry("lambdynamiclights", "LambDynamicLights", "динамический свет от предметов", "Графика"),
        new ModCatalogEntry("wavey-capes", "Wavey Capes", "анимированные плащи", "Косметика"),
        new ModCatalogEntry("e4mc", "e4mc", "удобное подключение друзей без проброса портов", "Сеть"),
        new ModCatalogEntry("spark", "spark", "профилирование производительности", "Диагностика"),
        new ModCatalogEntry("architectury-api", "Architectury API", "библиотека для модов", "Библиотека")
    ];

    private static readonly IReadOnlyList<ModCatalogEntry> ForgeModCatalog =
    [
        new ModCatalogEntry("embeddium", "Embeddium", "быстрый рендер для Forge", "Оптимизация"),
        new ModCatalogEntry("oculus", "Oculus", "шейдеры для Forge", "Шейдеры"),
        new ModCatalogEntry("rubidium", "Rubidium", "альтернативный рендер для Forge", "Оптимизация"),
        new ModCatalogEntry("ferrite-core", "FerriteCore", "уменьшает расход памяти", "Оптимизация"),
        new ModCatalogEntry("modernfix", "ModernFix", "убирает лаги и долгие загрузки", "Оптимизация"),
        new ModCatalogEntry("entityculling", "EntityCulling", "не рисует скрытые сущности", "Оптимизация"),
        new ModCatalogEntry("memoryleakfix", "Memory Leak Fix", "фиксит утечки памяти", "Оптимизация"),
        new ModCatalogEntry("clumps", "Clumps", "объединяет опыт для снижения лагов", "Оптимизация"),
        new ModCatalogEntry("jei", "JEI", "поиск рецептов и предметов", "Инвентарь"),
        new ModCatalogEntry("balm", "Balm", "библиотека для многих Forge-модов", "Библиотека"),
        new ModCatalogEntry("configured", "Configured", "настройка конфигов в игре", "Интерфейс"),
        new ModCatalogEntry("controlling", "Controlling", "поиск и сортировка биндов", "Интерфейс"),
        new ModCatalogEntry("appleskin", "AppleSkin", "индикаторы еды и насыщения", "Интерфейс"),
        new ModCatalogEntry("jade", "Jade", "подсказки по блокам и мобам", "Интерфейс"),
        new ModCatalogEntry("shulkerboxtooltip", "Shulker Box Tooltip", "просмотр содержимого шалкера", "Интерфейс"),
        new ModCatalogEntry("inventory-profiles-next", "Inventory Profiles Next", "сортировка инвентаря", "Интерфейс"),
        new ModCatalogEntry("mouse-tweaks", "Mouse Tweaks", "быстрое управление инвентарем", "Интерфейс"),
        new ModCatalogEntry("betterf3", "BetterF3", "улучшенный экран отладки", "Интерфейс"),
        new ModCatalogEntry("no-chat-reports", "No Chat Reports", "отключает chat report", "Безопасность"),
        new ModCatalogEntry("simple-voice-chat", "Simple Voice Chat", "голосовой чат", "Связь"),
        new ModCatalogEntry("presence-footsteps", "Presence Footsteps", "улучшенные звуки шагов", "Звук"),
        new ModCatalogEntry("zoomify", "Zoomify", "гибкие настройки зума", "Интерфейс"),
        new ModCatalogEntry("emi", "EMI", "просмотр рецептов и предметов", "Инвентарь"),
        new ModCatalogEntry("rei", "REI", "поиск рецептов и предметов", "Инвентарь"),
        new ModCatalogEntry("spark", "spark", "профилирование производительности", "Диагностика"),
        new ModCatalogEntry("architectury-api", "Architectury API", "библиотека для модов", "Библиотека"),
        new ModCatalogEntry("cloth-config", "Cloth Config", "настройки модов в меню", "Интерфейс"),
        new ModCatalogEntry("create", "Create", "механизмы и автоматизация", "Контент"),
        new ModCatalogEntry("farmers-delight", "Farmer's Delight", "еда и фермерские механики", "Контент"),
        new ModCatalogEntry("waystones", "Waystones", "быстрое перемещение", "Удобство"),
        new ModCatalogEntry("supplementaries", "Supplementaries", "декор и механики", "Контент"),
        new ModCatalogEntry("carry-on", "Carry On", "перенос сундуков, бочек и мобов", "Удобство"),
        new ModCatalogEntry("journeymap", "JourneyMap", "карта и мини-карта", "Навигация"),
        new ModCatalogEntry("xaeros-minimap", "Xaero's Minimap", "мини-карта для путешествий", "Навигация"),
        new ModCatalogEntry("xaeros-world-map", "Xaero's World Map", "подробная карта мира", "Навигация"),
        new ModCatalogEntry("natures-compass", "Nature's Compass", "поиск биомов по миру", "Навигация"),
        new ModCatalogEntry("structure-compass", "Structure Compass", "поиск структур и деревень", "Навигация"),
        new ModCatalogEntry("corpse", "Corpse", "тело игрока с предметами после смерти", "Удобство"),
        new ModCatalogEntry("trashslot", "TrashSlot", "быстрое удаление ненужных предметов", "Инвентарь"),
        new ModCatalogEntry("better-combat", "Better Combat", "улучшенная боевая система", "Геймплей"),
        new ModCatalogEntry("eating-animation", "Eating Animation", "анимации еды", "Анимации"),
        new ModCatalogEntry("travelersbackpack", "Traveler's Backpack", "рюкзаки и перенос вещей", "Удобство"),
        new ModCatalogEntry("sophisticated-core", "Sophisticated Core", "библиотека для умных хранилищ", "Библиотека"),
        new ModCatalogEntry("sophisticated-backpacks", "Sophisticated Backpacks", "продвинутые рюкзаки", "Удобство"),
        new ModCatalogEntry("sophisticated-storage", "Sophisticated Storage", "продвинутые сундуки и хранилища", "Инвентарь"),
        new ModCatalogEntry("alexs-mobs", "Alex's Mobs", "большой набор новых существ", "Контент"),
        new ModCatalogEntry("citadel", "Citadel", "библиотека для контент-модов", "Библиотека")
    ];
    private static LocalSkinHttpServer? LocalSkinHost;
    private static VesperAuthHttpServer? VesperAuthServer;
    private static readonly string MineSkinCachePath = LauncherDataPaths.GetDataFilePath("mineskin-cache.json");
    private static readonly string[] LegacyCustomGameDirectories =
    [
        @"D:\Новая папка (2)\Minecraft\game"
    ];
    private const string FallbackMinecraftLanguageCode = "ru_ru";
    private const int MaxRetainedNativesVersionDirectoriesPerProfile = 4;
    private const int MaxRetainedNativesLaunchDirectoriesPerVersion = 2;
    private static readonly TimeSpan LauncherTempRetention = TimeSpan.FromHours(12);
    private static readonly TimeSpan UpdateCacheRetention = TimeSpan.FromDays(3);
    private static readonly HashSet<int> SupportedBundledJavaRuntimeMajors = [8, 16, 17, 21];
    private static readonly Lazy<string> DefaultMinecraftLanguageCode = new(ResolveDefaultMinecraftLanguageCode, isThreadSafe: true);

    public void RunStorageMaintenance()
    {
        TryRunMaintenanceStep(CleanupLauncherTemporaryData);
        TryRunMaintenanceStep(CleanupObsoleteJavaRuntimeArtifacts);
        TryRunMaintenanceStep(() => CleanupProfileNativesDirectory(Path.Combine(BaseStorageDirectory.Value, "minecraft_vanilla", "natives")));
        TryRunMaintenanceStep(() => CleanupProfileNativesDirectory(Path.Combine(BaseStorageDirectory.Value, "minecraft_cheat", "natives")));
        TryRunMaintenanceStep(() => TryDeleteDirectoryQuietly(Path.Combine(AppContext.BaseDirectory, ".launcher-temp-probe")));
        TryRunMaintenanceStep(() => TryDeleteFileQuietly(Path.Combine(AppContext.BaseDirectory, "_last_java_stdout.log")));
        TryRunMaintenanceStep(() => TryDeleteFileQuietly(Path.Combine(AppContext.BaseDirectory, "_last_java_stderr.log")));
    }

    public async Task<IReadOnlyList<MinecraftVersionEntry>> GetAvailableVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versionsById = new Dictionary<string, MinecraftVersionEntry>(StringComparer.Ordinal);
        foreach (var externalVersion in LoadExternalGameVersions())
        {
            if (!versionsById.ContainsKey(externalVersion.Id))
            {
                versionsById[externalVersion.Id] = externalVersion;
            }
        }

        foreach (var bundledVersion in LoadBundledVersions())
        {
            if (!versionsById.ContainsKey(bundledVersion.Id))
            {
                versionsById[bundledVersion.Id] = bundledVersion;
            }
        }

        VersionManifestCache? manifestCache = null;
        if (TryReadVersionManifestCache(out var cachedManifest))
        {
            manifestCache = cachedManifest;
            foreach (var cachedVersion in cachedManifest.Versions)
            {
                if (!versionsById.ContainsKey(cachedVersion.Id))
                {
                    versionsById[cachedVersion.Id] = cachedVersion;
                }
            }
        }

        var shouldRefreshManifest = manifestCache is null ||
                                    DateTimeOffset.UtcNow - manifestCache.FetchedAt > VersionManifestCacheTtl;
        if (shouldRefreshManifest)
        {
            try
            {
                using var manifest = await GetJsonDocumentWithUserAgentAsync(VersionManifestUrl, cancellationToken);

                var addedReleaseVersions = 0;
                var manifestVersions = new List<MinecraftVersionEntry>(MaxManifestReleaseVersions);
                foreach (var versionElement in manifest.RootElement.GetProperty("versions").EnumerateArray())
                {
                    var type = versionElement.GetProperty("type").GetString() ?? "release";
                    if (!type.Equals("release", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = versionElement.GetProperty("id").GetString() ?? string.Empty;
                    var url = versionElement.GetProperty("url").GetString() ?? string.Empty;
                    var sha1 = versionElement.TryGetProperty("sha1", out var sha1Element)
                        ? sha1Element.GetString() ?? string.Empty
                        : string.Empty;
                    var releaseTimeText = versionElement.GetProperty("releaseTime").GetString() ??
                                          DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    if (!DateTimeOffset.TryParse(releaseTimeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var releaseTime))
                    {
                        releaseTime = DateTimeOffset.UtcNow;
                    }

                    var manifestEntry = new MinecraftVersionEntry(id, type, releaseTime, url, sha1);
                    manifestVersions.Add(manifestEntry);
                    if (!versionsById.ContainsKey(id))
                    {
                        versionsById[id] = manifestEntry;
                    }

                    addedReleaseVersions++;
                    if (addedReleaseVersions >= MaxManifestReleaseVersions)
                    {
                        break;
                    }
                }

                if (manifestVersions.Count > 0)
                {
                    TryWriteVersionManifestCache(manifestVersions);
                }
            }
            catch when (versionsById.Count > 0)
            {
                // Keep launcher usable offline if bundled versions exist.
            }
        }

        if (versionsById.Count == 0)
        {
            throw new InvalidOperationException("Не удалось получить список версий.");
        }

        return versionsById.Values
            .OrderByDescending(version => version.ReleaseTime)
            .ToArray();
    }

    private static bool TryReadVersionManifestCache([NotNullWhen(true)] out VersionManifestCache? cache)
    {
        cache = null;
        try
        {
            if (!File.Exists(VersionManifestCachePath))
            {
                return false;
            }

            var json = File.ReadAllText(VersionManifestCachePath);
            var loaded = JsonSerializer.Deserialize<VersionManifestCache>(json);
            if (loaded is null || loaded.Versions is null || loaded.Versions.Count == 0)
            {
                return false;
            }

            cache = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryWriteVersionManifestCache(IEnumerable<MinecraftVersionEntry> versions)
    {
        try
        {
            var directory = Path.GetDirectoryName(VersionManifestCachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new VersionManifestCache(DateTimeOffset.UtcNow, versions.ToList());
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(VersionManifestCachePath, json);
        }
        catch
        {
            // Ignore cache write failures.
        }
    }

    private static bool TryReadModrinthCatalogCache(
        RecommendedCatalogContentKind contentKind,
        ModLoaderKind? loaderKind,
        string minecraftBaseVersionId,
        out IReadOnlyList<RecommendedModCatalogItem> items)
    {
        items = [];

        try
        {
            lock (ModrinthCatalogCacheLock)
            {
                var cache = LoadModrinthCatalogCacheState();
                var cacheKey = BuildModrinthCatalogCacheKey(contentKind, loaderKind, minecraftBaseVersionId);
                if (!cache.Entries.TryGetValue(cacheKey, out var entry))
                {
                    return false;
                }

                if (DateTimeOffset.UtcNow - entry.CachedAtUtc > ModrinthCatalogCacheTtl)
                {
                    cache.Entries.Remove(cacheKey);
                    PersistModrinthCatalogCacheState(cache);
                    return false;
                }

                items = entry.Items ?? [];
                return true;
            }
        }
        catch
        {
            items = [];
            return false;
        }
    }

    private static void TryWriteModrinthCatalogCache(
        RecommendedCatalogContentKind contentKind,
        ModLoaderKind? loaderKind,
        string minecraftBaseVersionId,
        IReadOnlyList<RecommendedModCatalogItem> items)
    {
        try
        {
            lock (ModrinthCatalogCacheLock)
            {
                var cache = LoadModrinthCatalogCacheState();
                cache.Entries[BuildModrinthCatalogCacheKey(contentKind, loaderKind, minecraftBaseVersionId)] =
                    new ModrinthCatalogCacheEntry(DateTimeOffset.UtcNow, items.ToList());
                PersistModrinthCatalogCacheState(cache);
            }
        }
        catch
        {
            // Ignore cache write failures.
        }
    }

    private static ModrinthCatalogCacheState LoadModrinthCatalogCacheState()
    {
        if (_modrinthCatalogCacheState is not null)
        {
            return _modrinthCatalogCacheState;
        }

        try
        {
            if (File.Exists(ModrinthCatalogCachePath))
            {
                var json = File.ReadAllText(ModrinthCatalogCachePath);
                var loaded = JsonSerializer.Deserialize<ModrinthCatalogCacheState>(json);
                if (loaded?.Entries is not null)
                {
                    _modrinthCatalogCacheState = loaded;
                    return _modrinthCatalogCacheState;
                }
            }
        }
        catch
        {
            // Ignore broken cache state and recreate it.
        }

        _modrinthCatalogCacheState = new ModrinthCatalogCacheState();
        return _modrinthCatalogCacheState;
    }

    private static void PersistModrinthCatalogCacheState(ModrinthCatalogCacheState cache)
    {
        var directory = Path.GetDirectoryName(ModrinthCatalogCachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ModrinthCatalogCachePath, json);
    }

    private static string BuildModrinthCatalogCacheKey(
        RecommendedCatalogContentKind contentKind,
        ModLoaderKind? loaderKind,
        string minecraftBaseVersionId)
    {
        var loaderPart = loaderKind?.ToString() ?? "none";
        return $"{contentKind}:{loaderPart}:{NormalizeMinecraftBaseVersionId(minecraftBaseVersionId)}";
    }

    private static bool TryReadModrinthVersionResolutionCache(
        RecommendedCatalogContentKind contentKind,
        string projectId,
        string minecraftBaseVersionId,
        ModLoaderKind? loaderKind,
        out ModrinthVersionFileInfo? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        lock (ModrinthVersionResolutionCacheLock)
        {
            var cacheKey = BuildModrinthVersionResolutionCacheKey(contentKind, projectId, minecraftBaseVersionId, loaderKind);
            if (!ModrinthVersionResolutionCache.TryGetValue(cacheKey, out var entry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - entry.CachedAtUtc > ModrinthVersionResolutionCacheTtl)
            {
                ModrinthVersionResolutionCache.Remove(cacheKey);
                return false;
            }

            version = entry.Version;
            return true;
        }
    }

    private static void WriteModrinthVersionResolutionCache(
        RecommendedCatalogContentKind contentKind,
        string projectId,
        string minecraftBaseVersionId,
        ModLoaderKind? loaderKind,
        ModrinthVersionFileInfo? version)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }

        lock (ModrinthVersionResolutionCacheLock)
        {
            ModrinthVersionResolutionCache[
                BuildModrinthVersionResolutionCacheKey(contentKind, projectId, minecraftBaseVersionId, loaderKind)] =
                new ModrinthVersionResolutionCacheEntry(DateTimeOffset.UtcNow, version);
        }
    }

    private static string BuildModrinthVersionResolutionCacheKey(
        RecommendedCatalogContentKind contentKind,
        string projectId,
        string minecraftBaseVersionId,
        ModLoaderKind? loaderKind)
    {
        var loaderPart = loaderKind?.ToString() ?? "none";
        return $"{contentKind}:{loaderPart}:{NormalizeMinecraftBaseVersionId(minecraftBaseVersionId)}:{projectId.Trim()}";
    }

    private static string CreateLauncherTemporaryFilePath(string filePrefix, string extensionWithDot)
    {
        if (string.IsNullOrWhiteSpace(filePrefix))
        {
            throw new ArgumentException("Префикс временного файла не указан.", nameof(filePrefix));
        }

        var safeExtension = string.IsNullOrWhiteSpace(extensionWithDot)
            ? ".tmp"
            : extensionWithDot.StartsWith(".", StringComparison.Ordinal) ? extensionWithDot : $".{extensionWithDot}";

        var tempDirectory = ResolveLauncherTemporaryDirectory("downloads");
        return Path.Combine(tempDirectory, $"{SanitizePathSegment(filePrefix)}-{Guid.NewGuid():N}{safeExtension}");
    }

    private static string ResolveLauncherTemporaryDirectory(params string[] relativeSegments)
    {
        var candidates = new List<string>();
        var platformCacheDirectory = PlatformPaths.GetLauncherCacheDirectory();
        if (!string.IsNullOrWhiteSpace(platformCacheDirectory))
        {
            candidates.Add(Path.Combine(new[] { platformCacheDirectory, "temp" }.Concat(relativeSegments).ToArray()));
        }

        if (!string.IsNullOrWhiteSpace(BaseStorageDirectory.Value))
        {
            candidates.Add(Path.Combine(new[] { BaseStorageDirectory.Value, ".launcher-temp" }.Concat(relativeSegments).ToArray()));
        }

        candidates.Add(Path.Combine(new[] { AppContext.BaseDirectory, ".launcher-temp" }.Concat(relativeSegments).ToArray()));

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonAppData))
        {
            candidates.Add(Path.Combine(new[] { commonAppData, "VesperLauncher", "temp" }.Concat(relativeSegments).ToArray()));
        }

        var tempRoot = Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(tempRoot))
        {
            candidates.Add(Path.Combine(new[] { tempRoot, "VesperLauncher" }.Concat(relativeSegments).ToArray()));
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(comparer))
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // try next candidate
            }
        }

        throw new IOException("Не удалось подготовить папку для временных файлов лаунчера.");
    }

    private static string ResolveLauncherJavaTemporaryDirectory(string contextName)
    {
        var safeContextName = string.IsNullOrWhiteSpace(contextName)
            ? "shared"
            : SanitizePathSegment(contextName);
        return ResolveLauncherTemporaryDirectory("java-runtime", safeContextName);
    }

    private static void EnsureSufficientFreeSpace(string targetPath, long minimumFreeBytes, string purpose)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || minimumFreeBytes <= 0)
        {
            return;
        }

        var fullPath = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace >= minimumFreeBytes)
            {
                return;
            }

            var requiredMb = Math.Ceiling(minimumFreeBytes / 1024d / 1024d);
            var availableMb = Math.Floor(drive.AvailableFreeSpace / 1024d / 1024d);
            throw new IOException(
                $"Недостаточно свободного места для {purpose}. Диск {drive.Name}: доступно {availableMb:0} MB, нужно хотя бы {requiredMb:0} MB.");
        }
        catch (IOException)
        {
            throw;
        }
        catch
        {
            // ignore drive probing issues
        }
    }

    private static void ApplyLauncherTempEnvironment(ProcessStartInfo processInfo, string tempDirectory)
    {
        if (processInfo is null || string.IsNullOrWhiteSpace(tempDirectory))
        {
            return;
        }

        Directory.CreateDirectory(tempDirectory);
        processInfo.Environment["TEMP"] = tempDirectory;
        processInfo.Environment["TMP"] = tempDirectory;
        processInfo.Environment["TMPDIR"] = tempDirectory;
    }

    public async Task<IReadOnlyList<ModLoaderVersionEntry>> GetAvailableModLoaderVersionsAsync(
        string minecraftVersionId,
        ModLoaderKind loaderKind,
        CancellationToken cancellationToken = default)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);

        return loaderKind switch
        {
            ModLoaderKind.Forge => await FetchForgeVersionsAsync(normalizedMinecraftVersionId, cancellationToken),
            ModLoaderKind.Fabric => await FetchFabricVersionsAsync(normalizedMinecraftVersionId, cancellationToken),
            ModLoaderKind.OptiFine => await FetchOptiFineVersionsAsync(normalizedMinecraftVersionId, cancellationToken),
            _ => []
        };
    }

    public async Task<ModLoaderInstallResult> InstallModLoaderAsync(
        string minecraftVersionId,
        ModLoaderKind loaderKind,
        string loaderVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        if (string.IsNullOrWhiteSpace(loaderVersionId))
        {
            throw new ArgumentException("Версия модлоадера не указана.", nameof(loaderVersionId));
        }

        var gameDirectory = GetGameDirectory(profile);
        Directory.CreateDirectory(gameDirectory);
        EnsureProfileDirectories(gameDirectory, profile);

        await EnsureBaseVersionReadyAsync(normalizedMinecraftVersionId, gameDirectory, cancellationToken);

        return loaderKind switch
        {
            ModLoaderKind.Forge => await InstallForgeAsync(
                normalizedMinecraftVersionId,
                loaderVersionId,
                gameDirectory,
                progress,
                cancellationToken),
            ModLoaderKind.Fabric => await InstallFabricAsync(
                normalizedMinecraftVersionId,
                loaderVersionId,
                gameDirectory,
                progress,
                cancellationToken),
            ModLoaderKind.OptiFine => await InstallOptiFineAsync(
                normalizedMinecraftVersionId,
                loaderVersionId,
                gameDirectory,
                progress,
                cancellationToken),
            _ => throw new NotSupportedException($"Неизвестный тип модлоадера: {loaderKind}")
        };
    }

    public bool IsFabricOptiFineSupported(string minecraftVersionId)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            return false;
        }

        return TryResolveOptiFabricDownloadInfo(
            NormalizeMinecraftBaseVersionId(minecraftVersionId),
            out _);
    }

    public bool IsForgeSupported(string minecraftVersionId)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            return false;
        }

        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        return !string.Equals(normalizedMinecraftVersionId, "1.17", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsForgeOptiFineSupported(string minecraftVersionId)
    {
        return IsForgeSupported(minecraftVersionId);
    }

    public ModLoaderVersionEntry? SelectPreferredOptiFineVersion(
        IReadOnlyList<ModLoaderVersionEntry> availableVersions,
        ModLoaderKind hostLoaderKind)
    {
        if (availableVersions is null || availableVersions.Count == 0)
        {
            return null;
        }

        IEnumerable<ModLoaderVersionEntry> candidates = availableVersions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id));

        if (hostLoaderKind == ModLoaderKind.Forge)
        {
            candidates = candidates.Where(entry => !IsOptiFinePreviewLoaderId(entry.Id));
        }

        return candidates
            .OrderByDescending(entry => entry.Id, OptiFineVersionComparer.Instance)
            .FirstOrDefault();
    }

    public void RemoveInstalledOptiFineMods(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        if (string.IsNullOrWhiteSpace(installedVersionId))
        {
            return;
        }

        var instanceDirectory = GetVersionInstanceDirectory(profile, installedVersionId.Trim());
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return;
        }

        DeleteMatchingModFiles(modsDirectory, IsOptiFineModFileName);
    }

    public async Task<ModLoaderVersionEntry?> GetCompatibleFabricLoaderVersionForOptiFineAsync(
        string minecraftVersionId,
        IReadOnlyList<ModLoaderVersionEntry>? availableVersions = null,
        string? minimumLoaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        var candidates = availableVersions is { Count: > 0 }
            ? availableVersions
            : await FetchFabricVersionsAsync(normalizedMinecraftVersionId, cancellationToken);

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(minimumLoaderVersion) &&
                ModLoaderVersionComparer.Instance.Compare(candidate.Id, minimumLoaderVersion) < 0)
            {
                continue;
            }

            if (await DoesFabricLoaderVersionSupportOptiFineRuntimeAsync(
                    normalizedMinecraftVersionId,
                    candidate.Id,
                    cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<bool> DoesFabricLoaderVersionSupportOptiFineRuntimeAsync(
        string minecraftVersionId,
        string loaderVersionId,
        CancellationToken cancellationToken = default)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        var normalizedLoaderVersionId = loaderVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLoaderVersionId))
        {
            return false;
        }

        var profileUrl = string.Format(
            CultureInfo.InvariantCulture,
            FabricProfileUrlTemplate,
            Uri.EscapeDataString(normalizedMinecraftVersionId),
            Uri.EscapeDataString(normalizedLoaderVersionId));

        try
        {
            using var document = await GetJsonDocumentWithUserAgentAsync(profileUrl, cancellationToken);
            return HasFabricOptiFineRuntimeSupport(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    public bool DoesInstalledFabricVersionSupportOptiFineRuntime(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        var normalizedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInstalledVersionId))
        {
            return false;
        }

        var versionJsonPath = Path.Combine(
            GetGameDirectory(profile),
            "versions",
            normalizedInstalledVersionId,
            $"{normalizedInstalledVersionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(versionJsonPath);
            using var document = JsonDocument.Parse(stream);
            return HasFabricOptiFineRuntimeSupport(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    public string? GetInstalledFabricLoaderVersion(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        var normalizedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInstalledVersionId))
        {
            return null;
        }

        var versionJsonPath = Path.Combine(
            GetGameDirectory(profile),
            "versions",
            normalizedInstalledVersionId,
            $"{normalizedInstalledVersionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(versionJsonPath);
            using var document = JsonDocument.Parse(stream);
            return TryGetFabricLoaderVersion(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public string? GetRequiredMinimumFabricLoaderVersionForInstalledMods(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        var normalizedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInstalledVersionId))
        {
            return null;
        }

        var instanceDirectory = GetVersionInstanceDirectory(profile, normalizedInstalledVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        if (!Directory.Exists(modsDirectory))
        {
            return null;
        }

        return GetRequiredMinimumFabricLoaderVersion(
            Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly));
    }

    public bool DoesInstalledFabricVersionSatisfyInstalledMods(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        var requiredMinimumVersion = GetRequiredMinimumFabricLoaderVersionForInstalledMods(installedVersionId, profile);
        if (string.IsNullOrWhiteSpace(requiredMinimumVersion))
        {
            return true;
        }

        var currentLoaderVersion = GetInstalledFabricLoaderVersion(installedVersionId, profile);
        if (string.IsNullOrWhiteSpace(currentLoaderVersion))
        {
            return false;
        }

        return ModLoaderVersionComparer.Instance.Compare(currentLoaderVersion, requiredMinimumVersion) >= 0;
    }

    public void CopyInstalledMods(
        string sourceInstalledVersionId,
        string targetInstalledVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        var normalizedSourceVersionId = sourceInstalledVersionId?.Trim();
        var normalizedTargetVersionId = targetInstalledVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedSourceVersionId) ||
            string.IsNullOrWhiteSpace(normalizedTargetVersionId) ||
            string.Equals(normalizedSourceVersionId, normalizedTargetVersionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceModsDirectory = Path.Combine(GetVersionInstanceDirectory(profile, normalizedSourceVersionId), "mods");
        if (!Directory.Exists(sourceModsDirectory))
        {
            return;
        }

        var targetModsDirectory = Path.Combine(GetVersionInstanceDirectory(profile, normalizedTargetVersionId), "mods");
        Directory.CreateDirectory(targetModsDirectory);

        foreach (var sourceFilePath in Directory.EnumerateFiles(sourceModsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var destinationPath = Path.Combine(targetModsDirectory, Path.GetFileName(sourceFilePath));
            if (!File.Exists(destinationPath))
            {
                File.Copy(sourceFilePath, destinationPath, overwrite: false);
            }
        }

        ResetFabricProcessedModCache(normalizedTargetVersionId, profile);
    }

    public async Task<string> InstallOptiFineModAsync(
        string minecraftVersionId,
        string installedVersionId,
        string loaderVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        var normalizedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInstalledVersionId))
        {
            throw new ArgumentException("ID установленной версии не указан.", nameof(installedVersionId));
        }

        if (!TryParseOptiFineLoaderId(loaderVersionId, out var optifineType, out var optifinePatch))
        {
            throw new ArgumentException(
                $"Неверный идентификатор OptiFine '{loaderVersionId}'. Ожидается формат 'TYPE|PATCH'.",
                nameof(loaderVersionId));
        }

        var gameDirectory = GetGameDirectory(profile);
        Directory.CreateDirectory(gameDirectory);
        EnsureProfileDirectories(gameDirectory, profile);
        _ = await ResolveVersionEntryByIdAsync(normalizedInstalledVersionId, gameDirectory, cancellationToken);

        var instanceDirectory = GetVersionInstanceDirectory(profile, normalizedInstalledVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var optifineDownloadUrl = string.Format(
            CultureInfo.InvariantCulture,
            OptiFineDownloadUrlTemplate,
            Uri.EscapeDataString(normalizedMinecraftVersionId),
            Uri.EscapeDataString(optifineType),
            Uri.EscapeDataString(optifinePatch));
        var tempOptiFinePath = CreateLauncherTemporaryFilePath(
            $"optifine-mod-{normalizedMinecraftVersionId}-{optifineType}-{optifinePatch}",
            ".jar");
        var tempInstallerPath = GetOptiFineInstallerArchivePath(tempOptiFinePath);

        try
        {
            var optifineStage = $"Скачиваю OptiFine {optifineType} {optifinePatch}...";
            progress?.Report(new LauncherProgress(optifineStage, 0, 3));
            await DownloadFileWithRetriesAsync(
                optifineDownloadUrl,
                tempOptiFinePath,
                null,
                cancellationToken,
                CreateStageDownloadProgressReporter(progress, optifineStage, 0, 1, 3));

            if (IsOptiFinePatchArchive(tempOptiFinePath))
            {
                progress?.Report(new LauncherProgress("Подготавливаю OptiFine для Forge/Fabric...", 1, 3));
                var baseJarPath = await EnsureVersionJarAvailableAsync(
                    normalizedMinecraftVersionId,
                    gameDirectory,
                    cancellationToken);
                var baseVersion = await ResolveVersionEntryByIdAsync(
                    normalizedMinecraftVersionId,
                    gameDirectory,
                    cancellationToken);
                using var baseMetadata = await ResolveVersionMetadataAsync(baseVersion, gameDirectory, cancellationToken);
                var javaExecutable = await ResolveJavaExecutableAsync(
                    string.Empty,
                    baseMetadata.RootElement,
                    normalizedMinecraftVersionId,
                    progress,
                    cancellationToken);

                await EnsurePatchedOptiFineLibraryAsync(
                    tempOptiFinePath,
                    baseJarPath,
                    javaExecutable,
                    progress,
                    cancellationToken);
            }

            DeleteMatchingModFiles(modsDirectory, IsOptiFineModFileName);
            ResetFabricProcessedModCache(normalizedInstalledVersionId, profile);

            var destinationFileName = $"OptiFine-{normalizedMinecraftVersionId}_{optifineType}_{optifinePatch}.jar";
            var destinationPath = Path.Combine(modsDirectory, destinationFileName);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(tempOptiFinePath, destinationPath);
            progress?.Report(new LauncherProgress($"OptiFine {optifineType} {optifinePatch} добавлен в сборку.", 3, 3));
            return destinationPath;
        }
        finally
        {
            TryDeleteFileQuietly(tempOptiFinePath);
            TryDeleteFileQuietly(tempInstallerPath);
        }
    }

    public async Task<string> InstallOptiFabricModAsync(
        string minecraftVersionId,
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        var normalizedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInstalledVersionId))
        {
            throw new ArgumentException("ID установленной версии не указан.", nameof(installedVersionId));
        }

        if (!TryResolveOptiFabricDownloadInfo(normalizedMinecraftVersionId, out var downloadInfo))
        {
            throw new InvalidOperationException(
                $"Для Fabric+OptiFine пока нет подходящего OptiFabric под Minecraft {normalizedMinecraftVersionId}.");
        }

        var gameDirectory = GetGameDirectory(profile);
        Directory.CreateDirectory(gameDirectory);
        EnsureProfileDirectories(gameDirectory, profile);
        _ = await ResolveVersionEntryByIdAsync(normalizedInstalledVersionId, gameDirectory, cancellationToken);

        var instanceDirectory = GetVersionInstanceDirectory(profile, normalizedInstalledVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        EnsureFabricOptiFineCompatibleMods(normalizedInstalledVersionId, profile);
        DeleteMatchingModFiles(modsDirectory, IsOptiFabricModFileName);
        ResetFabricProcessedModCache(normalizedInstalledVersionId, profile);

        var destinationPath = Path.Combine(modsDirectory, downloadInfo.FileName);
        var downloadUrl = BuildCurseForgeCdnDownloadUrl(downloadInfo.FileId, downloadInfo.FileName);

        const string optiFabricStage = "Скачиваю OptiFabric...";
        progress?.Report(new LauncherProgress(optiFabricStage, 0, 1));
        await DownloadFileIfNeededAsync(
            downloadUrl,
            destinationPath,
            null,
            cancellationToken,
            CreateStageDownloadProgressReporter(progress, optiFabricStage, 0, 1, 1));
        return destinationPath;
    }

    public void EnsureFabricOptiFineCompatibleMods(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        var normalizedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedInstalledVersionId))
        {
            throw new ArgumentException("ID установленной версии не указан.", nameof(installedVersionId));
        }

        var instanceDirectory = GetVersionInstanceDirectory(profile, normalizedInstalledVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);
        DeleteMatchingModFiles(modsDirectory, IsFabricOptiFineIncompatibleModFile);
        ResetFabricProcessedModCache(normalizedInstalledVersionId, profile);
    }

    public async Task<IReadOnlyList<RecommendedModCatalogItem>> GetRecommendedModCatalogAsync(
        ModLoaderKind loaderKind,
        string? minecraftBaseVersionId = null,
        CancellationToken cancellationToken = default)
    {
        return await GetRecommendedCatalogAsync(
            RecommendedCatalogContentKind.Mod,
            loaderKind,
            minecraftBaseVersionId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<RecommendedModCatalogItem>> GetRecommendedCatalogAsync(
        RecommendedCatalogContentKind contentKind,
        ModLoaderKind? loaderKind,
        string? minecraftBaseVersionId = null,
        CancellationToken cancellationToken = default)
    {
        var catalog = loaderKind switch
        {
            ModLoaderKind.Fabric => FabricModCatalog,
            ModLoaderKind.Forge => ForgeModCatalog,
            _ => []
        };

        if (contentKind == RecommendedCatalogContentKind.Mod && catalog.Count == 0)
        {
            return [];
        }

        var normalizedBaseVersionId = string.IsNullOrWhiteSpace(minecraftBaseVersionId)
            ? null
            : NormalizeMinecraftBaseVersionId(minecraftBaseVersionId);
        if (!string.IsNullOrWhiteSpace(normalizedBaseVersionId))
        {
            if (TryReadModrinthCatalogCache(contentKind, loaderKind, normalizedBaseVersionId, out var cachedCatalog))
            {
                if (cachedCatalog.Count > 0 &&
                    HasResolvedCatalogTargets(cachedCatalog))
                {
                    return cachedCatalog;
                }
            }

            try
            {
                var searchedCatalog = await FetchModrinthCatalogAsync(
                    contentKind,
                    loaderKind,
                    normalizedBaseVersionId,
                    cancellationToken);
                searchedCatalog = await ResolveFetchedModrinthCatalogAsync(
                    contentKind,
                    searchedCatalog,
                    normalizedBaseVersionId,
                    loaderKind,
                    cancellationToken);

                TryWriteModrinthCatalogCache(contentKind, loaderKind, normalizedBaseVersionId, searchedCatalog);
                if (searchedCatalog.Count > 0)
                {
                    return searchedCatalog;
                }
            }
            catch
            {
                if (contentKind is not RecommendedCatalogContentKind.Mod)
                {
                    return [];
                }

                // Fall back to curated compatibility checks below.
            }
        }

        if (contentKind is not RecommendedCatalogContentKind.Mod)
        {
            return [];
        }

        if (loaderKind is not (ModLoaderKind.Fabric or ModLoaderKind.Forge))
        {
            return [];
        }

        var metadataCache = new Dictionary<string, ModrinthProjectMetadata>(StringComparer.OrdinalIgnoreCase);
        var resolvedProjects = normalizedBaseVersionId is null
            ? null
            : new Dictionary<string, ModrinthResolvedMod>(StringComparer.OrdinalIgnoreCase);
        var unresolvedProjects = normalizedBaseVersionId is null
            ? null
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolutionStack = normalizedBaseVersionId is null
            ? null
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectGroups = catalog
            .GroupBy(entry => entry.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new List<RecommendedModCatalogItem>(projectGroups.Length);
        foreach (var projectGroup in projectGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var firstEntry = projectGroup.First();
            var category = projectGroup
                .Select(entry => entry.Category)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var categorySummary = category.Length == 0
                ? string.Empty
                : $"Категория: {string.Join(", ", category)}";
            ModrinthProjectMetadata? metadata = null;
            if (!string.IsNullOrWhiteSpace(normalizedBaseVersionId))
            {
                try
                {
                    await ResolveRecommendedModProjectAsync(
                        new RecommendedModProject(
                            firstEntry.ProjectId,
                            firstEntry.DisplayName,
                            firstEntry.Description),
                        normalizedBaseVersionId,
                        loaderKind!.Value,
                        metadataCache,
                        resolvedProjects!,
                        unresolvedProjects!,
                        resolutionStack!,
                        cancellationToken);

                    metadataCache.TryGetValue(firstEntry.ProjectId, out metadata);
                    var resolvedProjectId = metadata?.ProjectId ?? firstEntry.ProjectId;
                    if (!resolvedProjects!.TryGetValue(resolvedProjectId, out var resolvedProject))
                    {
                        continue;
                    }

                    result.Add(new RecommendedModCatalogItem(
                        resolvedProjectId,
                        metadata?.Title ?? firstEntry.DisplayName,
                        !string.IsNullOrWhiteSpace(metadata?.Description) ? metadata.Description! : firstEntry.Description,
                        metadata?.IconUrl,
                        categorySummary,
                        RecommendedCatalogContentKind.Mod,
                        ResolvedFileName: resolvedProject.FileName,
                        ResolvedDownloadUrl: resolvedProject.DownloadUrl,
                        ResolvedFileSha1: resolvedProject.FileSha1,
                        RequiredDependencyProjectIds: resolvedProject.RequiredDependencyProjectIds));
                    continue;
                }
                catch
                {
                    continue;
                }
            }

            if (metadata is null)
            {
                try
                {
                    metadata = await GetModrinthProjectMetadataAsync(
                        firstEntry.ProjectId,
                        metadataCache,
                        cancellationToken);
                }
                catch
                {
                    // Keep the catalog usable even if one Modrinth metadata request fails.
                }
            }

            result.Add(new RecommendedModCatalogItem(
                metadata?.ProjectId ?? firstEntry.ProjectId,
                metadata?.Title ?? firstEntry.DisplayName,
                !string.IsNullOrWhiteSpace(metadata?.Description) ? metadata.Description! : firstEntry.Description,
                metadata?.IconUrl,
                categorySummary,
                RecommendedCatalogContentKind.Mod));
        }

        return result
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<IReadOnlyList<RecommendedModCatalogItem>> FetchModrinthCatalogAsync(
        RecommendedCatalogContentKind contentKind,
        ModLoaderKind? loaderKind,
        string minecraftBaseVersionId,
        CancellationToken cancellationToken)
    {
        var facets = new List<string[]>
        {
            new[] { $"project_type:{GetModrinthProjectTypeToken(contentKind)}" },
            new[] { $"versions:{NormalizeMinecraftBaseVersionId(minecraftBaseVersionId)}" }
        };

        if (contentKind == RecommendedCatalogContentKind.Mod)
        {
            var loaderToken = loaderKind switch
            {
                ModLoaderKind.Fabric => "fabric",
                ModLoaderKind.Forge => "forge",
                _ => throw new NotSupportedException($"Быстрый каталог Modrinth не поддерживается для {loaderKind}.")
            };

            facets.Add(new[] { $"categories:{loaderToken}" });
            facets.Add(new[] { "client_side:required", "client_side:optional" });
        }

        var facetsJson = JsonSerializer.Serialize(facets);
        var url = string.Format(
            CultureInfo.InvariantCulture,
            ModrinthProjectSearchUrlTemplate,
            ModrinthCatalogSearchLimit,
            Uri.EscapeDataString(facetsJson));

        using var document = await GetJsonDocumentWithUserAgentAsync(url, cancellationToken);
        if (!document.RootElement.TryGetProperty("hits", out var hitsElement) ||
            hitsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<RecommendedModCatalogItem>(ModrinthCatalogSearchLimit);
        var seenProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in hitsElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectId = hit.TryGetProperty("project_id", out var projectIdElement)
                ? projectIdElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(projectId) || !seenProjectIds.Add(projectId))
            {
                continue;
            }

            var displayName = hit.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = hit.TryGetProperty("slug", out var slugElement)
                    ? slugElement.GetString()
                    : projectId;
            }

            var description = hit.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;
            var iconUrl = hit.TryGetProperty("icon_url", out var iconUrlElement)
                ? iconUrlElement.GetString()
                : null;

            result.Add(new RecommendedModCatalogItem(
                projectId,
                displayName ?? projectId,
                string.IsNullOrWhiteSpace(description)
                    ? GetCatalogFallbackDescription(contentKind)
                    : description,
                string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl,
                BuildModrinthCategorySummary(hit),
                contentKind,
                contentKind == RecommendedCatalogContentKind.Modpack ? "Скачать" : "Установить"));
        }

        return result;
    }

    private static bool HasResolvedCatalogTargets(IReadOnlyList<RecommendedModCatalogItem> catalog)
    {
        return catalog.Count > 0 &&
               catalog.All(item =>
            (!string.IsNullOrWhiteSpace(item.ResolvedFileName) &&
             !string.IsNullOrWhiteSpace(item.ResolvedDownloadUrl)));
    }

    private static async Task<IReadOnlyList<RecommendedModCatalogItem>> ResolveFetchedModrinthCatalogAsync(
        RecommendedCatalogContentKind contentKind,
        IReadOnlyList<RecommendedModCatalogItem> catalog,
        string minecraftBaseVersionId,
        ModLoaderKind? loaderKind,
        CancellationToken cancellationToken)
    {
        if (catalog.Count == 0)
        {
            return catalog;
        }

        var resolvedCatalog = new RecommendedModCatalogItem[catalog.Count];
        using var semaphore = new SemaphoreSlim(6);
        var tasks = catalog.Select(async (item, index) =>
        {
            if (!string.IsNullOrWhiteSpace(item.ResolvedFileName) &&
                !string.IsNullOrWhiteSpace(item.ResolvedDownloadUrl))
            {
                resolvedCatalog[index] = item;
                return;
            }

            if (TryReadModrinthVersionResolutionCache(
                    contentKind,
                    item.ProjectId,
                    minecraftBaseVersionId,
                    loaderKind,
                    out var cachedVersion))
            {
                resolvedCatalog[index] = cachedVersion is null
                    ? item
                    : item with
                    {
                        ResolvedFileName = cachedVersion.FileName,
                        ResolvedDownloadUrl = cachedVersion.DownloadUrl,
                        ResolvedFileSha1 = cachedVersion.FileSha1,
                        RequiredDependencyProjectIds = cachedVersion.RequiredDependencyProjectIds
                    };
                return;
            }

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                switch (contentKind)
                {
                    case RecommendedCatalogContentKind.Mod when loaderKind is ModLoaderKind.Fabric or ModLoaderKind.Forge:
                    {
                        var version = await TryGetCompatibleModrinthVersionAsync(
                            item.ProjectId,
                            minecraftBaseVersionId,
                            loaderKind.Value,
                            cancellationToken);
                        WriteModrinthVersionResolutionCache(
                            contentKind,
                            item.ProjectId,
                            minecraftBaseVersionId,
                            loaderKind,
                            version);
                        resolvedCatalog[index] = version is null
                            ? item
                            : item with
                            {
                                ResolvedFileName = version.FileName,
                                ResolvedDownloadUrl = version.DownloadUrl,
                                ResolvedFileSha1 = version.FileSha1,
                                RequiredDependencyProjectIds = version.RequiredDependencyProjectIds
                            };
                        break;
                    }
                    case RecommendedCatalogContentKind.Shader:
                    case RecommendedCatalogContentKind.ResourcePack:
                    case RecommendedCatalogContentKind.Modpack:
                    {
                        var version = await TryGetCompatibleModrinthContentVersionAsync(
                            item.ProjectId,
                            minecraftBaseVersionId,
                            cancellationToken);
                        WriteModrinthVersionResolutionCache(
                            contentKind,
                            item.ProjectId,
                            minecraftBaseVersionId,
                            loaderKind,
                            version);
                        resolvedCatalog[index] = version is null
                            ? item
                            : item with
                            {
                                ResolvedFileName = version.FileName,
                                ResolvedDownloadUrl = version.DownloadUrl,
                                ResolvedFileSha1 = version.FileSha1
                            };
                        break;
                    }
                    default:
                        resolvedCatalog[index] = item;
                        break;
                }
            }
            catch
            {
                resolvedCatalog[index] = item;
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return resolvedCatalog;
    }

    private static string GetCatalogFallbackDescription(RecommendedCatalogContentKind contentKind)
    {
        return contentKind switch
        {
            RecommendedCatalogContentKind.Mod => "Совместимый мод для выбранной версии.",
            RecommendedCatalogContentKind.Shader => "Совместимый шейдерпак для выбранной версии.",
            RecommendedCatalogContentKind.ResourcePack => "Совместимый ресурспак для выбранной версии.",
            RecommendedCatalogContentKind.Modpack => "Совместимая готовая сборка для выбранной версии.",
            _ => "Совместимый материал для выбранной версии."
        };
    }

    private static string GetModrinthProjectTypeToken(RecommendedCatalogContentKind contentKind)
    {
        return contentKind switch
        {
            RecommendedCatalogContentKind.Mod => "mod",
            RecommendedCatalogContentKind.Shader => "shader",
            RecommendedCatalogContentKind.ResourcePack => "resourcepack",
            RecommendedCatalogContentKind.Modpack => "modpack",
            _ => throw new NotSupportedException($"Неизвестный тип каталога: {contentKind}")
        };
    }

    private static string BuildModrinthCategorySummary(JsonElement hit)
    {
        var categories = ReadModrinthCategories(hit);
        if (categories.Length == 0)
        {
            return string.Empty;
        }

        return $"Категория: {string.Join(", ", categories.Take(3))}";
    }

    private static string[] ReadModrinthCategories(JsonElement hit)
    {
        if (TryReadModrinthCategoryArray(hit, "display_categories", out var displayCategories) &&
            displayCategories.Length > 0)
        {
            return displayCategories;
        }

        return TryReadModrinthCategoryArray(hit, "categories", out var fallbackCategories)
            ? fallbackCategories
            : [];
    }

    private static bool TryReadModrinthCategoryArray(JsonElement hit, string propertyName, out string[] categories)
    {
        categories = [];
        if (!hit.TryGetProperty(propertyName, out var categoriesElement) ||
            categoriesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        categories = categoriesElement
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Where(value => !IgnoredModrinthCategories.Contains(value))
            .Select(FormatModrinthCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return true;
    }

    private static string FormatModrinthCategory(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "optimization" => "Оптимизация",
            "library" => "Библиотека",
            "utility" => "Утилиты",
            "adventure" => "Приключения",
            "decoration" => "Декор",
            "equipment" => "Снаряжение",
            "food" => "Еда",
            "game-mechanics" => "Геймплей",
            "magic" => "Магия",
            "management" => "Управление",
            "mobs" => "Существа",
            "social" => "Связь",
            "storage" => "Хранилище",
            "technology" => "Технологии",
            "transportation" => "Навигация",
            "worldgen" => "Мир",
            "audio" => "Звук",
            "cursed" => "Эксперименты",
            "economy" => "Экономика",
            "misc" => "Разное",
            "monitoring" => "Диагностика",
            _ => string.IsNullOrWhiteSpace(category)
                ? string.Empty
                : char.ToUpperInvariant(category[0]) + category[1..]
        };
    }

    public async Task<ModPackInstallResult> InstallRecommendedModsAsync(
        string minecraftBaseVersionId,
        string installedVersionId,
        ModLoaderKind loaderKind,
        IReadOnlyList<RecommendedModProject> projects,
        LauncherProfile profile = LauncherProfile.Vanilla,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (projects is null || projects.Count == 0)
        {
            throw new ArgumentException("Нужно выбрать хотя бы один мод.", nameof(projects));
        }

        return await InstallRecommendedProjectsAsync(
            minecraftBaseVersionId,
            installedVersionId,
            loaderKind,
            projects,
            profile,
            progress,
            cancellationToken);
    }

    public async Task<CatalogAssetInstallResult> InstallCatalogAssetAsync(
        RecommendedCatalogContentKind contentKind,
        string projectId,
        string displayName,
        string minecraftBaseVersionId,
        string? installedVersionId = null,
        LauncherProfile profile = LauncherProfile.Vanilla,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (contentKind == RecommendedCatalogContentKind.Mod)
        {
            throw new ArgumentException("Для модов используй установку через модлоадер.", nameof(contentKind));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Не указан id проекта для загрузки.", nameof(projectId));
        }

        var normalizedBaseVersionId = NormalizeMinecraftBaseVersionId(minecraftBaseVersionId);
        var resolvedTargetVersionId = string.IsNullOrWhiteSpace(installedVersionId)
            ? normalizedBaseVersionId
            : installedVersionId.Trim();
        var gameDirectory = GetGameDirectory(profile);
        Directory.CreateDirectory(gameDirectory);
        EnsureProfileDirectories(gameDirectory, profile);

        var destinationDirectory = contentKind switch
        {
            RecommendedCatalogContentKind.Shader => Path.Combine(GetVersionInstanceDirectory(profile, resolvedTargetVersionId), "shaderpacks"),
            RecommendedCatalogContentKind.ResourcePack => Path.Combine(GetVersionInstanceDirectory(profile, resolvedTargetVersionId), "resourcepacks"),
            RecommendedCatalogContentKind.Modpack => Path.Combine(gameDirectory, "modpacks"),
            _ => throw new NotSupportedException($"Неизвестный тип каталога: {contentKind}")
        };
        Directory.CreateDirectory(destinationDirectory);

        progress?.Report(new LauncherProgress(
            $"Подбираю файл: {displayName}",
            0,
            1));

        var versionFile = await TryGetCompatibleModrinthContentVersionAsync(
            projectId,
            normalizedBaseVersionId,
            cancellationToken);
        if (versionFile is null)
        {
            throw new InvalidOperationException(
                $"Для {displayName} не найден совместимый файл под Minecraft {normalizedBaseVersionId}.");
        }

        var downloadStage = $"Скачиваю {displayName}";
        progress?.Report(new LauncherProgress(downloadStage, 0, 1));

        var destinationPath = Path.Combine(destinationDirectory, versionFile.FileName);
        var downloaded = await DownloadFileIfNeededAsync(
            versionFile.DownloadUrl,
            destinationPath,
            versionFile.FileSha1,
            cancellationToken,
            CreateStageDownloadProgressReporter(progress, downloadStage, 0, 1, 1));

        return new CatalogAssetInstallResult(
            destinationDirectory,
            versionFile.FileName,
            downloaded);
    }

    private async Task<ModPackInstallResult> InstallRecommendedProjectsAsync(
        string minecraftBaseVersionId,
        string installedVersionId,
        ModLoaderKind loaderKind,
        IReadOnlyList<RecommendedModProject> projects,
        LauncherProfile profile,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (loaderKind is not (ModLoaderKind.Fabric or ModLoaderKind.Forge))
        {
            throw new NotSupportedException("Автоустановка модов сейчас поддерживается только для Fabric и Forge.");
        }

        var normalizedBaseVersionId = NormalizeMinecraftBaseVersionId(minecraftBaseVersionId);
        var resolvedInstalledVersionId = installedVersionId?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedInstalledVersionId))
        {
            throw new ArgumentException("ID установленной версии не указан.", nameof(installedVersionId));
        }
        var requestedProjects = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.ProjectId))
            .DistinctBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedProjects.Length == 0)
        {
            throw new InvalidOperationException("Список модов для установки пустой.");
        }

        var gameDirectory = GetGameDirectory(profile);
        Directory.CreateDirectory(gameDirectory);
        EnsureProfileDirectories(gameDirectory, profile);
        _ = await ResolveVersionEntryByIdAsync(resolvedInstalledVersionId, gameDirectory, cancellationToken);

        var instanceDirectory = GetVersionInstanceDirectory(profile, resolvedInstalledVersionId);
        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var metadataCache = new Dictionary<string, ModrinthProjectMetadata>(StringComparer.OrdinalIgnoreCase);
        var resolvedProjects = new Dictionary<string, ModrinthResolvedMod>(StringComparer.OrdinalIgnoreCase);
        var unresolvedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolutionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentProject = 0;

        foreach (var project in requestedProjects)
        {
            currentProject++;
            progress?.Report(new LauncherProgress(
                $"Подбираю моды {currentProject}/{requestedProjects.Length}: {project.DisplayName}",
                currentProject,
                requestedProjects.Length));
            await ResolveRecommendedModProjectAsync(
                project,
                normalizedBaseVersionId,
                loaderKind,
                metadataCache,
                resolvedProjects,
                unresolvedProjects,
                resolutionStack,
                cancellationToken);
        }

        var orderedResolvedProjects = resolvedProjects.Values
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var downloadedFiles = new List<string>(orderedResolvedProjects.Length);
        var installedProjects = new List<InstalledModProjectInfo>(orderedResolvedProjects.Length);
        var installIndex = 0;
        foreach (var project in orderedResolvedProjects)
        {
            installIndex++;
            var installStage = $"Скачиваю {project.DisplayName} ({installIndex}/{orderedResolvedProjects.Length})";
            progress?.Report(new LauncherProgress(
                installStage,
                installIndex - 1,
                orderedResolvedProjects.Length));

            var destinationPath = Path.Combine(modsDirectory, project.FileName);
            var downloaded = await DownloadFileIfNeededAsync(
                project.DownloadUrl,
                destinationPath,
                project.FileSha1,
                cancellationToken,
                CreateStageDownloadProgressReporter(
                    progress,
                    installStage,
                    installIndex - 1,
                    1,
                    orderedResolvedProjects.Length));
            if (downloaded)
            {
                downloadedFiles.Add(project.FileName);
            }
            else
            {
                skippedProjects.Add(project.DisplayName);
            }

            installedProjects.Add(new InstalledModProjectInfo(
                project.ProjectId,
                project.DisplayName,
                project.FileName,
                destinationPath,
                downloaded,
                project.ProjectAliases));
        }

        var missingProjects = unresolvedProjects
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var skippedProjectNames = skippedProjects
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (downloadedFiles.Count == 0 && skippedProjectNames.Length == 0)
        {
            throw new InvalidOperationException(
                $"Для {normalizedBaseVersionId} + {GetLoaderName(loaderKind)} не нашлось совместимых модов в выбранных наборах.");
        }

        return new ModPackInstallResult(
            modsDirectory,
            downloadedFiles.Count,
            skippedProjectNames.Length,
            missingProjects.Length,
            downloadedFiles,
            skippedProjectNames,
            missingProjects,
            installedProjects);
    }

    private async Task EnsureFabricApiForLaunchAsync(
        JsonElement versionRoot,
        string resolvedVersionId,
        string? minecraftBaseVersionId,
        string instanceDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsFabricVersion(versionRoot, resolvedVersionId) ||
            string.IsNullOrWhiteSpace(minecraftBaseVersionId))
        {
            return;
        }

        var modsDirectory = Path.Combine(instanceDirectory, "mods");
        Directory.CreateDirectory(modsDirectory);

        var installedModFiles = Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).ToArray();
        if (installedModFiles.Length == 0)
        {
            return;
        }

        var hasFabricApi = installedModFiles.Any(path =>
            Path.GetFileName(path).Contains("fabric-api", StringComparison.OrdinalIgnoreCase));
        if (hasFabricApi)
        {
            return;
        }

        if (!NeedsFabricApiAutoInstall(installedModFiles))
        {
            return;
        }

        progress?.Report(new LauncherProgress("Проверяю Fabric API...", 0, 1));

        var fabricApiVersion = await TryGetCompatibleModrinthVersionAsync(
            "fabric-api",
            minecraftBaseVersionId,
            ModLoaderKind.Fabric,
            cancellationToken);
        if (fabricApiVersion is null)
        {
            return;
        }

        var destinationPath = Path.Combine(modsDirectory, fabricApiVersion.FileName);
        await DownloadFileIfNeededAsync(
            fabricApiVersion.DownloadUrl,
            destinationPath,
            fabricApiVersion.FileSha1,
            cancellationToken);
    }

    private static async Task ResolveRecommendedModProjectAsync(
        RecommendedModProject requestedProject,
        string minecraftBaseVersionId,
        ModLoaderKind loaderKind,
        IDictionary<string, ModrinthProjectMetadata> metadataCache,
        IDictionary<string, ModrinthResolvedMod> resolvedProjects,
        ISet<string> unresolvedProjects,
        ISet<string> resolutionStack,
        CancellationToken cancellationToken)
    {
        var metadata = await GetModrinthProjectMetadataAsync(requestedProject.ProjectId, metadataCache, cancellationToken);
        if (resolvedProjects.ContainsKey(metadata.ProjectId))
        {
            return;
        }

        if (!resolutionStack.Add(metadata.ProjectId))
        {
            return;
        }

        try
        {
            var version = TryBuildPinnedModrinthVersionInfo(requestedProject);
            if (version is null)
            {
                version = await TryGetCompatibleModrinthVersionAsync(
                    metadata.ProjectId,
                    minecraftBaseVersionId,
                    loaderKind,
                    cancellationToken);
            }

            if (version is null)
            {
                unresolvedProjects.Add(string.IsNullOrWhiteSpace(requestedProject.DisplayName) ? metadata.Title : requestedProject.DisplayName);
                return;
            }

            foreach (var dependencyProjectId in version.RequiredDependencyProjectIds)
            {
                await ResolveRecommendedModProjectAsync(
                    new RecommendedModProject(
                        dependencyProjectId,
                        dependencyProjectId,
                        string.Empty),
                    minecraftBaseVersionId,
                    loaderKind,
                    metadataCache,
                    resolvedProjects,
                    unresolvedProjects,
                    resolutionStack,
                    cancellationToken);
            }

            var hasMissingDependency = version.RequiredDependencyProjectIds.Any(dependencyProjectId =>
                !resolvedProjects.ContainsKey(dependencyProjectId));
            if (hasMissingDependency)
            {
                unresolvedProjects.Add(string.IsNullOrWhiteSpace(requestedProject.DisplayName) ? metadata.Title : requestedProject.DisplayName);
                return;
            }

            resolvedProjects[metadata.ProjectId] = new ModrinthResolvedMod(
                metadata.ProjectId,
                string.IsNullOrWhiteSpace(requestedProject.DisplayName) ? metadata.Title : requestedProject.DisplayName,
                version.FileName,
                version.DownloadUrl,
                version.FileSha1,
                version.RequiredDependencyProjectIds,
                BuildProjectAliases(requestedProject.ProjectId, metadata));
        }
        finally
        {
            resolutionStack.Remove(metadata.ProjectId);
        }
    }

    private static IReadOnlyList<string> BuildProjectAliases(string? requestedProjectId, ModrinthProjectMetadata metadata)
    {
        return new[]
            {
                requestedProjectId,
                metadata.ProjectId,
                metadata.Slug
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsFabricVersion(JsonElement versionRoot, string versionId)
    {
        if (!string.IsNullOrWhiteSpace(versionId) &&
            versionId.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!versionRoot.TryGetProperty("libraries", out var librariesElement) ||
            librariesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var library in librariesElement.EnumerateArray())
        {
            var libraryName = library.TryGetProperty("name", out var libraryNameElement) &&
                              libraryNameElement.ValueKind == JsonValueKind.String
                ? libraryNameElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                continue;
            }

            if (libraryName.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase) ||
                libraryName.StartsWith("fabric-loader:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<ModrinthProjectMetadata> GetModrinthProjectMetadataAsync(
        string projectLookupId,
        IDictionary<string, ModrinthProjectMetadata> metadataCache,
        CancellationToken cancellationToken)
    {
        if (metadataCache.TryGetValue(projectLookupId, out var cachedMetadata))
        {
            return cachedMetadata;
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            ModrinthProjectUrlTemplate,
            Uri.EscapeDataString(projectLookupId));

        using var document = await GetJsonDocumentWithUserAgentAsync(url, cancellationToken);
        var root = document.RootElement;
        var projectId = root.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidDataException($"Modrinth вернул проект без id: {projectLookupId}");
        }

        var slug = root.TryGetProperty("slug", out var slugElement)
            ? slugElement.GetString()
            : null;
        var title = root.TryGetProperty("title", out var titleElement)
            ? titleElement.GetString()
            : null;
        var description = root.TryGetProperty("description", out var descriptionElement)
            ? descriptionElement.GetString()
            : null;
        var iconUrl = root.TryGetProperty("icon_url", out var iconUrlElement)
            ? iconUrlElement.GetString()
            : null;

        var metadata = new ModrinthProjectMetadata(
            projectId,
            slug ?? projectLookupId,
            string.IsNullOrWhiteSpace(title) ? projectLookupId : title,
            description,
            iconUrl);

        metadataCache[projectLookupId] = metadata;
        metadataCache[metadata.ProjectId] = metadata;
        if (!string.IsNullOrWhiteSpace(metadata.Slug))
        {
            metadataCache[metadata.Slug] = metadata;
        }

        return metadata;
    }

    private static async Task<ModrinthVersionFileInfo?> TryGetCompatibleModrinthVersionAsync(
        string projectId,
        string minecraftBaseVersionId,
        ModLoaderKind loaderKind,
        CancellationToken cancellationToken)
    {
        var loaderToken = loaderKind switch
        {
            ModLoaderKind.Fabric => "fabric",
            ModLoaderKind.Forge => "forge",
            ModLoaderKind.OptiFine => "optifine",
            _ => throw new NotSupportedException($"Modrinth не поддерживается для {loaderKind}.")
        };

        var encodedLoaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loaderToken }));
        var encodedGameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftBaseVersionId }));
        var url = string.Format(
            CultureInfo.InvariantCulture,
            ModrinthProjectVersionsUrlTemplate,
            Uri.EscapeDataString(projectId),
            encodedLoaders,
            encodedGameVersions);

        using var document = await GetJsonDocumentWithUserAgentAsync(url, cancellationToken);
        var exactMatch = TrySelectCompatibleModrinthVersion(
            EnumerateArrayLike(document.RootElement),
            minecraftBaseVersionId,
            allowSeriesFallback: false);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var fallbackUrl = string.Format(
            CultureInfo.InvariantCulture,
            ModrinthProjectVersionsByLoaderUrlTemplate,
            Uri.EscapeDataString(projectId),
            encodedLoaders);
        using var fallbackDocument = await GetJsonDocumentWithUserAgentAsync(fallbackUrl, cancellationToken);
        return TrySelectCompatibleModrinthVersion(
            EnumerateArrayLike(fallbackDocument.RootElement),
            minecraftBaseVersionId,
            allowSeriesFallback: true);
    }

    private static async Task<ModrinthVersionFileInfo?> TryGetCompatibleModrinthContentVersionAsync(
        string projectId,
        string minecraftBaseVersionId,
        CancellationToken cancellationToken)
    {
        var encodedGameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftBaseVersionId }));
        var exactUrl = string.Format(
            CultureInfo.InvariantCulture,
            ModrinthProjectVersionsByGameVersionUrlTemplate,
            Uri.EscapeDataString(projectId),
            encodedGameVersions);
        using var exactDocument = await GetJsonDocumentWithUserAgentAsync(exactUrl, cancellationToken);
        var exactMatch = TrySelectCompatibleModrinthVersion(
            EnumerateArrayLike(exactDocument.RootElement),
            minecraftBaseVersionId,
            allowSeriesFallback: false);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var fallbackUrl = string.Format(
            CultureInfo.InvariantCulture,
            ModrinthProjectVersionsAllUrlTemplate,
            Uri.EscapeDataString(projectId));
        using var fallbackDocument = await GetJsonDocumentWithUserAgentAsync(fallbackUrl, cancellationToken);
        return TrySelectCompatibleModrinthVersion(
            EnumerateArrayLike(fallbackDocument.RootElement),
            minecraftBaseVersionId,
            allowSeriesFallback: true);
    }

    private static ModrinthVersionFileInfo? TrySelectCompatibleModrinthVersion(
        IEnumerable<JsonElement> versionElements,
        string minecraftBaseVersionId,
        bool allowSeriesFallback)
    {
        ModrinthVersionFileInfo? bestFallback = null;
        var bestFallbackRank = int.MinValue;

        foreach (var versionElement in versionElements)
        {
            if (!TryReadPreferredModrinthFile(versionElement, out var fileInfo))
            {
                continue;
            }

            var dependencyIds = ParseRequiredDependencyProjectIds(versionElement);
            var candidate = new ModrinthVersionFileInfo(
                fileInfo.FileName,
                fileInfo.DownloadUrl,
                fileInfo.FileSha1,
                dependencyIds);
            if (!allowSeriesFallback)
            {
                return candidate;
            }

            var compatibilityRank = GetModrinthCompatibilityRank(versionElement, minecraftBaseVersionId);
            if (compatibilityRank >= 1000)
            {
                return candidate;
            }

            if (compatibilityRank <= bestFallbackRank || compatibilityRank <= 0)
            {
                continue;
            }

            bestFallbackRank = compatibilityRank;
            bestFallback = candidate;
        }

        return bestFallback;
    }

    private static ModrinthVersionFileInfo? TryBuildPinnedModrinthVersionInfo(RecommendedModProject requestedProject)
    {
        if (string.IsNullOrWhiteSpace(requestedProject.ResolvedFileName) ||
            string.IsNullOrWhiteSpace(requestedProject.ResolvedDownloadUrl))
        {
            return null;
        }

        var dependencyProjectIds = requestedProject.RequiredDependencyProjectIds?
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];

        return new ModrinthVersionFileInfo(
            requestedProject.ResolvedFileName,
            requestedProject.ResolvedDownloadUrl,
            requestedProject.ResolvedFileSha1,
            dependencyProjectIds);
    }

    private static bool TryReadPreferredModrinthFile(JsonElement versionElement, out ModrinthFileInfo fileInfo)
    {
        fileInfo = null!;
        if (!versionElement.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement? selectedFile = null;
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (fileElement.TryGetProperty("primary", out var primaryElement) &&
                primaryElement.ValueKind == JsonValueKind.True)
            {
                selectedFile = fileElement;
                break;
            }

            if (selectedFile is null)
            {
                selectedFile = fileElement;
            }
        }

        if (selectedFile is not JsonElement preferredFile)
        {
            return false;
        }

        var downloadUrl = preferredFile.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : null;
        var fileName = preferredFile.TryGetProperty("filename", out var fileNameElement)
            ? fileNameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string? fileSha1 = null;
        if (preferredFile.TryGetProperty("hashes", out var hashesElement) &&
            hashesElement.ValueKind == JsonValueKind.Object &&
            hashesElement.TryGetProperty("sha1", out var sha1Element))
        {
            fileSha1 = sha1Element.GetString();
        }

        fileInfo = new ModrinthFileInfo(fileName, downloadUrl, fileSha1);
        return true;
    }

    private static IReadOnlyList<string> ParseRequiredDependencyProjectIds(JsonElement versionElement)
    {
        if (!versionElement.TryGetProperty("dependencies", out var dependenciesElement) ||
            dependenciesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return dependenciesElement
            .EnumerateArray()
            .Where(dependency =>
                dependency.TryGetProperty("dependency_type", out var typeElement) &&
                string.Equals(typeElement.GetString(), "required", StringComparison.OrdinalIgnoreCase) &&
                dependency.TryGetProperty("project_id", out var projectIdElement) &&
                projectIdElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(projectIdElement.GetString()))
            .Select(dependency => dependency.GetProperty("project_id").GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetModrinthCompatibilityRank(JsonElement versionElement, string requestedMinecraftVersionId)
    {
        var requestedVersionId = NormalizeMinecraftBaseVersionId(requestedMinecraftVersionId);
        var requestedSeries = GetMinecraftVersionSeries(requestedVersionId);
        var requestedPatch = GetMinecraftVersionPatch(requestedVersionId);
        var bestRank = 0;

        foreach (var gameVersion in EnumerateModrinthGameVersions(versionElement))
        {
            var normalizedGameVersion = NormalizeMinecraftBaseVersionId(gameVersion);
            if (string.Equals(normalizedGameVersion, requestedVersionId, StringComparison.OrdinalIgnoreCase))
            {
                return 1000;
            }

            if (!string.Equals(GetMinecraftVersionSeries(normalizedGameVersion), requestedSeries, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var gamePatch = GetMinecraftVersionPatch(normalizedGameVersion);
            var patchDistance = requestedPatch.HasValue && gamePatch.HasValue
                ? Math.Abs(requestedPatch.Value - gamePatch.Value)
                : 0;
            var rank = 500 - Math.Min(patchDistance, 200);
            if (rank > bestRank)
            {
                bestRank = rank;
            }
        }

        return bestRank;
    }

    private static IEnumerable<string> EnumerateModrinthGameVersions(JsonElement versionElement)
    {
        if (!versionElement.TryGetProperty("game_versions", out var gameVersionsElement) ||
            gameVersionsElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var gameVersionElement in gameVersionsElement.EnumerateArray())
        {
            if (gameVersionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var gameVersion = gameVersionElement.GetString();
            if (!string.IsNullOrWhiteSpace(gameVersion))
            {
                yield return gameVersion;
            }
        }
    }

    private static string GetMinecraftVersionSeries(string minecraftVersionId)
    {
        var parts = NormalizeMinecraftBaseVersionId(minecraftVersionId)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : minecraftVersionId;
    }

    private static int? GetMinecraftVersionPatch(string minecraftVersionId)
    {
        var parts = NormalizeMinecraftBaseVersionId(minecraftVersionId)
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        return int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var patch)
            ? patch
            : null;
    }

    private static async Task<JsonDocument> GetJsonDocumentWithUserAgentAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttemptsPerCandidate = 6;
        var attempted = new List<string>();
        HttpStatusCode? lastStatus = null;
        Exception? lastError = null;

        foreach (var candidate in EnumerateDownloadFallbackUrls(url))
        {
            if (attempted.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            attempted.Add(candidate);
            for (var attempt = 1; attempt <= maxAttemptsPerCandidate; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                request.Headers.UserAgent.ParseAdd("VesperLauncher/1.0");
                var useModrinthGate = IsModrinthApiUrl(candidate);
                if (useModrinthGate)
                {
                    await ModrinthApiRequestGate.WaitAsync(cancellationToken);
                }

                try
                {
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    }

                    lastStatus = response.StatusCode;
                    if (attempt < maxAttemptsPerCandidate && IsTransientHttpStatus(response.StatusCode))
                    {
                        await Task.Delay(GetRetryDelay(response.Headers, response.StatusCode, attempt), cancellationToken);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                           !cancellationToken.IsCancellationRequested)
                {
                    lastError = ex;
                    if (attempt < maxAttemptsPerCandidate)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(6, attempt * 2)), cancellationToken);
                        continue;
                    }
                }
                finally
                {
                    if (useModrinthGate)
                    {
                        ModrinthApiRequestGate.Release();
                    }
                }

                break;
            }
        }

        var statusText = lastStatus.HasValue ? $"{(int)lastStatus.Value} {lastStatus.Value}" : "unknown";
        throw new HttpRequestException(
            $"Не удалось получить JSON. Статус: {statusText}. URL: {url}. " +
            $"Проверено: {string.Join(" -> ", attempted)}",
            lastError);
    }

    private static string GetLoaderName(ModLoaderKind loaderKind)
    {
        return loaderKind switch
        {
            ModLoaderKind.Forge => "Forge",
            ModLoaderKind.Fabric => "Fabric",
            ModLoaderKind.OptiFine => "OptiFine",
            _ => loaderKind.ToString()
        };
    }

    private static List<MinecraftVersionEntry> LoadBundledVersions()
    {
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, BundledVersionsDirectoryName);
        if (!Directory.Exists(bundledRoot))
        {
            return [];
        }

        var versions = new List<MinecraftVersionEntry>();
        foreach (var versionDirectory in Directory.EnumerateDirectories(bundledRoot))
        {
            var versionId = Path.GetFileName(versionDirectory);
            if (string.IsNullOrWhiteSpace(versionId))
            {
                continue;
            }

            var versionJsonPath = Path.Combine(versionDirectory, $"{versionId}.json");
            if (!File.Exists(versionJsonPath))
            {
                continue;
            }

            var type = "release";
            var releaseTime = new DateTimeOffset(File.GetLastWriteTimeUtc(versionJsonPath), TimeSpan.Zero);
            string? baseVersionId = null;

            try
            {
                using var stream = File.OpenRead(versionJsonPath);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;

                if (root.TryGetProperty("type", out var typeElement))
                {
                    type = typeElement.GetString() ?? type;
                }

                if (root.TryGetProperty("releaseTime", out var releaseTimeElement))
                {
                    var releaseTimeText = releaseTimeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(releaseTimeText) &&
                        DateTimeOffset.TryParse(
                            releaseTimeText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out var parsedReleaseTime))
                    {
                            releaseTime = parsedReleaseTime;
                    }
                }

                baseVersionId = TryDetermineBaseMinecraftVersionId(versionId, root);
            }
            catch (JsonException)
            {
                // Skip malformed bundled metadata and keep launcher working.
                continue;
            }

            versions.Add(new MinecraftVersionEntry(
                versionId,
                type,
                releaseTime,
                "bundled",
                string.Empty,
                BaseVersionId: baseVersionId));
        }

        return versions;
    }

    private static List<MinecraftVersionEntry> LoadExternalGameVersions()
    {
        var versions = new List<MinecraftVersionEntry>();
        foreach (var gameDirectory in EnumerateKnownGameDirectories())
        {
            var versionsDirectory = Path.Combine(gameDirectory, "versions");
            if (!Directory.Exists(versionsDirectory))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(versionsDirectory))
            {
                var versionId = Path.GetFileName(versionDirectory);
                if (string.IsNullOrWhiteSpace(versionId))
                {
                    continue;
                }

                var versionJsonPath = Path.Combine(versionDirectory, $"{versionId}.json");
                if (!File.Exists(versionJsonPath))
                {
                    continue;
                }

                var type = "local";
                var releaseTime = new DateTimeOffset(File.GetLastWriteTimeUtc(versionJsonPath), TimeSpan.Zero);

                try
                {
                    using var stream = File.OpenRead(versionJsonPath);
                    using var document = JsonDocument.Parse(stream);
                    var root = document.RootElement;

                    if (root.TryGetProperty("type", out var typeElement))
                    {
                        type = typeElement.GetString() ?? type;
                    }

                    if (TryReadReleaseTime(root, out var parsedReleaseTime))
                    {
                        releaseTime = parsedReleaseTime;
                    }

                    type = NormalizeLocalVersionType(versionId, type, root);
                    var baseVersionId = TryDetermineBaseMinecraftVersionId(versionId, root);

                    versions.Add(new MinecraftVersionEntry(
                        versionId,
                        type,
                        releaseTime,
                        "local",
                        string.Empty,
                        LocalMetadataPath: versionJsonPath,
                        SourceGameDirectory: gameDirectory,
                        BaseVersionId: baseVersionId));
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }

        return versions;
    }

    private static string? TryDetermineBaseMinecraftVersionId(string versionId, JsonElement root)
    {
        if (root.TryGetProperty("inheritsFrom", out var inheritsFromElement) &&
            inheritsFromElement.ValueKind == JsonValueKind.String)
        {
            var inheritedBase = TryExtractLikelyMinecraftVersionId(inheritsFromElement.GetString());
            if (!string.IsNullOrWhiteSpace(inheritedBase))
            {
                return inheritedBase;
            }
        }

        if (root.TryGetProperty("jar", out var jarElement) &&
            jarElement.ValueKind == JsonValueKind.String)
        {
            var jarBase = TryExtractLikelyMinecraftVersionId(jarElement.GetString());
            if (!string.IsNullOrWhiteSpace(jarBase))
            {
                return jarBase;
            }
        }

        return TryExtractLikelyMinecraftVersionId(versionId);
    }

    private static string? NormalizeBaseJarVersionReference(string? rawJarVersionId, string? fallbackBaseVersionId)
    {
        var normalizedFallback = TryExtractLikelyMinecraftVersionId(fallbackBaseVersionId);
        if (string.IsNullOrWhiteSpace(rawJarVersionId))
        {
            return normalizedFallback;
        }

        var trimmedReference = rawJarVersionId.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReference))
        {
            return normalizedFallback;
        }

        var normalizedReference = TryExtractLikelyMinecraftVersionId(trimmedReference);
        return string.IsNullOrWhiteSpace(normalizedReference)
            ? trimmedReference
            : normalizedReference;
    }

    private static string? TryExtractLikelyMinecraftVersionId(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var value = rawValue.Trim();
        if (value.All(character => char.IsDigit(character) || character == '.'))
        {
            return value;
        }

        var matches = Regex.Matches(value, @"\d+\.\d+(?:\.\d+)?");
        if (matches.Count == 0)
        {
            return null;
        }

        if (value.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
        {
            return matches[^1].Value;
        }

        foreach (Match match in matches)
        {
            var candidate = match.Value;
            if (candidate.StartsWith("1.", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return matches[0].Value;
    }

    private static string NormalizeLocalVersionType(string versionId, string sourceType, JsonElement root)
    {
        var normalizedType = string.IsNullOrWhiteSpace(sourceType) ? "local" : sourceType.Trim();
        if (!normalizedType.Equals("modified", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedType;
        }

        var tags = new List<string>();
        static void AddTag(List<string> target, string tag)
        {
            if (!target.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(tag);
            }
        }

        var idLower = versionId.ToLowerInvariant();
        if (idLower.Contains("forge", StringComparison.Ordinal))
        {
            AddTag(tags, "forge");
        }

        if (idLower.Contains("optifine", StringComparison.Ordinal))
        {
            AddTag(tags, "optifine");
        }

        if (idLower.Contains("fabric", StringComparison.Ordinal))
        {
            AddTag(tags, "fabric");
        }

        if (idLower.Contains("quilt", StringComparison.Ordinal))
        {
            AddTag(tags, "quilt");
        }

        if (idLower.Contains("neoforge", StringComparison.Ordinal))
        {
            AddTag(tags, "neoforge");
        }

        if (idLower.Contains("liteloader", StringComparison.Ordinal))
        {
            AddTag(tags, "liteloader");
        }

        if (root.TryGetProperty("libraries", out var librariesElement) &&
            librariesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in librariesElement.EnumerateArray())
            {
                if (!library.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var libraryName = (nameElement.GetString() ?? string.Empty).ToLowerInvariant();
                if (libraryName.StartsWith("net.minecraftforge:", StringComparison.Ordinal) ||
                    libraryName.StartsWith("cpw.mods:", StringComparison.Ordinal))
                {
                    AddTag(tags, "forge");
                }

                if (libraryName.StartsWith("optifine:", StringComparison.Ordinal))
                {
                    AddTag(tags, "optifine");
                }

                if (libraryName.StartsWith("net.fabricmc:", StringComparison.Ordinal) ||
                    libraryName.StartsWith("fabric-loader:", StringComparison.Ordinal))
                {
                    AddTag(tags, "fabric");
                }

                if (libraryName.StartsWith("org.quiltmc:", StringComparison.Ordinal))
                {
                    AddTag(tags, "quilt");
                }

                if (libraryName.StartsWith("net.neoforged:", StringComparison.Ordinal))
                {
                    AddTag(tags, "neoforge");
                }

                if (libraryName.StartsWith("com.mumfrey:liteloader", StringComparison.Ordinal))
                {
                    AddTag(tags, "liteloader");
                }
            }
        }

        return tags.Count > 0 ? string.Join("+", tags) : normalizedType;
    }

    private static bool TryReadReleaseTime(JsonElement root, out DateTimeOffset releaseTime)
    {
        if (root.TryGetProperty("releaseTime", out var releaseTimeElement))
        {
            var releaseTimeText = releaseTimeElement.GetString();
            if (!string.IsNullOrWhiteSpace(releaseTimeText) &&
                DateTimeOffset.TryParse(
                    releaseTimeText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out releaseTime))
            {
                return true;
            }
        }

        if (root.TryGetProperty("time", out var timeElement))
        {
            var timeText = timeElement.GetString();
            if (!string.IsNullOrWhiteSpace(timeText) &&
                DateTimeOffset.TryParse(
                    timeText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out releaseTime))
            {
                return true;
            }
        }

        releaseTime = default;
        return false;
    }

    private static IEnumerable<string> EnumerateKnownGameDirectories()
    {
        var result = new List<string>();

        static void AddIfExists(List<string> target, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
            if (!Directory.Exists(expandedPath))
            {
                return;
            }

            target.Add(expandedPath);
        }

        AddIfExists(result, Environment.GetEnvironmentVariable("VESPER_MC_GAME_DIR"));

        var manyPaths = Environment.GetEnvironmentVariable("VESPER_MC_GAME_DIRS");
        if (!string.IsNullOrWhiteSpace(manyPaths))
        {
            foreach (var entry in manyPaths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddIfExists(result, entry);
            }
        }

        foreach (var legacyPath in LegacyCustomGameDirectories)
        {
            AddIfExists(result, legacyPath);
        }

        AddIfExists(result, BaseStorageDirectory.Value);
        AddIfExists(result, Path.Combine(BaseStorageDirectory.Value, "minecraft_vanilla"));
        AddIfExists(result, Path.Combine(BaseStorageDirectory.Value, "minecraft_cheat"));

        AddIfExists(result, PlatformPaths.MinecraftDirectory);

        if (OperatingSystem.IsWindows())
        {
            var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddIfExists(result, Path.Combine(appDataDirectory, ".minecraft"));
            AddIfExists(result, Path.Combine(appDataDirectory, ".tlauncher", "legacy", "Minecraft", "game"));
            AddIfExists(result, Path.Combine(appDataDirectory, ".tlauncher", "Minecraft", "game"));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveDefaultMinecraftLanguageCode()
    {
        foreach (var gameDirectory in EnumerateKnownGameDirectories())
        {
            var optionsPath = Path.Combine(gameDirectory, "options.txt");
            var languageCode = TryReadMinecraftLanguageCode(optionsPath);
            if (!string.IsNullOrWhiteSpace(languageCode))
            {
                return languageCode;
            }
        }

        return FallbackMinecraftLanguageCode;
    }

    private static string? TryReadMinecraftLanguageCode(string optionsPath)
    {
        if (string.IsNullOrWhiteSpace(optionsPath) || !File.Exists(optionsPath))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(optionsPath))
            {
                if (!line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var languageCode = line["lang:".Length..].Trim();
                return string.IsNullOrWhiteSpace(languageCode)
                    ? null
                    : languageCode.ToLowerInvariant();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void EnsureMinecraftLanguageOptions(string gameDirectory, string? preferredLanguageCode = null)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return;
        }

        try
        {
            var optionsPath = Path.Combine(gameDirectory, "options.txt");
            var normalizedPreferredLanguageCode = preferredLanguageCode?.Trim().ToLowerInvariant();
            var targetLanguageCode = !string.IsNullOrWhiteSpace(normalizedPreferredLanguageCode) &&
                                     !string.Equals(normalizedPreferredLanguageCode, "auto", StringComparison.OrdinalIgnoreCase)
                ? normalizedPreferredLanguageCode
                : string.IsNullOrWhiteSpace(DefaultMinecraftLanguageCode.Value)
                ? FallbackMinecraftLanguageCode
                : DefaultMinecraftLanguageCode.Value;
            var languageLine = $"lang:{targetLanguageCode}";

            var lines = File.Exists(optionsPath)
                ? File.ReadAllLines(optionsPath).ToList()
                : new List<string>();

            var hasLanguageLine = false;
            for (var index = 0; index < lines.Count; index++)
            {
                if (!lines[index].StartsWith("lang:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lines[index] = languageLine;
                hasLanguageLine = true;
                break;
            }

            if (!hasLanguageLine)
            {
                lines.Add(languageLine);
            }

            var content = string.Join(Environment.NewLine, lines);
            if (content.Length > 0)
            {
                content += Environment.NewLine;
            }

            File.WriteAllText(optionsPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Keep launcher startup resilient even if options.txt is locked or malformed.
        }
    }

    private static void EnsureMinecraftPerformanceOptions(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return;
        }

        try
        {
            var optionsPath = Path.Combine(gameDirectory, "options.txt");
            var looksLikeLauncherForcedPreset = HasLauncherForcedPerformancePreset(optionsPath);
            if (looksLikeLauncherForcedPreset)
            {
                EnsureOptionFileSettings(
                    optionsPath,
                    ("enableVsync", "true"),
                    ("maxFps", "120"),
                    ("framerateLimit", "120"));
            }
            else
            {
                EnsureOptionFileSettingsIfMissing(
                    optionsPath,
                    ("enableVsync", "true"),
                    ("maxFps", "120"),
                    ("framerateLimit", "120"));
            }

            var optifineOptionsPath = Path.Combine(gameDirectory, "optionsof.txt");
            if (File.Exists(optifineOptionsPath))
            {
                if (looksLikeLauncherForcedPreset)
                {
                    EnsureOptionFileSettings(optifineOptionsPath, ("ofVSync", "true"));
                }
                else
                {
                    EnsureOptionFileSettingsIfMissing(optifineOptionsPath, ("ofVSync", "true"));
                }
            }
        }
        catch
        {
            // Keep launcher startup resilient even if performance options are locked or malformed.
        }
    }

    private static bool HasLauncherForcedPerformancePreset(string optionsPath)
    {
        if (!File.Exists(optionsPath))
        {
            return false;
        }

        try
        {
            var values = ReadOptionFileSettings(optionsPath);
            return values.TryGetValue("enableVsync", out var vsync) &&
                   values.TryGetValue("maxFps", out var maxFps) &&
                   values.TryGetValue("framerateLimit", out var framerateLimit) &&
                   string.Equals(vsync, "false", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(maxFps, "260", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(framerateLimit, "260", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureOptionFileSettings(string optionsPath, params (string Key, string Value)[] settings)
    {
        var lines = File.Exists(optionsPath)
            ? File.ReadAllLines(optionsPath).ToList()
            : new List<string>();

        foreach (var (key, value) in settings)
        {
            var prefix = key + ":";
            var hasSetting = false;
            for (var index = 0; index < lines.Count; index++)
            {
                if (!lines[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                lines[index] = prefix + value;
                hasSetting = true;
                break;
            }

            if (!hasSetting)
            {
                lines.Add(prefix + value);
            }
        }

        var content = string.Join(Environment.NewLine, lines);
        if (content.Length > 0)
        {
            content += Environment.NewLine;
        }

        File.WriteAllText(optionsPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void EnsureOptionFileSettingsIfMissing(string optionsPath, params (string Key, string Value)[] settings)
    {
        var values = ReadOptionFileSettings(optionsPath);
        var missingSettings = settings
            .Where(setting => !values.ContainsKey(setting.Key))
            .ToArray();

        if (missingSettings.Length == 0)
        {
            return;
        }

        EnsureOptionFileSettings(optionsPath, missingSettings);
    }

    private static Dictionary<string, string> ReadOptionFileSettings(string optionsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(optionsPath))
        {
            return result;
        }

        foreach (var rawLine in File.ReadAllLines(optionsPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var separatorIndex = rawLine.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = rawLine[..separatorIndex].Trim();
            var value = rawLine[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    public string GetGameDirectory(LauncherProfile profile)
    {
        var directoryName = profile == LauncherProfile.CheatClient ? "minecraft_cheat" : "minecraft_vanilla";
        var fullPath = Path.Combine(BaseStorageDirectory.Value, directoryName);
        Directory.CreateDirectory(fullPath);
        EnsureProfileDirectories(fullPath, profile);
        return fullPath;
    }

    public string GetVersionInstanceDirectory(LauncherProfile profile, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new ArgumentException("ID версии не указан.", nameof(versionId));
        }

        var profileRoot = GetGameDirectory(profile);
        var instanceName = SanitizePathSegment(versionId.Trim());
        var instancePath = Path.Combine(profileRoot, "instances", instanceName);
        Directory.CreateDirectory(instancePath);
        EnsureProfileDirectories(instancePath, profile);
        InitializeVersionInstanceDirectory(profileRoot, instancePath);
        return instancePath;
    }

    private static async Task<IReadOnlyList<ModLoaderVersionEntry>> FetchForgeVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken)
    {
        List<ModLoaderVersionEntry>? result = null;
        try
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                ForgeVersionsByMinecraftUrlTemplate,
                Uri.EscapeDataString(minecraftVersionId));

            using var document = await GetJsonDocumentWithUserAgentAsync(url, cancellationToken);

            result = new List<ModLoaderVersionEntry>();
            var ordinal = 0;
            foreach (var item in EnumerateArrayLike(document.RootElement))
            {
                if (!item.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var forgeVersion = versionElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(forgeVersion))
                {
                    continue;
                }

                var releaseTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ordinal++);
                if (item.TryGetProperty("modified", out var modifiedElement) &&
                    modifiedElement.ValueKind == JsonValueKind.String)
                {
                    var modifiedText = modifiedElement.GetString();
                    if (!string.IsNullOrWhiteSpace(modifiedText) &&
                        DateTimeOffset.TryParse(
                            modifiedText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal,
                            out var modifiedTime))
                    {
                        releaseTime = modifiedTime;
                    }
                }

                var displayName = $"Forge {forgeVersion}";
                result.Add(new ModLoaderVersionEntry(forgeVersion, displayName, releaseTime));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            result = null;
        }

        if (result is null || result.Count == 0)
        {
            result = (await FetchForgeVersionsFromPromotionsAsync(minecraftVersionId, cancellationToken)).ToList();
        }

        return result
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.ReleaseTime).First())
            .OrderByDescending(item => item.Id, OptiFineVersionComparer.Instance)
            .ThenByDescending(item => item.ReleaseTime)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ModLoaderVersionEntry>> FetchForgeVersionsFromPromotionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            return [];
        }

        using var document = await GetJsonDocumentWithUserAgentAsync(ForgePromotionsUrl, cancellationToken);
        if (!document.RootElement.TryGetProperty("promos", out var promosElement) ||
            promosElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        string? recommended = null;
        string? latest = null;
        foreach (var candidate in EnumerateForgePromotionKeys(minecraftVersionId))
        {
            if (recommended is null &&
                promosElement.TryGetProperty($"{candidate}-recommended", out var recElement) &&
                recElement.ValueKind == JsonValueKind.String)
            {
                recommended = recElement.GetString();
            }

            if (latest is null &&
                promosElement.TryGetProperty($"{candidate}-latest", out var latestElement) &&
                latestElement.ValueKind == JsonValueKind.String)
            {
                latest = latestElement.GetString();
            }

            if (!string.IsNullOrWhiteSpace(recommended) || !string.IsNullOrWhiteSpace(latest))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(recommended) && string.IsNullOrWhiteSpace(latest))
        {
            return [];
        }

        var result = new List<ModLoaderVersionEntry>();
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(recommended))
        {
            result.Add(new ModLoaderVersionEntry(
                recommended,
                $"Forge {recommended} (recommended)",
                now));
        }

        if (!string.IsNullOrWhiteSpace(latest) &&
            !string.Equals(latest, recommended, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(new ModLoaderVersionEntry(
                latest,
                $"Forge {latest} (latest)",
                now.AddSeconds(-5)));
        }

        return result;
    }

    private static IEnumerable<string> EnumerateForgePromotionKeys(string minecraftVersionId)
    {
        var trimmed = minecraftVersionId.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numericMatch = MinecraftVersionRegex.Match(trimmed);
        if (numericMatch.Success)
        {
            var numeric = numericMatch.Value;
            if (seen.Add(numeric))
            {
                yield return numeric;
            }

            var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                var majorMinor = $"{parts[0]}.{parts[1]}";
                if (seen.Add(majorMinor))
                {
                    yield return majorMinor;
                }
            }
        }

        if (seen.Add(trimmed))
        {
            yield return trimmed;
        }
    }

    private static async Task<IReadOnlyList<ModLoaderVersionEntry>> FetchFabricVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            FabricLoaderVersionsUrlTemplate,
            Uri.EscapeDataString(minecraftVersionId));

        using var document = await GetJsonDocumentWithUserAgentAsync(url, cancellationToken);

        var result = new List<ModLoaderVersionEntry>();
        var ordinal = 0;
        foreach (var item in EnumerateArrayLike(document.RootElement))
        {
            if (!item.TryGetProperty("loader", out var loaderElement) ||
                loaderElement.ValueKind != JsonValueKind.Object ||
                !loaderElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var loaderVersion = versionElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                continue;
            }

            var stable = loaderElement.TryGetProperty("stable", out var stableElement) &&
                         stableElement.ValueKind == JsonValueKind.True;
            var displayName = stable
                ? $"Fabric {loaderVersion}"
                : $"Fabric {loaderVersion} (preview)";

            var releaseTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ordinal++);
            if (!stable)
            {
                releaseTime -= TimeSpan.FromDays(3650);
            }

            result.Add(new ModLoaderVersionEntry(loaderVersion, displayName, releaseTime));
        }

        return result
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.ReleaseTime).First())
            .OrderByDescending(item => item.ReleaseTime)
            .ThenByDescending(item => item.Id, ModLoaderVersionComparer.Instance)
            .ToArray();
    }

    private static async Task<IReadOnlyList<ModLoaderVersionEntry>> FetchOptiFineVersionsAsync(
        string minecraftVersionId,
        CancellationToken cancellationToken)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            OptiFineVersionListUrlTemplate,
            Uri.EscapeDataString(minecraftVersionId));

        using var document = await GetJsonDocumentWithUserAgentAsync(url, cancellationToken);

        var result = new List<ModLoaderVersionEntry>();
        var ordinal = 0;
        foreach (var item in EnumerateArrayLike(document.RootElement))
        {
            if (!item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("patch", out var patchElement) ||
                patchElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var optifineType = typeElement.GetString()?.Trim();
            var optifinePatch = patchElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(optifineType) || string.IsNullOrWhiteSpace(optifinePatch))
            {
                continue;
            }

            var fileName = item.TryGetProperty("filename", out var fileElement) && fileElement.ValueKind == JsonValueKind.String
                ? fileElement.GetString() ?? string.Empty
                : string.Empty;
            var forgeHint = item.TryGetProperty("forge", out var forgeElement) && forgeElement.ValueKind == JsonValueKind.String
                ? forgeElement.GetString() ?? string.Empty
                : string.Empty;

            var isPreview = fileName.StartsWith("preview_", StringComparison.OrdinalIgnoreCase) ||
                            optifinePatch.StartsWith("pre", StringComparison.OrdinalIgnoreCase);

            var displayName = isPreview
                ? $"OptiFine {optifineType} {optifinePatch} (preview)"
                : $"OptiFine {optifineType} {optifinePatch}";

            if (!string.IsNullOrWhiteSpace(forgeHint) &&
                !forgeHint.Contains("N/A", StringComparison.OrdinalIgnoreCase))
            {
                displayName += $" • {forgeHint}";
            }

            var releaseTime = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ordinal++);
            if (isPreview)
            {
                releaseTime -= TimeSpan.FromDays(3650);
            }

            var id = $"{optifineType}|{optifinePatch}";
            result.Add(new ModLoaderVersionEntry(id, displayName, releaseTime));
        }

        return result
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.ReleaseTime).First())
            .OrderByDescending(item => item.ReleaseTime)
            .ThenByDescending(item => item.Id, ModLoaderVersionComparer.Instance)
            .ToArray();
    }

    private async Task<ModLoaderInstallResult> InstallForgeAsync(
        string minecraftVersionId,
        string loaderVersionId,
        string gameDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var forgeVersion = loaderVersionId.Trim();
        if (string.IsNullOrWhiteSpace(forgeVersion))
        {
            throw new ArgumentException("Версия Forge не указана.", nameof(loaderVersionId));
        }

        var installerUrl = string.Format(
            CultureInfo.InvariantCulture,
            ForgeInstallerMavenUrlTemplate,
            Uri.EscapeDataString(minecraftVersionId),
            Uri.EscapeDataString(forgeVersion));

        var forgeStage = $"Скачиваю Forge {forgeVersion}...";
        progress?.Report(new LauncherProgress(forgeStage, 0, 2));

        var installerPath = CreateLauncherTemporaryFilePath(
            $"forge-installer-{minecraftVersionId}-{forgeVersion}",
            ".jar");
        try
        {
            var downloadAttempt = await GetDownloadResponseWithFallbackAsync(installerUrl, cancellationToken);
            using (var response = downloadAttempt.Response)
            {
                await using var destination = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyToWithProgressAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    destination,
                    response.Content.Headers.ContentLength,
                    CreateStageDownloadProgressReporter(progress, forgeStage, 0, 1, 2),
                    cancellationToken);
            }

            var versionJsonText = ReadZipEntryText(installerPath, "version.json");
            if (string.IsNullOrWhiteSpace(versionJsonText))
            {
                throw new InvalidDataException("В installer Forge отсутствует файл version.json.");
            }

            var versionNode = JsonNode.Parse(versionJsonText) as JsonObject
                              ?? throw new InvalidDataException("Не удалось разобрать version.json Forge.");

            var installedVersionId = versionNode["id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(installedVersionId))
            {
                installedVersionId = $"{minecraftVersionId}-forge-{forgeVersion}";
            }

            versionNode["id"] = installedVersionId;
            versionNode["inheritsFrom"] ??= minecraftVersionId;
            versionNode["type"] = "forge";

            var versionDirectory = Path.Combine(gameDirectory, "versions", installedVersionId);
            Directory.CreateDirectory(versionDirectory);
            var versionJsonPath = Path.Combine(versionDirectory, $"{installedVersionId}.json");

            await File.WriteAllTextAsync(
                versionJsonPath,
                versionNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);

            await EnsureForgeClientLibraryAsync(
                versionNode,
                installerPath,
                gameDirectory,
                minecraftVersionId,
                progress,
                cancellationToken);

            progress?.Report(new LauncherProgress($"Forge {forgeVersion} установлен.", 2, 2));
            return new ModLoaderInstallResult(installedVersionId, versionJsonPath);
        }
        finally
        {
            try
            {
                if (File.Exists(installerPath))
                {
                    File.Delete(installerPath);
                }
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }

    private async Task<ModLoaderInstallResult> InstallFabricAsync(
        string minecraftVersionId,
        string loaderVersionId,
        string gameDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fabricLoaderVersion = loaderVersionId.Trim();
        if (string.IsNullOrWhiteSpace(fabricLoaderVersion))
        {
            throw new ArgumentException("Версия Fabric не указана.", nameof(loaderVersionId));
        }

        var profileUrl = string.Format(
            CultureInfo.InvariantCulture,
            FabricProfileUrlTemplate,
            Uri.EscapeDataString(minecraftVersionId),
            Uri.EscapeDataString(fabricLoaderVersion));

        progress?.Report(new LauncherProgress($"Скачиваю Fabric {fabricLoaderVersion}...", 0, 1));

        using var profileDocument = await GetJsonDocumentWithUserAgentAsync(profileUrl, cancellationToken);
        var profileNode = JsonNode.Parse(profileDocument.RootElement.GetRawText()) as JsonObject
                          ?? throw new InvalidDataException("Не удалось разобрать профиль Fabric.");

        var installedVersionId = profileNode["id"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(installedVersionId))
        {
            installedVersionId = $"fabric-loader-{fabricLoaderVersion}-{minecraftVersionId}";
        }

        profileNode["id"] = installedVersionId;
        profileNode["inheritsFrom"] ??= minecraftVersionId;
        profileNode["type"] = "fabric";

        var versionDirectory = Path.Combine(gameDirectory, "versions", installedVersionId);
        Directory.CreateDirectory(versionDirectory);
        var versionJsonPath = Path.Combine(versionDirectory, $"{installedVersionId}.json");

        await File.WriteAllTextAsync(
            versionJsonPath,
            profileNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        progress?.Report(new LauncherProgress($"Fabric {fabricLoaderVersion} установлен.", 1, 1));
        return new ModLoaderInstallResult(installedVersionId, versionJsonPath);
    }

    private async Task<ModLoaderInstallResult> InstallOptiFineAsync(
        string minecraftVersionId,
        string loaderVersionId,
        string gameDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptiFineLoaderId(loaderVersionId, out var optifineType, out var optifinePatch))
        {
            throw new ArgumentException(
                $"Неверный идентификатор OptiFine '{loaderVersionId}'. Ожидается формат 'TYPE|PATCH'.",
                nameof(loaderVersionId));
        }

        var coordinateVersion = $"{minecraftVersionId}_{optifineType}_{optifinePatch}";
        var optifineJarRelativePath = $"optifine/OptiFine/{coordinateVersion}/OptiFine-{coordinateVersion}.jar";
        var optifineDownloadUrl = string.Format(
            CultureInfo.InvariantCulture,
            OptiFineDownloadUrlTemplate,
            Uri.EscapeDataString(minecraftVersionId),
            Uri.EscapeDataString(optifineType),
            Uri.EscapeDataString(optifinePatch));

        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        Directory.CreateDirectory(librariesDirectory);

        var optifineInstallStage = $"Скачиваю OptiFine {optifineType} {optifinePatch}...";
        progress?.Report(new LauncherProgress(optifineInstallStage, 0, 2));
        var optifineJarPath = CombineWithRoot(librariesDirectory, optifineJarRelativePath);
        await DownloadFileIfNeededAsync(
            optifineDownloadUrl,
            optifineJarPath,
            null,
            cancellationToken,
            CreateStageDownloadProgressReporter(progress, optifineInstallStage, 0, 1, 2));

        var launchwrapperLibrary = EnsureOptiFineLaunchwrapperLibrary(
            optifineJarPath,
            librariesDirectory,
            minecraftVersionId);

        progress?.Report(new LauncherProgress("Формирую профиль OptiFine...", 1, 2));

        var baseVersion = await ResolveVersionEntryByIdAsync(minecraftVersionId, gameDirectory, cancellationToken);
        using var baseMetadata = await ResolveVersionMetadataAsync(baseVersion, gameDirectory, cancellationToken);
        var baseMetadataNode = JsonNode.Parse(baseMetadata.RootElement.GetRawText()) as JsonObject
                               ?? throw new InvalidDataException("Не удалось разобрать metadata базовой версии для OptiFine.");

        var installedVersionId = $"OptiFine {minecraftVersionId} {optifineType} {optifinePatch}";
        var metadataNode = new JsonObject
        {
            ["id"] = installedVersionId,
            ["inheritsFrom"] = minecraftVersionId,
            ["type"] = "optifine",
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["jar"] = minecraftVersionId,
            ["releaseTime"] = baseMetadataNode["releaseTime"]?.DeepClone(),
            ["time"] = baseMetadataNode["time"]?.DeepClone(),
            ["libraries"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = $"optifine:OptiFine:{coordinateVersion}",
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = optifineJarRelativePath.Replace('\\', '/'),
                            ["url"] = optifineDownloadUrl
                        }
                    }
                },
                new JsonObject
                {
                    ["name"] = launchwrapperLibrary.LibraryName,
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = launchwrapperLibrary.RelativePath.Replace('\\', '/')
                        }
                    }
                }
            }
        };

        EnsureOptiFineTweakClass(metadataNode);

        var versionDirectory = Path.Combine(gameDirectory, "versions", installedVersionId);
        Directory.CreateDirectory(versionDirectory);
        var versionJsonPath = Path.Combine(versionDirectory, $"{installedVersionId}.json");

        await File.WriteAllTextAsync(
            versionJsonPath,
            metadataNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        progress?.Report(new LauncherProgress($"OptiFine {optifineType} {optifinePatch} установлен.", 2, 2));
        return new ModLoaderInstallResult(installedVersionId, versionJsonPath);
    }

    private async Task<string> EnsureVersionJarAvailableAsync(
        string versionId,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        var normalizedVersionId = NormalizeMinecraftBaseVersionId(versionId);
        var version = await ResolveVersionEntryByIdAsync(normalizedVersionId, gameDirectory, cancellationToken);
        var versionDirectory = Path.Combine(gameDirectory, "versions", normalizedVersionId);
        Directory.CreateDirectory(versionDirectory);

        var versionJarPath = Path.Combine(versionDirectory, $"{normalizedVersionId}.jar");
        if (!TryCopyVersionJarFromKnownSources(version, normalizedVersionId, versionJarPath) &&
            !File.Exists(versionJarPath))
        {
            using var metadata = await ResolveVersionMetadataAsync(version, gameDirectory, cancellationToken);
            if (!metadata.RootElement.TryGetProperty("downloads", out var downloadsElement) ||
                !downloadsElement.TryGetProperty("client", out var clientDownload))
            {
                throw new InvalidDataException(
                    $"В metadata версии '{normalizedVersionId}' отсутствует client download.");
            }

            var downloadEntry = DownloadEntry.FromJson(clientDownload, versionJarPath, "client.jar");
            await DownloadFileIfNeededAsync(
                downloadEntry.Url,
                downloadEntry.DestinationPath,
                downloadEntry.Sha1,
                cancellationToken);
        }

        if (!File.Exists(versionJarPath))
        {
            throw new FileNotFoundException($"Не удалось подготовить jar версии {normalizedVersionId}.", versionJarPath);
        }

        return versionJarPath;
    }

    private async Task EnsureBaseVersionReadyAsync(
        string versionId,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        var normalizedVersionId = NormalizeMinecraftBaseVersionId(versionId);
        var versionDirectory = Path.Combine(gameDirectory, "versions", normalizedVersionId);
        var versionJsonPath = Path.Combine(versionDirectory, $"{normalizedVersionId}.json");
        var versionJarPath = Path.Combine(versionDirectory, $"{normalizedVersionId}.jar");
        if (File.Exists(versionJsonPath) && File.Exists(versionJarPath))
        {
            return;
        }

        var version = await ResolveVersionEntryByIdAsync(normalizedVersionId, gameDirectory, cancellationToken);
        if (!File.Exists(versionJsonPath))
        {
            using var _ = await DownloadVersionMetadataAsync(version, gameDirectory, cancellationToken);
        }

        if (!File.Exists(versionJarPath))
        {
            _ = await EnsureVersionJarAvailableAsync(normalizedVersionId, gameDirectory, cancellationToken);
        }
    }

    public async Task<OfflineSkinLaunchData?> PrepareOfflineSkinUserPropertiesAsync(
        string selectedSkinPath,
        bool isSlimModel,
        string username,
        string? accountAccessToken = null,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedSkinPath))
        {
            return null;
        }

        var fullSkinPath = Path.GetFullPath(selectedSkinPath);
        if (!File.Exists(fullSkinPath))
        {
            return null;
        }

        var preparedSkinPath = PrepareSkinFileForLaunch(fullSkinPath, isSlimModel);
        byte[]? preparedSkinBytes = null;
        try
        {
            preparedSkinBytes = await File.ReadAllBytesAsync(preparedSkinPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            preparedSkinBytes = null;
        }

        var sessionUuid = BuildOfflineUuid(username);
        var sessionUsername = username;
        var sessionAccessToken = BuildOfflineAccessToken(username);
        var hostedSkinUrl = TryGetHostedSkinUrl(preparedSkinPath, preferDirectLoopback: true);
        var fallbackSkinUrl = hostedSkinUrl ?? new Uri(preparedSkinPath, UriKind.Absolute).AbsoluteUri;
        var fallbackTextureValue = BuildTextureValueFromSkinUrl(
            fallbackSkinUrl,
            sessionUuid,
            sessionUsername,
            isSlimModel);
        var fallbackUserProperties = BuildUserPropertiesJsonFromTextureValue(
            fallbackTextureValue,
            textureSignature: null);
        if (!string.IsNullOrWhiteSpace(accountAccessToken))
        {
            VesperSkinRegistry.SaveOrUpdate(
                sessionUsername,
                sessionUuid,
                fallbackTextureValue,
                textureSignature: null,
                fallbackSkinUrl,
                accountAccessToken,
                publishedUuid: null,
                skinImageBytes: preparedSkinBytes);
        }

        var fallbackOfflineLaunchData = new OfflineSkinLaunchData(
            fallbackUserProperties,
            sessionUuid,
            null);

        try
        {
            var sharedSkinCacheEntry = await TryGenerateAndCacheSharedSkinAsync(
                preparedSkinPath,
                isSlimModel,
                sessionUsername,
                cancellationToken).ConfigureAwait(false);
            if (sharedSkinCacheEntry != null &&
                !string.IsNullOrWhiteSpace(sharedSkinCacheEntry.TextureValue))
            {
                var sharedSkinUrl = !string.IsNullOrWhiteSpace(sharedSkinCacheEntry.TextureUrl)
                    ? NormalizeTextureUrl(sharedSkinCacheEntry.TextureUrl)
                    : TryExtractTextureUrlFromTextureValue(sharedSkinCacheEntry.TextureValue);
                var sharedSkinProfileId = NormalizeMineSkinProfileId(sharedSkinCacheEntry.ProfileId);

                VesperSkinRegistry.SaveOrUpdate(
                    sessionUsername,
                    sessionUuid,
                    sharedSkinCacheEntry.TextureValue,
                    sharedSkinCacheEntry.TextureSignature,
                    sharedSkinUrl,
                    accountAccessToken,
                    sharedSkinProfileId,
                    preparedSkinBytes);

                if (!string.IsNullOrWhiteSpace(hostedSkinUrl))
                {
                    lock (VesperAuthServerLock)
                    {
                        VesperAuthServer ??= new VesperAuthHttpServer();
                        var preparedProfile = VesperAuthServer.RegisterProfile(
                            sessionUsername,
                            sessionUuid,
                            sessionAccessToken,
                            hostedSkinUrl,
                            isSlimModel);

                        return new OfflineSkinLaunchData(
                            preparedProfile.UserPropertiesJson,
                            sessionUuid,
                            preparedProfile.Session);
                    }
                }

                lock (VesperAuthServerLock)
                {
                    VesperAuthServer ??= new VesperAuthHttpServer();
                    var preparedProfile = VesperAuthServer.RegisterSignedProfile(
                        sessionUsername,
                        sessionUuid,
                        sessionAccessToken,
                        sharedSkinCacheEntry.TextureValue,
                        sharedSkinCacheEntry.TextureSignature);

                    return new OfflineSkinLaunchData(
                        preparedProfile.UserPropertiesJson,
                        sessionUuid,
                        preparedProfile.Session);
                }
            }

            if (string.IsNullOrWhiteSpace(hostedSkinUrl))
            {
                return fallbackOfflineLaunchData;
            }

            lock (VesperAuthServerLock)
            {
                VesperAuthServer ??= new VesperAuthHttpServer();
                var preparedProfile = VesperAuthServer.RegisterProfile(
                    sessionUsername,
                    sessionUuid,
                    sessionAccessToken,
                    hostedSkinUrl,
                    isSlimModel);

                return new OfflineSkinLaunchData(
                    preparedProfile.UserPropertiesJson,
                    sessionUuid,
                    preparedProfile.Session);
            }
        }
        catch
        {
            return fallbackOfflineLaunchData;
        }
    }

    private async Task EnsureForgeClientLibraryAsync(
        JsonObject versionNode,
        string installerPath,
        string gameDirectory,
        string minecraftVersionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var clientRelativePath = TryGetForgeClientLibraryRelativePath(versionNode);
        if (string.IsNullOrWhiteSpace(clientRelativePath))
        {
            clientRelativePath = TryGetForgeClientLibraryRelativePathFromInstaller(installerPath);
        }

        if (string.IsNullOrWhiteSpace(clientRelativePath))
        {
            await EnsureLegacyForgeUniversalLibraryAsync(versionNode, gameDirectory, progress, cancellationToken);
            return;
        }

        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        var clientJarPath = CombineWithRoot(librariesDirectory, clientRelativePath);
        if (File.Exists(clientJarPath))
        {
            return;
        }

        progress?.Report(new LauncherProgress("Устанавливаю Forge client jar...", 1, 2));

        if (TryExtractForgeClientFromInstaller(installerPath, clientJarPath, progress, cancellationToken))
        {
            return;
        }

        var workspaceDirectory = PrepareForgeInstallerWorkspace(gameDirectory, minecraftVersionId);
        await RunForgeInstallerWithFallbackAsync(
            installerPath,
            workspaceDirectory,
            minecraftVersionId,
            progress,
            cancellationToken);

        if (!File.Exists(clientJarPath))
        {
            var workspaceJarPath = CombineWithRoot(
                Path.Combine(workspaceDirectory, "libraries"),
                clientRelativePath);
            if (File.Exists(workspaceJarPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(clientJarPath)
                                          ?? throw new InvalidOperationException("Некорректный путь Forge client jar."));
                File.Copy(workspaceJarPath, clientJarPath, overwrite: true);
            }
        }

        if (!File.Exists(clientJarPath))
        {
            throw new InvalidOperationException(
                "Forge installer завершился без создания client jar. Проверь права доступа.");
        }

        await EnsureLegacyForgeUniversalLibraryAsync(versionNode, gameDirectory, progress, cancellationToken);
    }

    private async Task EnsureLegacyForgeUniversalLibraryAsync(
        JsonObject versionNode,
        string gameDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryGetLegacyForgeUniversalDownloadEntry(versionNode, gameDirectory, out var downloadEntry))
        {
            return;
        }

        if (File.Exists(downloadEntry.DestinationPath))
        {
            return;
        }

        progress?.Report(new LauncherProgress("Скачиваю Forge universal jar...", 1, 2));
        await DownloadFileIfNeededAsync(
            downloadEntry.Url,
            downloadEntry.DestinationPath,
            downloadEntry.Sha1,
            cancellationToken);
    }

    private async Task EnsureForgeClientLibraryFromInstallerAsync(
        string libraryName,
        string gameDirectory,
        string clientJarPath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(clientJarPath))
        {
            return;
        }

        if (!TryParseForgeCombinedVersion(libraryName, out var minecraftVersionId, out var forgeVersionId))
        {
            return;
        }

        var installerUrl = string.Format(
            CultureInfo.InvariantCulture,
            ForgeInstallerMavenUrlTemplate,
            Uri.EscapeDataString(minecraftVersionId),
            Uri.EscapeDataString(forgeVersionId));

        var installerPath = CreateLauncherTemporaryFilePath(
            $"forge-installer-{minecraftVersionId}-{forgeVersionId}",
            ".jar");

        try
        {
            var downloadAttempt = await GetDownloadResponseWithFallbackAsync(installerUrl, cancellationToken);
            using (var response = downloadAttempt.Response)
            {
                await using var destination = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(destination, cancellationToken);
            }

            if (TryExtractForgeLibraryFromInstaller(installerPath, libraryName, clientJarPath, progress, cancellationToken))
            {
                return;
            }

            if (TryExtractForgeClientFromInstaller(installerPath, clientJarPath, progress, cancellationToken))
            {
                return;
            }

            var workspaceDirectory = PrepareForgeInstallerWorkspace(gameDirectory, minecraftVersionId);
            await RunForgeInstallerWithFallbackAsync(
                installerPath,
                workspaceDirectory,
                minecraftVersionId,
                progress,
                cancellationToken);

            var workspaceJarPath = CombineWithRoot(
                Path.Combine(workspaceDirectory, "libraries"),
                BuildLibraryRelativePathFromName(libraryName));
            if (File.Exists(workspaceJarPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(clientJarPath)
                                          ?? throw new InvalidOperationException("Некорректный путь Forge client jar."));
                File.Copy(workspaceJarPath, clientJarPath, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(installerPath))
                {
                    File.Delete(installerPath);
                }
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }

    private static string? TryGetForgeClientLibraryRelativePath(JsonObject versionNode)
    {
        if (versionNode["libraries"] is not JsonArray libraries)
        {
            return null;
        }

        foreach (var entry in libraries)
        {
            if (entry is not JsonObject library)
            {
                continue;
            }

            var libraryName = library["name"]?.GetValue<string>();
            if (!IsForgeClientLibraryName(libraryName))
            {
                continue;
            }

            var artifactPath = library["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            var relativePath = string.IsNullOrWhiteSpace(artifactPath)
                ? BuildLibraryRelativePathFromName(libraryName ?? string.Empty)
                : NormalizeLibraryRelativePath(artifactPath);

            return string.IsNullOrWhiteSpace(relativePath) ? null : relativePath;
        }

        return null;
    }

    private static string? TryGetForgeClientLibraryRelativePathFromInstaller(string installerPath)
    {
        var libraryName = TryGetForgeClientLibraryNameFromInstaller(installerPath);
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return null;
        }

        var relativePath = BuildLibraryRelativePathFromName(libraryName);
        return string.IsNullOrWhiteSpace(relativePath) ? null : NormalizeLibraryRelativePath(relativePath);
    }

    private static string? TryGetForgeClientLibraryNameFromInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return null;
        }

        try
        {
            var installProfileText = ReadZipEntryText(installerPath, "install_profile.json");
            if (string.IsNullOrWhiteSpace(installProfileText))
            {
                return null;
            }

            var installProfileNode = JsonNode.Parse(installProfileText) as JsonObject;
            var clientValue = installProfileNode?["data"]?["PATCHED"]?["client"]?.GetValue<string>();
            return TryExtractLibraryNameFromInstallProfileValue(clientValue);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractLibraryNameFromInstallProfileValue(string? rawValue)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("[", StringComparison.Ordinal) &&
            value.EndsWith("]", StringComparison.Ordinal) &&
            value.Length > 2)
        {
            value = value[1..^1].Trim();
        }

        if (value.StartsWith("'", StringComparison.Ordinal) &&
            value.EndsWith("'", StringComparison.Ordinal) &&
            value.Length > 2)
        {
            value = value[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(value) || !value.Contains(':')
            ? null
            : value;
    }

    private static bool TryGetLegacyForgeUniversalDownloadEntry(
        JsonObject versionNode,
        string gameDirectory,
        out DownloadEntry downloadEntry)
    {
        downloadEntry = null!;

        if (versionNode["libraries"] is not JsonArray libraries)
        {
            return false;
        }

        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        foreach (var entry in libraries)
        {
            if (entry is not JsonObject library)
            {
                continue;
            }

            var libraryName = library["name"]?.GetValue<string>();
            if (TryCreateLegacyForgeUniversalDownloadEntry(libraryName, librariesDirectory, out downloadEntry))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateLegacyForgeUniversalDownloadEntry(
        string? libraryName,
        string librariesDirectory,
        out DownloadEntry downloadEntry)
    {
        downloadEntry = null!;

        if (string.IsNullOrWhiteSpace(libraryName) ||
            !libraryName.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = libraryName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !TryParseForgeCombinedVersion(libraryName, out var minecraftVersionId, out var forgeVersionId))
        {
            return false;
        }

        var relativePath = NormalizeLibraryRelativePath(
            $"net/minecraftforge/forge/{minecraftVersionId}-{forgeVersionId}/forge-{minecraftVersionId}-{forgeVersionId}-universal.jar");
        var downloadUrl = BuildRepositoryDownloadUrl(ForgeMavenBaseUrl, relativePath);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return false;
        }

        var destinationPath = CombineWithRoot(librariesDirectory, relativePath);
        downloadEntry = new DownloadEntry(downloadUrl, destinationPath, null, "library");
        return true;
    }

    private static async Task<string?> EnsureForgeInstallerHeadlessToolAsync(CancellationToken cancellationToken)
    {
        var toolsDirectory = Path.Combine(BaseStorageDirectory.Value, "tools", "forge-installer-headless");
        var jarPath = Path.Combine(toolsDirectory, "forge-installer-headless.jar");
        if (File.Exists(jarPath))
        {
            return jarPath;
        }

        Directory.CreateDirectory(toolsDirectory);

        try
        {
            using var document = await GetJsonDocumentWithUserAgentAsync(ForgeInstallerHeadlessReleaseApiUrl, cancellationToken);
            if (!document.RootElement.TryGetProperty("assets", out var assetsElement) ||
                assetsElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var assetName = nameElement.GetString() ?? string.Empty;
                if (!assetName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!asset.TryGetProperty("browser_download_url", out var urlElement) ||
                    urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var downloadUrl = urlElement.GetString();
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                var tempFile = jarPath + ".tmp";
                var downloadAttempt = await GetDownloadResponseWithFallbackAsync(downloadUrl, cancellationToken);
                using (var response = downloadAttempt.Response)
                {
                    await using var destination = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(destination, cancellationToken);
                }

                if (File.Exists(jarPath))
                {
                    File.Delete(jarPath);
                }

                File.Move(tempFile, jarPath);
                return jarPath;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private async Task RunForgeHeadlessInstallerAsync(
        string installerPath,
        string gameDirectory,
        string minecraftVersionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureLauncherProfilesStub(gameDirectory);

        var headlessInstallerPath = await EnsureForgeInstallerHeadlessToolAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(headlessInstallerPath) || !File.Exists(headlessInstallerPath))
        {
            throw new InvalidOperationException(
                "Не удалось скачать ForgeInstallerHeadless. Проверь интернет или установи Forge вручную.");
        }

        var baseVersion = await ResolveVersionEntryByIdAsync(minecraftVersionId, gameDirectory, cancellationToken);
        using var baseMetadata = await ResolveVersionMetadataAsync(baseVersion, gameDirectory, cancellationToken);
        var javaExecutable = await ResolveJavaExecutableAsync(
            string.Empty,
            baseMetadata.RootElement,
            minecraftVersionId,
            progress,
            cancellationToken);

        var classpath = string.Join(Path.PathSeparator, new[] { headlessInstallerPath, installerPath });
        await RunJavaMainAsync(
            javaExecutable,
            classpath,
            "me.xfl03.HeadlessInstaller",
            ["-installClient", gameDirectory],
            cancellationToken);
    }

    private async Task RunForgeInstallerWithFallbackAsync(
        string installerPath,
        string gameDirectory,
        string minecraftVersionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? cliError = null;
        try
        {
            await RunForgeInstallerCliAsync(
                installerPath,
                gameDirectory,
                minecraftVersionId,
                progress,
                cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            cliError = ex;
        }

        try
        {
            await RunForgeHeadlessInstallerAsync(
                installerPath,
                gameDirectory,
                minecraftVersionId,
                progress,
                cancellationToken);
        }
        catch (Exception headlessError) when (cliError is not null)
        {
            throw new InvalidOperationException(
                $"Не удалось установить Forge для {minecraftVersionId} ни через обычный installer, ни через headless fallback.",
                new AggregateException(cliError, headlessError));
        }
    }

    private async Task RunForgeInstallerCliAsync(
        string installerPath,
        string gameDirectory,
        string minecraftVersionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureLauncherProfilesStub(gameDirectory);

        var baseVersion = await ResolveVersionEntryByIdAsync(minecraftVersionId, gameDirectory, cancellationToken);
        using var baseMetadata = await ResolveVersionMetadataAsync(baseVersion, gameDirectory, cancellationToken);
        var javaExecutable = await ResolveJavaExecutableAsync(
            string.Empty,
            baseMetadata.RootElement,
            minecraftVersionId,
            progress,
            cancellationToken);

        await RunJavaJarAsync(
            javaExecutable,
            installerPath,
            ["--installClient", gameDirectory],
            cancellationToken);
    }

    private static bool TryParseForgeCombinedVersion(
        string libraryName,
        out string minecraftVersionId,
        out string forgeVersionId)
    {
        minecraftVersionId = string.Empty;
        forgeVersionId = string.Empty;

        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        var parts = libraryName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        var combined = parts[2];
        var separatorIndex = combined.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= combined.Length - 1)
        {
            return false;
        }

        minecraftVersionId = combined[..separatorIndex];
        forgeVersionId = combined[(separatorIndex + 1)..];
        return !string.IsNullOrWhiteSpace(minecraftVersionId) && !string.IsNullOrWhiteSpace(forgeVersionId);
    }

    private static bool TryExtractForgeLibraryFromInstaller(
        string installerPath,
        string libraryName,
        string destinationPath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installerPath) ||
            string.IsNullOrWhiteSpace(libraryName) ||
            !File.Exists(installerPath))
        {
            return false;
        }

        var relativePath = NormalizeLibraryRelativePath(BuildLibraryRelativePathFromName(libraryName));
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        try
        {
            var expectedInfo = TryReadForgeLibraryInfoFromInstaller(installerPath, libraryName);
            using var archive = ZipFile.OpenRead(installerPath);
            var entry = FindForgeInstallerEntry(
                archive,
                $"maven/{relativePath}",
                $"libraries/{relativePath}",
                relativePath);

            if (entry is null)
            {
                return false;
            }

            progress?.Report(new LauncherProgress("Р Р°СЃРїР°РєРѕРІС‹РІР°СЋ Forge library...", 1, 2));
            return ExtractForgeJarEntry(entry, destinationPath, expectedInfo, cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractForgeClientFromInstaller(
        string installerPath,
        string clientJarPath,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return false;
        }

        try
        {
            var expectedInfo = TryReadForgeClientInfoFromInstaller(installerPath);

            using var archive = ZipFile.OpenRead(installerPath);
            var lzmaEntry = FindForgeInstallerEntry(archive, "data/client.lzma", "client.lzma");
            if (lzmaEntry != null)
            {
                progress?.Report(new LauncherProgress("Распаковываю Forge client jar...", 1, 2));
                return ExtractForgeLzmaEntry(lzmaEntry, clientJarPath, expectedInfo, cancellationToken);
            }

            var jarEntry = FindForgeInstallerEntry(archive, "data/client.jar", "client.jar");
            if (jarEntry != null)
            {
                progress?.Report(new LauncherProgress("Распаковываю Forge client jar...", 1, 2));
                return ExtractForgeJarEntry(jarEntry, clientJarPath, expectedInfo, cancellationToken);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static ZipArchiveEntry? FindForgeInstallerEntry(ZipArchive archive, params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            var entry = archive.GetEntry(name);
            if (entry != null)
            {
                return entry;
            }
        }

        foreach (var entry in archive.Entries)
        {
            foreach (var name in preferredNames)
            {
                if (entry.FullName.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static bool ExtractForgeJarEntry(
        ZipArchiveEntry entry,
        string destinationPath,
        ForgeClientInfo expectedInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
                                   ?? throw new InvalidOperationException("Некорректный путь Forge client jar.");
        Directory.CreateDirectory(destinationDirectory);

        var tempFile = destinationPath + ".tmp";
        try
        {
            using var entryStream = entry.Open();
            using var destination = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(destination);

            if (!ValidateForgeClientHash(tempFile, expectedInfo))
            {
                File.Delete(tempFile);
                return false;
            }

            ReplaceFile(tempFile, destinationPath);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // ignore cleanup failures
            }

            return false;
        }
    }

    private static bool ExtractForgeLzmaEntry(
        ZipArchiveEntry entry,
        string destinationPath,
        ForgeClientInfo expectedInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
                                   ?? throw new InvalidOperationException("Некорректный путь Forge client jar.");
        Directory.CreateDirectory(destinationDirectory);

        var tempFile = destinationPath + ".tmp";
        try
        {
            using var entryStream = entry.Open();
            using var destination = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
            LzmaHelper.DecodeLzmaStream(entryStream, destination, expectedInfo.Size);

            if (!ValidateForgeClientHash(tempFile, expectedInfo))
            {
                File.Delete(tempFile);
                return false;
            }

            ReplaceFile(tempFile, destinationPath);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // ignore cleanup failures
            }

            return false;
        }
    }

    private readonly record struct ForgeClientInfo(long? Size, string? Sha1);

    private static ForgeClientInfo TryReadForgeLibraryInfoFromInstaller(string installerPath, string targetLibraryName)
    {
        if (string.IsNullOrWhiteSpace(installerPath) ||
            string.IsNullOrWhiteSpace(targetLibraryName) ||
            !File.Exists(installerPath))
        {
            return default;
        }

        try
        {
            var versionJsonText = ReadZipEntryText(installerPath, "version.json");
            if (string.IsNullOrWhiteSpace(versionJsonText))
            {
                return default;
            }

            var versionNode = JsonNode.Parse(versionJsonText) as JsonObject;
            if (versionNode?["libraries"] is not JsonArray libraries)
            {
                return default;
            }

            foreach (var entry in libraries)
            {
                if (entry is not JsonObject library)
                {
                    continue;
                }

                var libraryName = library["name"]?.GetValue<string>();
                if (!string.Equals(libraryName, targetLibraryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return ReadForgeLibraryInfo(library);
            }
        }
        catch
        {
            return default;
        }

        return default;
    }

    private static ForgeClientInfo TryReadForgeClientInfoFromInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return default;
        }

        try
        {
            var versionJsonText = ReadZipEntryText(installerPath, "version.json");
            if (string.IsNullOrWhiteSpace(versionJsonText))
            {
                return default;
            }

            var versionNode = JsonNode.Parse(versionJsonText) as JsonObject;
            if (versionNode?["libraries"] is not JsonArray libraries)
            {
                return default;
            }

            foreach (var entry in libraries)
            {
                if (entry is not JsonObject library)
                {
                    continue;
                }

                var libraryName = library["name"]?.GetValue<string>();
                if (!IsForgeClientLibraryName(libraryName))
                {
                    continue;
                }

                return ReadForgeLibraryInfo(library);
            }
        }
        catch
        {
            return default;
        }

        return default;
    }

    private static ForgeClientInfo ReadForgeLibraryInfo(JsonObject library)
    {
        long? size = null;
        var sizeNode = library["downloads"]?["artifact"]?["size"];
        if (sizeNode is JsonValue sizeValue && sizeValue.TryGetValue<long>(out var parsedSize) && parsedSize > 0)
        {
            size = parsedSize;
        }

        string? sha1 = null;
        var sha1Node = library["downloads"]?["artifact"]?["sha1"];
        if (sha1Node is JsonValue sha1Value && sha1Value.TryGetValue<string>(out var parsedSha1) &&
            !string.IsNullOrWhiteSpace(parsedSha1))
        {
            sha1 = parsedSha1.Trim();
        }

        return new ForgeClientInfo(size, sha1);
    }

    private static bool ValidateForgeClientHash(string filePath, ForgeClientInfo expectedInfo)
    {
        if (string.IsNullOrWhiteSpace(expectedInfo.Sha1))
        {
            return true;
        }

        try
        {
            var actual = ComputeSha1(filePath);
            return actual.Equals(expectedInfo.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ReplaceFile(string tempFile, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempFile, destinationPath);
    }

    private static void EnsureLauncherProfilesStub(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return;
        }

        var profilesPath = Path.Combine(gameDirectory, "launcher_profiles.json");
        if (File.Exists(profilesPath))
        {
            return;
        }

        var profileName = "VesperLauncher";
        var stub = new JsonObject
        {
            ["profiles"] = new JsonObject
            {
                [profileName] = new JsonObject
                {
                    ["name"] = profileName,
                    ["type"] = "custom"
                }
            },
            ["settings"] = new JsonObject(),
            ["selectedProfile"] = profileName,
            ["clientToken"] = Guid.NewGuid().ToString("N"),
            ["launcherVersion"] = new JsonObject
            {
                ["name"] = "VesperLauncher",
                ["format"] = 2
            },
            ["version"] = 2
        };

        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(
            profilesPath,
            stub.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static string PrepareForgeInstallerWorkspace(string sourceGameDirectory, string minecraftVersionId)
    {
        var workspaceRoot = ResolveForgeInstallerWorkspaceRoot();
        var workspaceGameDirectory = Path.Combine(workspaceRoot, "minecraft");

        Directory.CreateDirectory(workspaceGameDirectory);
        Directory.CreateDirectory(Path.Combine(workspaceGameDirectory, "versions"));
        Directory.CreateDirectory(Path.Combine(workspaceGameDirectory, "libraries"));

        EnsureLauncherProfilesStub(workspaceGameDirectory);

        var sourceVersionDirectory = Path.Combine(sourceGameDirectory, "versions", minecraftVersionId);
        var sourceJsonPath = Path.Combine(sourceVersionDirectory, $"{minecraftVersionId}.json");
        var sourceJarPath = Path.Combine(sourceVersionDirectory, $"{minecraftVersionId}.jar");
        if (!File.Exists(sourceJsonPath) || !File.Exists(sourceJarPath))
        {
            throw new InvalidOperationException(
                $"Не найдены файлы базовой версии {minecraftVersionId}. Сначала запусти эту версию без Forge.");
        }

        var targetVersionDirectory = Path.Combine(workspaceGameDirectory, "versions", minecraftVersionId);
        Directory.CreateDirectory(targetVersionDirectory);

        File.Copy(sourceJsonPath, Path.Combine(targetVersionDirectory, $"{minecraftVersionId}.json"), overwrite: true);
        File.Copy(sourceJarPath, Path.Combine(targetVersionDirectory, $"{minecraftVersionId}.jar"), overwrite: true);

        return workspaceGameDirectory;
    }

    private static string ResolveForgeInstallerWorkspaceRoot()
    {
        var candidates = new List<string>();
        var platformCacheDirectory = PlatformPaths.GetLauncherCacheDirectory();
        if (!string.IsNullOrWhiteSpace(platformCacheDirectory))
        {
            candidates.Add(Path.Combine(platformCacheDirectory, "forge-installer"));
        }

        try
        {
            candidates.Add(ResolveLauncherTemporaryDirectory("forge-installer-workspace"));
        }
        catch
        {
            // try legacy paths below
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            candidates.Add(Path.Combine(programData, "VesperLauncher", "forge-installer"));
        }

        var baseRoot = Path.GetPathRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(baseRoot))
        {
            candidates.Add(Path.Combine(baseRoot, "VesperLauncher", "forge-installer"));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "VesperLauncher", "forge-installer"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // try next candidate
            }
        }

        return candidates[^1];
    }

    private static async Task<string> EnsureAuthlibInjectorAsync(CancellationToken cancellationToken)
    {
        var authlibDirectory = Path.Combine(LauncherDataPaths.GetPreferredDataDirectory(), "authlib-injector");
        Directory.CreateDirectory(authlibDirectory);

        await AuthlibInjectorInstallLock.WaitAsync(cancellationToken);
        try
        {
            var localFallbackJar = Directory.EnumerateFiles(authlibDirectory, "authlib-injector-*.jar", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            AuthlibInjectorRelease? release = null;
            try
            {
                release = await FetchLatestAuthlibInjectorReleaseAsync(cancellationToken);
            }
            catch when (!string.IsNullOrWhiteSpace(localFallbackJar) && File.Exists(localFallbackJar))
            {
                return localFallbackJar!;
            }

            if (release is null)
            {
                if (!string.IsNullOrWhiteSpace(localFallbackJar) && File.Exists(localFallbackJar))
                {
                    return localFallbackJar!;
                }

                throw new InvalidOperationException("Не удалось получить authlib-injector.");
            }

            var destinationPath = Path.Combine(authlibDirectory, release.FileName);
            await DownloadFileWithSha256IfNeededAsync(
                release.DownloadUrl,
                destinationPath,
                release.Sha256,
                cancellationToken);

            return destinationPath;
        }
        finally
        {
            AuthlibInjectorInstallLock.Release();
        }
    }

    private static async Task<AuthlibInjectorRelease?> FetchLatestAuthlibInjectorReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthlibInjectorReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("VesperLauncher/1.0");

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var assetElement in assetsElement.EnumerateArray())
        {
            var fileName = assetElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(fileName) ||
                string.IsNullOrWhiteSpace(downloadUrl) ||
                !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var digest = assetElement.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(digest) &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                digest = digest["sha256:".Length..];
            }

            return new AuthlibInjectorRelease(fileName!, downloadUrl!, digest);
        }

        return null;
    }

    public async Task<LaunchResult> DownloadAndLaunchAsync(
        LaunchOptions options,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowAutomaticRepair = true)
    {
        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new ArgumentException("РЈРєР°Р¶Рё РЅРёРє РїРµСЂРµРґ Р·Р°РїСѓСЃРєРѕРј.", nameof(options));
        }

        if (options.MemoryMb < 1024)
        {
            throw new ArgumentException("РњРёРЅРёРјР°Р»СЊРЅР°СЏ РїР°РјСЏС‚СЊ РґР»СЏ Р·Р°РїСѓСЃРєР°: 1024 MB.", nameof(options));
        }

        var gameDirectory = GetGameDirectory(options.Profile);
        Directory.CreateDirectory(gameDirectory);
        EnsureProfileDirectories(gameDirectory, options.Profile, options.MinecraftLanguageCode);

        progress?.Report(new LauncherProgress("Скачиваю metadata версии...", 0, 1));
        using var versionMetadata = await ResolveVersionMetadataAsync(options.Version, gameDirectory, cancellationToken);

        var root = versionMetadata.RootElement;
        var resolvedVersionId = root.GetProperty("id").GetString() ?? options.Version.Id;
        var rawBaseJarVersionId = root.TryGetProperty("jar", out var jarElement) &&
                                  jarElement.ValueKind == JsonValueKind.String
            ? jarElement.GetString()?.Trim()
            : null;
        var baseMinecraftVersionId = !string.IsNullOrWhiteSpace(options.Version.BaseVersionId)
            ? NormalizeMinecraftBaseVersionId(options.Version.BaseVersionId)
            : TryDetermineBaseMinecraftVersionId(resolvedVersionId, root);
        var baseJarVersionId = NormalizeBaseJarVersionReference(rawBaseJarVersionId, baseMinecraftVersionId);
        var instanceDirectory = GetVersionInstanceDirectory(options.Profile, resolvedVersionId);
        var versionDirectory = Path.Combine(gameDirectory, "versions", resolvedVersionId);
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        var assetsDirectory = Path.Combine(gameDirectory, "assets");
        var javaTemporaryDirectory = ResolveLauncherJavaTemporaryDirectory(resolvedVersionId);

        Directory.CreateDirectory(versionDirectory);
        Directory.CreateDirectory(librariesDirectory);
        Directory.CreateDirectory(Path.Combine(assetsDirectory, "indexes"));
        Directory.CreateDirectory(Path.Combine(assetsDirectory, "objects"));
        EnsureSufficientFreeSpace(instanceDirectory, 512L * 1024 * 1024, $"запуска версии {resolvedVersionId}");
        EnsureSufficientFreeSpace(javaTemporaryDirectory, 512L * 1024 * 1024, $"временных файлов Java для {resolvedVersionId}");

        await EnsureForgeClientLibraryForLaunchAsync(
            root,
            gameDirectory,
            progress,
            cancellationToken);

        await EnsureFabricApiForLaunchAsync(
            root,
            resolvedVersionId,
            baseMinecraftVersionId,
            instanceDirectory,
            progress,
            cancellationToken);

        var downloadEntries = new List<DownloadEntry>();
        var classpathEntries = new List<string>();
        var nativeJars = new List<string>();
        string? optiFineJarPath = null;
        var downloadedSomething = false;

        var versionJarPath = Path.Combine(versionDirectory, $"{resolvedVersionId}.jar");
        var copiedBundledJar = TryCopyVersionJarFromKnownSources(options.Version, resolvedVersionId, versionJarPath);
        if (!copiedBundledJar && !string.IsNullOrWhiteSpace(baseJarVersionId))
        {
            copiedBundledJar = TryCopyVersionJarFromKnownSources(options.Version, baseJarVersionId!, versionJarPath);
        }

        if (!copiedBundledJar)
        {
            if (root.TryGetProperty("downloads", out var downloadsElement) &&
                downloadsElement.TryGetProperty("client", out var clientDownload))
            {
                downloadEntries.Add(DownloadEntry.FromJson(clientDownload, versionJarPath, "client.jar"));
            }
            else
            {
                throw new InvalidDataException(
                    $"Р’ metadata РІРµСЂСЃРёРё '{resolvedVersionId}' РѕС‚СЃСѓС‚СЃС‚РІСѓРµС‚ client download Рё РЅРµ РЅР°Р№РґРµРЅ Р»РѕРєР°Р»СЊРЅС‹Р№ jar.");
            }
        }

        var skinBridgePlan = await ResolveVesperSkinBridgePlanAsync(
            librariesDirectory,
            resolvedVersionId,
            options,
            root,
            cancellationToken);
        PrependCompatibilityLibraries(classpathEntries, librariesDirectory, resolvedVersionId, options, skinBridgePlan);

        if (root.TryGetProperty("libraries", out var librariesElement) && librariesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in librariesElement.EnumerateArray())
            {
                if (!EvaluateRules(library))
                {
                    continue;
                }

                var libraryName = library.TryGetProperty("name", out var libraryNameElement) &&
                                  libraryNameElement.ValueKind == JsonValueKind.String
                    ? libraryNameElement.GetString()
                    : null;
                var downloadOnly = library.TryGetProperty("downloadOnly", out var downloadOnlyElement) &&
                                   downloadOnlyElement.ValueKind == JsonValueKind.True;
                var repositoryUrl = ResolveLibraryRepositoryUrl(library);
                repositoryUrl = ResolveLibraryRepositoryUrlFallback(repositoryUrl, libraryName);

                if (IsOptiFineLaunchwrapperLibraryName(libraryName))
                {
                    if (!string.IsNullOrWhiteSpace(optiFineJarPath) && File.Exists(optiFineJarPath))
                    {
                        var launchwrapperLibrary = EnsureOptiFineLaunchwrapperLibrary(
                            optiFineJarPath,
                            librariesDirectory,
                            resolvedVersionId,
                            options.Version.SourceGameDirectory);
                        ReplaceOptiFineLaunchwrapperEntries(classpathEntries, launchwrapperLibrary.AbsolutePath);
                    }
                    else if (TryCreateExpectedOptiFineLaunchwrapperLibrary(
                                 libraryName,
                                 librariesDirectory,
                                 out var expectedLaunchwrapperLibrary))
                    {
                        ReplaceOptiFineLaunchwrapperEntries(classpathEntries, expectedLaunchwrapperLibrary.AbsolutePath);
                    }

                    continue;
                }

                if (!library.TryGetProperty("downloads", out var libraryDownloads))
                {
                    var fallbackRelativePath = ResolveLibraryPathFromName(library);
                    if (string.IsNullOrWhiteSpace(fallbackRelativePath))
                    {
                        continue;
                    }

                    var fallbackArtifactPath = CombineWithRoot(librariesDirectory, fallbackRelativePath);
                    TryCopyLibraryFromKnownSources(resolvedVersionId, fallbackRelativePath, fallbackArtifactPath, options.Version.SourceGameDirectory);

                    if (!File.Exists(fallbackArtifactPath))
                    {
                        var fallbackUrl = BuildRepositoryDownloadUrl(repositoryUrl, fallbackRelativePath);
                        if (!string.IsNullOrWhiteSpace(fallbackUrl))
                        {
                            downloadEntries.Add(new DownloadEntry(fallbackUrl, fallbackArtifactPath, null, "library"));
                        }
                    }

                    if (!downloadOnly)
                    {
                        classpathEntries.Add(fallbackArtifactPath);
                    }

                    if (IsOptiFineLibraryName(libraryName))
                    {
                        optiFineJarPath = fallbackArtifactPath;
                    }
                    continue;
                }

                if (libraryDownloads.TryGetProperty("artifact", out var artifact))
                {
                    var relativePath = ResolveLibraryRelativePath(library, artifact);
                    if (!string.IsNullOrWhiteSpace(relativePath))
                    {
                        var artifactPath = CombineWithRoot(librariesDirectory, relativePath);
                        TryCopyLibraryFromKnownSources(resolvedVersionId, relativePath, artifactPath, options.Version.SourceGameDirectory);
                        var isForgeInstallerBundledLibrary = IsForgeInstallerBundledLibraryName(libraryName);
                        var artifactUrl = artifact.TryGetProperty("url", out var artifactUrlElement) &&
                                          artifactUrlElement.ValueKind == JsonValueKind.String
                            ? artifactUrlElement.GetString()
                            : null;
                        var artifactSha1 = artifact.TryGetProperty("sha1", out var artifactSha1Element) &&
                                           artifactSha1Element.ValueKind == JsonValueKind.String
                            ? artifactSha1Element.GetString()
                            : null;

                        if (isForgeInstallerBundledLibrary && string.IsNullOrWhiteSpace(artifactUrl))
                        {
                            await DeleteFileIfSha1MismatchAsync(artifactPath, artifactSha1, cancellationToken);
                        }

                        if (!File.Exists(artifactPath))
                        {
                            if (isForgeInstallerBundledLibrary && string.IsNullOrWhiteSpace(artifactUrl))
                            {
                                if (!string.IsNullOrWhiteSpace(libraryName))
                                {
                                    await EnsureForgeClientLibraryFromInstallerAsync(
                                        libraryName,
                                        gameDirectory,
                                        artifactPath,
                                        progress,
                                        cancellationToken);
                                }

                                if (!File.Exists(artifactPath))
                                {
                                    throw new InvalidOperationException(
                                        "Forge client jar не найден. Переустанови Forge через лаунчер.");
                                }
                            }
                            else
                            {
                                downloadEntries.Add(DownloadEntry.FromJson(
                                    artifact,
                                    artifactPath,
                                    "library",
                                    repositoryUrl,
                                    relativePath));
                            }
                        }

                        if (!downloadOnly)
                        {
                            classpathEntries.Add(artifactPath);
                        }

                        if (TryCreateLegacyForgeUniversalDownloadEntry(
                                libraryName,
                                librariesDirectory,
                                out var legacyForgeUniversalDownload) &&
                            !File.Exists(legacyForgeUniversalDownload.DestinationPath))
                        {
                            downloadEntries.Add(legacyForgeUniversalDownload);
                        }

                        if (IsOptiFineLibraryName(libraryName))
                        {
                            optiFineJarPath = artifactPath;
                        }
                    }
                }

                if (library.TryGetProperty("natives", out var nativesElement) &&
                    libraryDownloads.TryGetProperty("classifiers", out var classifiersElement) &&
                    classifiersElement.ValueKind == JsonValueKind.Object)
                {
                    var classifierKey = ResolveWindowsNativeClassifierKey(nativesElement);
                    if (!string.IsNullOrWhiteSpace(classifierKey) &&
                        classifiersElement.TryGetProperty(classifierKey, out var nativeDownload))
                    {
                        var nativeRelativePath = ResolveRelativeDownloadPath(nativeDownload);
                        if (!string.IsNullOrWhiteSpace(nativeRelativePath))
                        {
                            var nativePath = CombineWithRoot(librariesDirectory, nativeRelativePath);
                            TryCopyLibraryFromKnownSources(resolvedVersionId, nativeRelativePath, nativePath, options.Version.SourceGameDirectory);

                            if (!File.Exists(nativePath))
                            {
                                downloadEntries.Add(DownloadEntry.FromJson(
                                    nativeDownload,
                                    nativePath,
                                    "native",
                                    repositoryUrl,
                                    nativeRelativePath));
                            }

                            nativeJars.Add(nativePath);
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(optiFineJarPath) &&
            File.Exists(optiFineJarPath))
        {
            var launchwrapperLibrary = EnsureOptiFineLaunchwrapperLibrary(
                optiFineJarPath,
                librariesDirectory,
                resolvedVersionId,
                options.Version.SourceGameDirectory);
            ReplaceOptiFineLaunchwrapperEntries(classpathEntries, launchwrapperLibrary.AbsolutePath);
        }

        if (!string.IsNullOrWhiteSpace(baseJarVersionId) &&
            !string.Equals(baseJarVersionId, resolvedVersionId, StringComparison.OrdinalIgnoreCase))
        {
            await AppendNativeLibraryEntriesFromVersionAsync(
                baseJarVersionId!,
                gameDirectory,
                downloadEntries,
                nativeJars,
                librariesDirectory,
                cancellationToken);
        }

        classpathEntries.Add(versionJarPath);

        if (options.Profile == LauncherProfile.CheatClient)
        {
            var cheatLibsDirectory = Path.Combine(gameDirectory, "cheat-libs");
            foreach (var customJar in Directory.EnumerateFiles(cheatLibsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
            {
                classpathEntries.Add(customJar);
            }
        }

        var assetIndexElement = root.GetProperty("assetIndex");
        var assetIndexId = assetIndexElement.GetProperty("id").GetString() ?? resolvedVersionId;
        var assetIndexPath = Path.Combine(assetsDirectory, "indexes", $"{assetIndexId}.json");

        var assetIndexDownload = DownloadEntry.FromJson(assetIndexElement, assetIndexPath, "asset index");
        downloadedSomething |= await DownloadFileIfNeededAsync(
            assetIndexDownload.Url,
            assetIndexDownload.DestinationPath,
            assetIndexDownload.Sha1,
            cancellationToken);

        var assetDownloads = BuildAssetDownloadEntries(assetIndexPath, assetsDirectory);
        downloadEntries.AddRange(assetDownloads);

        var total = DeduplicateDownloadEntries(downloadEntries).Count;
        downloadedSomething |= await DownloadEntriesAsync(downloadEntries, progress, cancellationToken);

        string? resolvedJavaExecutable = null;
        if (!string.IsNullOrWhiteSpace(optiFineJarPath) &&
            File.Exists(optiFineJarPath))
        {
            if (IsOptiFinePatchArchive(optiFineJarPath))
            {
                resolvedJavaExecutable = await ResolveJavaExecutableAsync(
                    options.JavaExecutable,
                    root,
                    resolvedVersionId,
                    progress,
                    cancellationToken);

                await EnsurePatchedOptiFineLibraryAsync(
                    optiFineJarPath,
                    versionJarPath,
                    resolvedJavaExecutable,
                    progress,
                    cancellationToken);
            }

            var launchwrapperLibrary = EnsureOptiFineLaunchwrapperLibrary(
                optiFineJarPath,
                librariesDirectory,
                resolvedVersionId,
                options.Version.SourceGameDirectory);
            ReplaceOptiFineLaunchwrapperEntries(classpathEntries, launchwrapperLibrary.AbsolutePath);
        }

        var nativesDirectory = PrepareNativesDirectory(gameDirectory, resolvedVersionId);
        TryCopyBundledNatives(resolvedVersionId, nativesDirectory, options.Version);
        foreach (var nativeJar in nativeJars.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ExtractNatives(nativeJar, nativesDirectory);
        }

        var classpath = string.Join(
            Path.PathSeparator,
            classpathEntries.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists));

        if (string.IsNullOrWhiteSpace(classpath))
        {
            throw new InvalidOperationException("Classpath пустой. Не удалось подготовить библиотеки для запуска.");
        }

        var replacements = BuildLaunchReplacements(
            options,
            root,
            instanceDirectory,
            assetsDirectory,
            assetIndexId,
            classpath,
            nativesDirectory,
            resolvedVersionId,
            librariesDirectory);

        if (options.VesperAuthSession != null && !ShouldUseLegacyRuntimeCompatibility(resolvedVersionId))
        {
            TryClearSkinAssetCache(assetsDirectory);
        }

        EnsureSavedServerBypassRoutes(instanceDirectory);

        string? authlibInjectorPath = null;
        if (options.VesperAuthSession != null)
        {
            try
            {
                progress?.Report(new LauncherProgress("Скачиваю authlib-injector...", total, total));
                authlibInjectorPath = await EnsureAuthlibInjectorAsync(cancellationToken);
            }
            catch
            {
                authlibInjectorPath = null;
            }
        }

        resolvedJavaExecutable ??= await ResolveJavaExecutableAsync(
            options.JavaExecutable,
            root,
            resolvedVersionId,
            progress,
            cancellationToken);

        var resolvedJavaMajorVersion = GetJavaMajorVersion(resolvedJavaExecutable);
        var javaArguments = BuildJvmArguments(
            root,
            options,
            replacements,
            resolvedJavaMajorVersion,
            classpath,
            nativesDirectory,
            instanceDirectory,
            javaTemporaryDirectory,
            authlibInjectorPath,
            skinBridgePlan);
        var gameArguments = BuildGameArguments(root, options, replacements);
        var mainClass = root.GetProperty("mainClass").GetString();

        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new InvalidDataException("Р’ metadata РІРµСЂСЃРёРё РѕС‚СЃСѓС‚СЃС‚РІСѓРµС‚ mainClass.");
        }

        var launchJavaExecutable = ResolveJavaProbeExecutable(resolvedJavaExecutable);
        var lastJavaStdOutPath = Path.Combine(AppContext.BaseDirectory, "_last_java_stdout.log");
        var lastJavaStdErrPath = Path.Combine(AppContext.BaseDirectory, "_last_java_stderr.log");
        TryWriteJavaLaunchOutput(lastJavaStdOutPath, string.Empty);
        TryWriteJavaLaunchOutput(lastJavaStdErrPath, string.Empty);

        var processInfo = new ProcessStartInfo
        {
            FileName = launchJavaExecutable,
            WorkingDirectory = instanceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ApplyLauncherTempEnvironment(processInfo, javaTemporaryDirectory);

        var fullArgs = new List<string>(javaArguments);
        fullArgs.Add(mainClass);
        fullArgs.AddRange(gameArguments);

        // Save command for debugging
        try
        {
            WriteLaunchDiagnostics(
                instanceDirectory,
                gameDirectory,
                resolvedVersionId,
                options,
                javaTemporaryDirectory,
                authlibInjectorPath,
                skinBridgePlan);
            var logText = $"\"{processInfo.FileName}\" {string.Join(" ", fullArgs.Select(a => $"\"{a}\""))}";
            File.WriteAllText(Path.Combine(instanceDirectory, "launch_command.txt"), logText);
            File.WriteAllText(Path.Combine(gameDirectory, "launch_command.txt"), logText);
        }
        catch { }

        foreach (var arg in fullArgs)
        {
            processInfo.ArgumentList.Add(arg);
        }

        progress?.Report(new LauncherProgress("Запуск Minecraft...", total, total));
        var process = Process.Start(processInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Не удалось запустить процесс Java.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = PersistJavaLaunchOutputAsync(stdOutTask, stdErrTask, lastJavaStdOutPath, lastJavaStdErrPath);

        var exitTask = process.WaitForExitAsync(cancellationToken);
        var launchDelayTask = Task.Delay(4000, cancellationToken);
        if (await Task.WhenAny(exitTask, launchDelayTask) == exitTask)
        {
            await exitTask;
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            TryWriteJavaLaunchOutput(lastJavaStdOutPath, stdOut);
            TryWriteJavaLaunchOutput(lastJavaStdErrPath, stdErr);

            var latestLogPath = Path.Combine(instanceDirectory, "logs", "latest.log");
            var failureAnalysis = AnalyzeImmediateLaunchFailure(
                resolvedVersionId,
                latestLogPath,
                lastJavaStdErrPath,
                stdErr,
                stdOut);

            if (allowAutomaticRepair &&
                process.ExitCode != 0 &&
                failureAnalysis.ShouldAttemptAutomaticRepair &&
                TryRepairImmediateLaunchFailure(
                    gameDirectory,
                    resolvedVersionId,
                    versionDirectory,
                    instanceDirectory,
                    resolvedJavaExecutable,
                    failureAnalysis))
            {
                progress?.Report(new LauncherProgress(
                    failureAnalysis.RepairStageText ?? "Восстанавливаю поврежденную установку...",
                    total,
                    total));

                return await DownloadAndLaunchAsync(
                    options,
                    progress,
                    cancellationToken,
                    allowAutomaticRepair: false);
            }

            var logHint = File.Exists(latestLogPath)
                ? $" Подробности: {latestLogPath}"
                : string.Empty;
            var javaLogHint = File.Exists(lastJavaStdErrPath)
                ? $" Java stderr: {lastJavaStdErrPath}"
                : string.Empty;
            var reasonHint = string.IsNullOrWhiteSpace(failureAnalysis.UserMessage)
                ? string.Empty
                : $"{Environment.NewLine}{Environment.NewLine}Причина: {failureAnalysis.UserMessage}";
            var diagnosticHint = string.IsNullOrWhiteSpace(failureAnalysis.DiagnosticSnippet)
                ? string.Empty
                : $"{Environment.NewLine}{Environment.NewLine}{failureAnalysis.DiagnosticSnippet}";
            throw new InvalidOperationException(
                $"Minecraft завершился сразу после запуска. Код: {process.ExitCode}.{reasonHint}{logHint}{javaLogHint}{diagnosticHint}");
        }

        return new LaunchResult(instanceDirectory, process.Id);
    }

    private async Task<JsonDocument> ResolveVersionMetadataAsync(
        MinecraftVersionEntry version,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        using var metadata = await DownloadVersionMetadataAsync(version, gameDirectory, cancellationToken);
        var versionNode = JsonNode.Parse(metadata.RootElement.GetRawText()) as JsonObject
                          ?? throw new InvalidDataException($"Не удалось разобрать metadata версии '{version.Id}'.");

        var resolutionPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            version.Id
        };
        var resolvedNode = await ResolveVersionMetadataNodeAsync(versionNode, gameDirectory, resolutionPath, cancellationToken);
        resolvedNode.Remove("inheritsFrom");

        return JsonDocument.Parse(resolvedNode.ToJsonString());
    }

    private async Task<JsonObject> ResolveVersionMetadataNodeAsync(
        JsonObject node,
        string gameDirectory,
        HashSet<string> resolutionPath,
        CancellationToken cancellationToken)
    {
        if (!TryGetStringNodeValue(node["inheritsFrom"], out var inheritsFrom) ||
            string.IsNullOrWhiteSpace(inheritsFrom))
        {
            return node;
        }

        if (!resolutionPath.Add(inheritsFrom))
        {
            throw new InvalidDataException(
                $"Обнаружена циклическая зависимость inheritsFrom: {string.Join(" -> ", resolutionPath)} -> {inheritsFrom}");
        }

        try
        {
            var parentVersion = await ResolveVersionEntryByIdAsync(inheritsFrom, gameDirectory, cancellationToken);
            using var parentMetadata = await DownloadVersionMetadataAsync(parentVersion, gameDirectory, cancellationToken);
            var parentNode = JsonNode.Parse(parentMetadata.RootElement.GetRawText()) as JsonObject
                             ?? throw new InvalidDataException($"Не удалось разобрать metadata родительской версии '{inheritsFrom}'.");

            var resolvedParentNode = await ResolveVersionMetadataNodeAsync(parentNode, gameDirectory, resolutionPath, cancellationToken);
            var merged = MergeVersionMetadataNodes(resolvedParentNode, node);
            merged.Remove("inheritsFrom");
            return merged;
        }
        finally
        {
            resolutionPath.Remove(inheritsFrom);
        }
    }

    private async Task AppendNativeLibraryEntriesFromVersionAsync(
        string versionId,
        string gameDirectory,
        List<DownloadEntry> downloadEntries,
        List<string> nativeJars,
        string librariesDirectory,
        CancellationToken cancellationToken)
    {
        var baseVersion = await ResolveVersionEntryByIdAsync(versionId, gameDirectory, cancellationToken);
        using var baseMetadata = await ResolveVersionMetadataAsync(baseVersion, gameDirectory, cancellationToken);

        AppendNativeLibraryEntries(
            baseMetadata.RootElement,
            downloadEntries,
            nativeJars,
            librariesDirectory,
            versionId,
            baseVersion.SourceGameDirectory);
    }

    private static void AppendNativeLibraryEntries(
        JsonElement versionRoot,
        List<DownloadEntry> downloadEntries,
        List<string> nativeJars,
        string librariesDirectory,
        string versionId,
        string? sourceGameDirectory)
    {
        if (!versionRoot.TryGetProperty("libraries", out var librariesElement) ||
            librariesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var library in librariesElement.EnumerateArray())
        {
            if (!EvaluateRules(library) ||
                !library.TryGetProperty("downloads", out var libraryDownloads) ||
                !library.TryGetProperty("natives", out var nativesElement) ||
                !libraryDownloads.TryGetProperty("classifiers", out var classifiersElement) ||
                classifiersElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var classifierKey = ResolveWindowsNativeClassifierKey(nativesElement);
            if (string.IsNullOrWhiteSpace(classifierKey) ||
                !classifiersElement.TryGetProperty(classifierKey, out var nativeDownload))
            {
                continue;
            }

            var nativeRelativePath = ResolveRelativeDownloadPath(nativeDownload);
            if (string.IsNullOrWhiteSpace(nativeRelativePath))
            {
                continue;
            }

            var nativePath = CombineWithRoot(librariesDirectory, nativeRelativePath);
            TryCopyLibraryFromKnownSources(versionId, nativeRelativePath, nativePath, sourceGameDirectory);

            if (!File.Exists(nativePath) &&
                !downloadEntries.Any(entry => string.Equals(entry.DestinationPath, nativePath, StringComparison.OrdinalIgnoreCase)))
            {
                var repositoryUrl = ResolveLibraryRepositoryUrl(library);
                downloadEntries.Add(DownloadEntry.FromJson(
                    nativeDownload,
                    nativePath,
                    "native",
                    repositoryUrl,
                    nativeRelativePath));
            }

            if (!nativeJars.Contains(nativePath, StringComparer.OrdinalIgnoreCase))
            {
                nativeJars.Add(nativePath);
            }
        }
    }

    private static JsonObject MergeVersionMetadataNodes(JsonObject parentNode, JsonObject childNode)
    {
        var merged = (JsonObject)parentNode.DeepClone();
        foreach (var property in childNode)
        {
            var key = property.Key;
            var childValue = property.Value;
            if (childValue is null)
            {
                merged[key] = null;
                continue;
            }

            if (key.Equals("libraries", StringComparison.OrdinalIgnoreCase))
            {
                merged[key] = MergeLibraries(parentNode["libraries"] as JsonArray, childValue as JsonArray);
                continue;
            }

            if (key.Equals("arguments", StringComparison.OrdinalIgnoreCase))
            {
                merged[key] = MergeArguments(parentNode["arguments"] as JsonObject, childValue as JsonObject);
                continue;
            }

            if (merged[key] is JsonObject existingObject && childValue is JsonObject childObject)
            {
                merged[key] = MergeJsonObjects(existingObject, childObject);
                continue;
            }

            merged[key] = childValue.DeepClone();
        }

        return merged;
    }

    private async Task EnsureForgeClientLibraryForLaunchAsync(
        JsonElement versionRoot,
        string gameDirectory,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!TryGetForgeClientLibraryNameFromLaunchMetadata(versionRoot, out var libraryName))
        {
            return;
        }

        var relativePath = BuildLibraryRelativePathFromName(libraryName);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        var clientJarPath = CombineWithRoot(librariesDirectory, relativePath);
        if (File.Exists(clientJarPath))
        {
            return;
        }

        progress?.Report(new LauncherProgress("Восстанавливаю Forge client jar...", 0, 1));
        await EnsureForgeClientLibraryFromInstallerAsync(
            libraryName,
            gameDirectory,
            clientJarPath,
            progress,
            cancellationToken);

        if (!File.Exists(clientJarPath))
        {
            throw new InvalidOperationException("Не удалось подготовить Forge client jar для запуска.");
        }
    }

    private static bool TryGetForgeClientLibraryNameFromLaunchMetadata(JsonElement versionRoot, out string libraryName)
    {
        libraryName = string.Empty;

        if (!TryGetGameLaunchArgumentValue(versionRoot, "--fml.mcVersion", out var minecraftVersionId) ||
            !TryGetGameLaunchArgumentValue(versionRoot, "--fml.forgeVersion", out var forgeVersionId))
        {
            return false;
        }

        minecraftVersionId = minecraftVersionId.Trim();
        forgeVersionId = forgeVersionId.Trim();
        if (string.IsNullOrWhiteSpace(minecraftVersionId) || string.IsNullOrWhiteSpace(forgeVersionId))
        {
            return false;
        }

        libraryName = $"net.minecraftforge:forge:{minecraftVersionId}-{forgeVersionId}:client";
        return true;
    }

    private static bool TryGetGameLaunchArgumentValue(JsonElement versionRoot, string argumentName, out string value)
    {
        value = string.Empty;

        if (!versionRoot.TryGetProperty("arguments", out var argumentsElement) ||
            !argumentsElement.TryGetProperty("game", out var gameElement) ||
            gameElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var expectValue = false;
        foreach (var argument in gameElement.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                expectValue = false;
                continue;
            }

            var token = argument.GetString() ?? string.Empty;
            if (expectValue)
            {
                value = token;
                return !string.IsNullOrWhiteSpace(value);
            }

            expectValue = string.Equals(token, argumentName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static JsonObject MergeArguments(JsonObject? parentArguments, JsonObject? childArguments)
    {
        if (parentArguments is null)
        {
            return childArguments is null ? new JsonObject() : (JsonObject)childArguments.DeepClone();
        }

        if (childArguments is null)
        {
            return (JsonObject)parentArguments.DeepClone();
        }

        var merged = (JsonObject)parentArguments.DeepClone();
        foreach (var property in childArguments)
        {
            var key = property.Key;
            var childValue = property.Value;
            if (childValue is null)
            {
                merged[key] = null;
                continue;
            }

            if ((key.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("jvm", StringComparison.OrdinalIgnoreCase)) &&
                childValue is JsonArray childArray)
            {
                merged[key] = MergeArgumentArray(merged[key] as JsonArray, childArray);
                continue;
            }

            if (merged[key] is JsonObject existingObject && childValue is JsonObject childObject)
            {
                merged[key] = MergeJsonObjects(existingObject, childObject);
                continue;
            }

            merged[key] = childValue.DeepClone();
        }

        return merged;
    }

    private static JsonArray MergeLibraries(JsonArray? parentLibraries, JsonArray? childLibraries)
    {
        var merged = new JsonArray();
        var mergedLibraryIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddLibraries(JsonArray? source)
        {
            if (source is null)
            {
                return;
            }

            var index = 0;
            foreach (var libraryNode in source)
            {
                AddOrMergeLibraryNode(merged, mergedLibraryIndexes, libraryNode, index++);
            }
        }

        // Parent first, then child overrides matching fields while preserving
        // missing metadata such as natives/classifiers from the base version.
        AddLibraries(parentLibraries);
        AddLibraries(childLibraries);
        return merged;
    }

    private static void AddOrMergeLibraryNode(
        JsonArray target,
        IDictionary<string, int> indexByLibraryKey,
        JsonNode? libraryNode,
        int fallbackIndex)
    {
        if (libraryNode is null)
        {
            return;
        }

        var libraryKey = GetLibraryIdentity(libraryNode, fallbackIndex);
        if (indexByLibraryKey.TryGetValue(libraryKey, out var existingIndex))
        {
            if (target[existingIndex] is JsonObject existingObject &&
                libraryNode is JsonObject incomingObject)
            {
                target[existingIndex] = MergeJsonObjects(existingObject, incomingObject);
            }
            else
            {
                target[existingIndex] = libraryNode.DeepClone();
            }

            return;
        }

        indexByLibraryKey[libraryKey] = target.Count;
        target.Add(libraryNode.DeepClone());
    }

    private static JsonArray MergeArgumentArray(JsonArray? parentArray, JsonArray childArray)
    {
        var merged = new JsonArray();

        void AddRange(JsonArray? source)
        {
            if (source is null)
            {
                return;
            }

            foreach (var item in source)
            {
                if (item is null)
                {
                    continue;
                }

                merged.Add(item.DeepClone());
            }
        }

        AddRange(parentArray);
        AddRange(childArray);
        return merged;
    }

    private static JsonObject MergeJsonObjects(JsonObject baseObject, JsonObject overrideObject)
    {
        var merged = (JsonObject)baseObject.DeepClone();
        foreach (var property in overrideObject)
        {
            if (property.Value is JsonObject overrideChild &&
                merged[property.Key] is JsonObject baseChild)
            {
                merged[property.Key] = MergeJsonObjects(baseChild, overrideChild);
            }
            else
            {
                merged[property.Key] = property.Value?.DeepClone();
            }
        }

        return merged;
    }

    private async Task<MinecraftVersionEntry> ResolveVersionEntryByIdAsync(
        string versionId,
        string? preferredGameDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new ArgumentException("ID версии не указан.", nameof(versionId));
        }

        var normalizedVersionId = versionId.Trim();

        if (!string.IsNullOrWhiteSpace(preferredGameDirectory))
        {
            var preferredMetadataPath = Path.Combine(
                preferredGameDirectory,
                "versions",
                normalizedVersionId,
                $"{normalizedVersionId}.json");
            if (File.Exists(preferredMetadataPath))
            {
                return new MinecraftVersionEntry(
                    normalizedVersionId,
                    "local",
                    new DateTimeOffset(File.GetLastWriteTimeUtc(preferredMetadataPath), TimeSpan.Zero),
                    "local",
                    string.Empty,
                    LocalMetadataPath: preferredMetadataPath,
                    SourceGameDirectory: preferredGameDirectory);
            }
        }

        foreach (var externalVersion in LoadExternalGameVersions())
        {
            if (string.Equals(externalVersion.Id, normalizedVersionId, StringComparison.Ordinal))
            {
                return externalVersion;
            }
        }

        foreach (var bundledVersion in LoadBundledVersions())
        {
            if (string.Equals(bundledVersion.Id, normalizedVersionId, StringComparison.Ordinal))
            {
                return bundledVersion;
            }
        }

        using var manifest = await GetJsonDocumentWithUserAgentAsync(VersionManifestUrl, cancellationToken);
        foreach (var versionElement in manifest.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (!versionElement.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidateId = idElement.GetString();
            if (!string.Equals(candidateId, normalizedVersionId, StringComparison.Ordinal))
            {
                continue;
            }

            var type = versionElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString() ?? "release"
                : "release";
            var url = versionElement.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;
            var sha1 = versionElement.TryGetProperty("sha1", out var sha1Element) && sha1Element.ValueKind == JsonValueKind.String
                ? sha1Element.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                break;
            }

            var releaseTime = DateTimeOffset.UtcNow;
            if (versionElement.TryGetProperty("releaseTime", out var releaseElement) &&
                releaseElement.ValueKind == JsonValueKind.String)
            {
                var releaseText = releaseElement.GetString();
                if (!string.IsNullOrWhiteSpace(releaseText) &&
                    DateTimeOffset.TryParse(
                        releaseText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var parsedRelease))
                {
                    releaseTime = parsedRelease;
                }
            }

            return new MinecraftVersionEntry(
                normalizedVersionId,
                type,
                releaseTime,
                url,
                sha1);
        }

        throw new InvalidOperationException($"Не удалось найти metadata для версии '{normalizedVersionId}'.");
    }

    private static async Task<JsonDocument> DownloadVersionMetadataAsync(
        MinecraftVersionEntry version,
        string gameDirectory,
        CancellationToken cancellationToken)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", version.Id);
        Directory.CreateDirectory(versionDirectory);

        var versionJsonPath = Path.Combine(versionDirectory, $"{version.Id}.json");
        if (!TryCopyVersionMetadataFromKnownSources(version, versionJsonPath))
        {
            await DownloadFileIfNeededAsync(version.MetadataUrl, versionJsonPath, version.MetadataSha1, cancellationToken);
        }

        UpgradeLegacyOptiFineMetadataIfNeeded(versionJsonPath);

        await using var stream = File.OpenRead(versionJsonPath);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryCopyVersionMetadataFromKnownSources(MinecraftVersionEntry version, string destinationPath)
    {
        if (!string.IsNullOrWhiteSpace(version.LocalMetadataPath) && File.Exists(version.LocalMetadataPath))
        {
            if (PathsReferToSameFile(version.LocalMetadataPath, destinationPath))
            {
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Некорректный путь метаданных версии."));
            File.Copy(version.LocalMetadataPath, destinationPath, overwrite: true);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(version.SourceGameDirectory))
        {
            var sourceVersionDirectory = Path.Combine(version.SourceGameDirectory, "versions", version.Id);
            if (TryCopyVersionMetadataFromDirectory(sourceVersionDirectory, version.Id, destinationPath))
            {
                return true;
            }
        }

        foreach (var gameDirectory in EnumerateKnownGameDirectories())
        {
            var sourceVersionDirectory = Path.Combine(gameDirectory, "versions", version.Id);
            if (TryCopyVersionMetadataFromDirectory(sourceVersionDirectory, version.Id, destinationPath))
            {
                return true;
            }
        }

        return TryCopyBundledVersionJson(version.Id, destinationPath);
    }

    private static bool TryCopyVersionMetadataFromDirectory(string? sourceDirectory, string versionId, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return false;
        }

        var sourcePath = Path.Combine(sourceDirectory, $"{versionId}.json");
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Некорректный путь метаданных версии."));
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return true;
    }

    private static bool TryCopyBundledVersionJson(string versionId, string destinationPath)
    {
        var sourceDirectory = ResolveBundledVersionDirectory(versionId);
        if (sourceDirectory is null)
        {
            return false;
        }

        var sourcePath = Path.Combine(sourceDirectory, $"{versionId}.json");
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("РќРµРєРѕСЂСЂРµРєС‚РЅС‹Р№ РїСѓС‚СЊ РјРµС‚Р°РґР°РЅРЅС‹С… РІРµСЂСЃРёРё."));
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return true;
    }

    private static void UpgradeLegacyOptiFineMetadataIfNeeded(string versionJsonPath)
    {
        if (string.IsNullOrWhiteSpace(versionJsonPath) || !File.Exists(versionJsonPath))
        {
            return;
        }

        try
        {
            var sourceText = File.ReadAllText(versionJsonPath);
            var sourceNode = JsonNode.Parse(sourceText) as JsonObject;
            if (sourceNode is null)
            {
                return;
            }

            if (!TryGetStringNodeValue(sourceNode["type"], out var versionType) ||
                !versionType.Equals("optifine", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (TryGetStringNodeValue(sourceNode["inheritsFrom"], out var inheritsFrom) &&
                !string.IsNullOrWhiteSpace(inheritsFrom))
            {
                return;
            }

            if (!TryGetStringNodeValue(sourceNode["jar"], out var baseVersionId) ||
                string.IsNullOrWhiteSpace(baseVersionId))
            {
                return;
            }

            if (sourceNode["libraries"] is not JsonArray sourceLibraries)
            {
                return;
            }

            var migratedLibraries = new JsonArray();
            var hasOptiFineLibrary = false;
            var hasLaunchWrapperLibrary = false;
            foreach (var libraryNode in sourceLibraries)
            {
                if (libraryNode is not JsonObject libraryObject ||
                    !TryGetStringNodeValue(libraryObject["name"], out var libraryName))
                {
                    continue;
                }

                if (libraryName.StartsWith("optifine:OptiFine:", StringComparison.OrdinalIgnoreCase))
                {
                    migratedLibraries.Add(libraryObject.DeepClone());
                    hasOptiFineLibrary = true;
                    continue;
                }

                if (libraryName.Equals("optifine:launchwrapper:2.3", StringComparison.OrdinalIgnoreCase))
                {
                    migratedLibraries.Add(libraryObject.DeepClone());
                    hasLaunchWrapperLibrary = true;
                }
            }

            if (!hasOptiFineLibrary)
            {
                return;
            }

            if (!hasLaunchWrapperLibrary)
            {
                migratedLibraries.Add(new JsonObject
                {
                    ["name"] = "optifine:launchwrapper:2.3"
                });
            }

            var versionId = TryGetStringNodeValue(sourceNode["id"], out var existingVersionId) &&
                            !string.IsNullOrWhiteSpace(existingVersionId)
                ? existingVersionId
                : Path.GetFileNameWithoutExtension(versionJsonPath);

            var migratedNode = new JsonObject
            {
                ["id"] = versionId,
                ["inheritsFrom"] = baseVersionId,
                ["type"] = "optifine",
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                ["jar"] = baseVersionId,
                ["releaseTime"] = sourceNode["releaseTime"]?.DeepClone(),
                ["time"] = sourceNode["time"]?.DeepClone(),
                ["libraries"] = migratedLibraries
            };

            EnsureOptiFineTweakClass(migratedNode);
            var migratedText = migratedNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (!string.Equals(sourceText, migratedText, StringComparison.Ordinal))
            {
                File.WriteAllText(versionJsonPath, migratedText, Encoding.UTF8);
            }
        }
        catch
        {
            // Keep startup resilient even if a local legacy OptiFine profile is malformed.
        }
    }

    private static bool IsOptiFineLibraryName(string? libraryName)
        => !string.IsNullOrWhiteSpace(libraryName) &&
           libraryName.StartsWith("optifine:OptiFine:", StringComparison.OrdinalIgnoreCase);

    private static bool IsForgeClientLibraryName(string? libraryName)
        => !string.IsNullOrWhiteSpace(libraryName) &&
           libraryName.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase) &&
           libraryName.EndsWith(":client", StringComparison.OrdinalIgnoreCase);

    private static bool IsForgeInstallerBundledLibraryName(string? libraryName)
        => !string.IsNullOrWhiteSpace(libraryName) &&
           libraryName.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase) &&
           TryParseForgeCombinedVersion(libraryName, out _, out _);

    private static bool IsOptiFineLaunchwrapperLibraryName(string? libraryName)
        => !string.IsNullOrWhiteSpace(libraryName) &&
           (libraryName.StartsWith("optifine:launchwrapper:", StringComparison.OrdinalIgnoreCase) ||
            libraryName.StartsWith("optifine:launchwrapper-of:", StringComparison.OrdinalIgnoreCase));

    private static bool TryCreateExpectedOptiFineLaunchwrapperLibrary(
        string? libraryName,
        string librariesDirectory,
        out OptiFineLaunchwrapperLibrary library)
    {
        library = null!;
        if (string.IsNullOrWhiteSpace(libraryName) ||
            !libraryName.StartsWith("optifine:launchwrapper-of:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var launchwrapperVersion = libraryName["optifine:launchwrapper-of:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(launchwrapperVersion))
        {
            return false;
        }

        var fileName = $"launchwrapper-of-{launchwrapperVersion}.jar";
        var relativePath = Path.Combine("optifine", "launchwrapper-of", launchwrapperVersion, fileName);
        var absolutePath = CombineWithRoot(librariesDirectory, relativePath);
        library = new OptiFineLaunchwrapperLibrary(
            $"optifine:launchwrapper-of:{launchwrapperVersion}",
            relativePath,
            absolutePath);
        return true;
    }

    private static void ReplaceOptiFineLaunchwrapperEntries(List<string> classpathEntries, string launchwrapperPath)
    {
        var insertionIndex = -1;
        for (var index = classpathEntries.Count - 1; index >= 0; index--)
        {
            if (!IsOptiFineLaunchwrapperClasspathEntry(classpathEntries[index]))
            {
                continue;
            }

            insertionIndex = index;
            classpathEntries.RemoveAt(index);
        }

        if (insertionIndex < 0 || insertionIndex > classpathEntries.Count)
        {
            classpathEntries.Add(launchwrapperPath);
            return;
        }

        classpathEntries.Insert(insertionIndex, launchwrapperPath);
    }

    private static bool IsOptiFineLaunchwrapperClasspathEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalizedPath.Contains(
                   $"{Path.DirectorySeparatorChar}optifine{Path.DirectorySeparatorChar}launchwrapper{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.Contains(
                   $"{Path.DirectorySeparatorChar}optifine{Path.DirectorySeparatorChar}launchwrapper-of{Path.DirectorySeparatorChar}",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static OptiFineLaunchwrapperLibrary EnsureOptiFineLaunchwrapperLibrary(
        string optiFineJarPath,
        string librariesDirectory,
        string versionId,
        string? sourceGameDirectory = null)
    {
        var embeddedLibrary = TryExtractEmbeddedOptiFineLaunchwrapper(optiFineJarPath, librariesDirectory);
        if (embeddedLibrary != null)
        {
            return embeddedLibrary;
        }

        var fallbackRelativePath = Path.Combine("optifine", "launchwrapper", "2.3", "launchwrapper-2.3.jar");
        var fallbackAbsolutePath = CombineWithRoot(librariesDirectory, fallbackRelativePath);
        if (!File.Exists(fallbackAbsolutePath))
        {
            TryCopyLibraryFromKnownSources(versionId, fallbackRelativePath, fallbackAbsolutePath, sourceGameDirectory);
        }

        if (!File.Exists(fallbackAbsolutePath))
        {
            throw new InvalidOperationException(
                "Не удалось подготовить OptiFine launchwrapper. Во встроенном jar нет launchwrapper-of, а внешний launchwrapper-2.3 тоже не найден.");
        }

        return new OptiFineLaunchwrapperLibrary(
            "optifine:launchwrapper:2.3",
            fallbackRelativePath,
            fallbackAbsolutePath);
    }

    private static OptiFineLaunchwrapperLibrary? TryExtractEmbeddedOptiFineLaunchwrapper(
        string optiFineJarPath,
        string librariesDirectory)
    {
        var optiFineArchivePath = ResolveOptiFineArchivePath(optiFineJarPath);
        if (string.IsNullOrWhiteSpace(optiFineArchivePath) || !File.Exists(optiFineArchivePath))
        {
            return null;
        }

        try
        {
            var launchwrapperVersion = TryReadEmbeddedOptiFineLaunchwrapperVersion(optiFineArchivePath);
            if (string.IsNullOrWhiteSpace(launchwrapperVersion))
            {
                return null;
            }

            var fileName = $"launchwrapper-of-{launchwrapperVersion}.jar";
            var relativePath = Path.Combine("optifine", "launchwrapper-of", launchwrapperVersion, fileName);
            var absolutePath = CombineWithRoot(librariesDirectory, relativePath);
            if (!File.Exists(absolutePath))
            {
                using var archive = ZipFile.OpenRead(optiFineArchivePath);
                var entry = archive.GetEntry(fileName);
                if (entry is null)
                {
                    return null;
                }

                Directory.CreateDirectory(
                    Path.GetDirectoryName(absolutePath) ?? throw new InvalidOperationException("Некорректный путь OptiFine launchwrapper."));

                using var source = entry.Open();
                using var destination = File.Create(absolutePath);
                source.CopyTo(destination);
            }

            return File.Exists(absolutePath)
                ? new OptiFineLaunchwrapperLibrary(
                    $"optifine:launchwrapper-of:{launchwrapperVersion}",
                    relativePath,
                    absolutePath)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadEmbeddedOptiFineLaunchwrapperVersion(string optiFineJarPath)
    {
        if (string.IsNullOrWhiteSpace(optiFineJarPath) || !File.Exists(optiFineJarPath))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(optiFineJarPath);
            var markerEntry = archive.GetEntry("launchwrapper-of.txt");
            if (markerEntry != null)
            {
                using var reader = new StreamReader(markerEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var launchwrapperVersion = reader.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(launchwrapperVersion))
                {
                    return launchwrapperVersion;
                }
            }

            const string prefix = "launchwrapper-of-";
            foreach (var entry in archive.Entries)
            {
                var entryName = Path.GetFileName(entry.FullName);
                if (!entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !entryName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var launchwrapperVersion = entryName[prefix.Length..^4].Trim();
                if (!string.IsNullOrWhiteSpace(launchwrapperVersion))
                {
                    return launchwrapperVersion;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private async Task EnsurePatchedOptiFineLibraryAsync(
        string optiFineLibraryPath,
        string baseJarPath,
        string javaExecutable,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(optiFineLibraryPath) ||
            string.IsNullOrWhiteSpace(baseJarPath) ||
            string.IsNullOrWhiteSpace(javaExecutable) ||
            !File.Exists(optiFineLibraryPath) ||
            !File.Exists(baseJarPath) ||
            !IsOptiFinePatchArchive(optiFineLibraryPath))
        {
            return;
        }

        var installerArchivePath = GetOptiFineInstallerArchivePath(optiFineLibraryPath);
        if (!File.Exists(installerArchivePath) || !IsOptiFinePatchArchive(installerArchivePath))
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(installerArchivePath) ?? throw new InvalidOperationException("Некорректный путь исходного OptiFine архива."));
            File.Copy(optiFineLibraryPath, installerArchivePath, overwrite: true);
        }

        File.Delete(optiFineLibraryPath);

        progress?.Report(new LauncherProgress("Собираю patched OptiFine jar...", 0, 1));
        await RunJavaMainAsync(
            javaExecutable,
            installerArchivePath,
            "optifine.Patcher",
            [baseJarPath, installerArchivePath, optiFineLibraryPath],
            cancellationToken);

        if (!File.Exists(optiFineLibraryPath))
        {
            throw new InvalidOperationException("OptiFine patcher завершился без создания patched jar.");
        }
    }

    private static bool IsOptiFinePatchArchive(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            return archive.GetEntry("optifine/Patcher.class") != null &&
                   archive.Entries.Any(entry =>
                       entry.FullName.StartsWith("patch/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveOptiFineArchivePath(string optiFineLibraryPath)
    {
        if (string.IsNullOrWhiteSpace(optiFineLibraryPath))
        {
            return optiFineLibraryPath;
        }

        var installerArchivePath = GetOptiFineInstallerArchivePath(optiFineLibraryPath);
        return File.Exists(installerArchivePath)
            ? installerArchivePath
            : optiFineLibraryPath;
    }

    private static string GetOptiFineInstallerArchivePath(string optiFineLibraryPath)
    {
        var directory = Path.GetDirectoryName(optiFineLibraryPath)
                        ?? throw new InvalidOperationException("Некорректный путь OptiFine библиотеки.");
        var fileName = Path.GetFileNameWithoutExtension(optiFineLibraryPath);
        var extension = Path.GetExtension(optiFineLibraryPath);
        return Path.Combine(directory, $"{fileName}-installer{extension}");
    }

    private static async Task RunJavaMainAsync(
        string javaExecutable,
        string classpathEntry,
        string mainClass,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var tempDirectory = ResolveLauncherJavaTemporaryDirectory(mainClass);
        EnsureSufficientFreeSpace(tempDirectory, 512L * 1024 * 1024, $"временных файлов Java для {mainClass}");
        var processInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ApplyLauncherTempEnvironment(processInfo, tempDirectory);

        processInfo.ArgumentList.Add($"-Djava.io.tmpdir={tempDirectory}");
        processInfo.ArgumentList.Add("-cp");
        processInfo.ArgumentList.Add(classpathEntry);
        processInfo.ArgumentList.Add(mainClass);
        foreach (var argument in arguments)
        {
            processInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processInfo)
                            ?? throw new InvalidOperationException($"Не удалось запустить Java процесс '{mainClass}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode == 0)
        {
            return;
        }

        var errorText = string.Join(
            Environment.NewLine,
            new[] { stdOut, stdErr }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()))
            .Trim();

        throw new InvalidOperationException(
            $"Java процесс '{mainClass}' завершился с кодом {process.ExitCode}.{(string.IsNullOrWhiteSpace(errorText) ? string.Empty : Environment.NewLine + errorText)}");
    }

    private static async Task RunJavaJarAsync(
        string javaExecutable,
        string jarPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var tempDirectory = ResolveLauncherJavaTemporaryDirectory(Path.GetFileNameWithoutExtension(jarPath));
        EnsureSufficientFreeSpace(tempDirectory, 512L * 1024 * 1024, $"временных файлов Java для {Path.GetFileName(jarPath)}");
        var processInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ApplyLauncherTempEnvironment(processInfo, tempDirectory);

        processInfo.ArgumentList.Add($"-Djava.io.tmpdir={tempDirectory}");
        processInfo.ArgumentList.Add("-jar");
        processInfo.ArgumentList.Add(jarPath);
        foreach (var argument in arguments)
        {
            processInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processInfo)
                            ?? throw new InvalidOperationException("Не удалось запустить Java процесс Forge installer.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode == 0)
        {
            return;
        }

        var errorText = string.Join(
            Environment.NewLine,
            new[] { stdOut, stdErr }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()))
            .Trim();

        throw new InvalidOperationException(
            $"Forge installer завершился с кодом {process.ExitCode}.{(string.IsNullOrWhiteSpace(errorText) ? string.Empty : Environment.NewLine + errorText)}");
    }

    private static bool PathsReferToSameFile(string firstPath, string secondPath)
    {
        if (string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
        {
            return false;
        }

        var fullFirstPath = Path.GetFullPath(firstPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullSecondPath = Path.GetFullPath(secondPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(fullFirstPath, fullSecondPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCopyVersionJarFromKnownSources(MinecraftVersionEntry version, string versionId, string destinationPath)
    {
        if (!string.IsNullOrWhiteSpace(version.LocalMetadataPath))
        {
            var localVersionDirectory = Path.GetDirectoryName(version.LocalMetadataPath);
            if (TryCopyVersionJarFromDirectory(localVersionDirectory, versionId, destinationPath))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(version.SourceGameDirectory))
        {
            var sourceVersionDirectory = Path.Combine(version.SourceGameDirectory, "versions", versionId);
            if (TryCopyVersionJarFromDirectory(sourceVersionDirectory, versionId, destinationPath))
            {
                return true;
            }
        }

        foreach (var gameDirectory in EnumerateKnownGameDirectories())
        {
            var sourceVersionDirectory = Path.Combine(gameDirectory, "versions", versionId);
            if (TryCopyVersionJarFromDirectory(sourceVersionDirectory, versionId, destinationPath))
            {
                return true;
            }
        }

        if (TryCopyBundledVersionJar(versionId, destinationPath))
        {
            return true;
        }

        return false;
    }

    private static bool TryCopyVersionJarFromDirectory(string? sourceDirectory, string versionId, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return false;
        }

        var sourcePath = Path.Combine(sourceDirectory, $"{versionId}.jar");
        if (!File.Exists(sourcePath))
        {
            var backupSourcePath = sourcePath + ".bak";
            if (!File.Exists(backupSourcePath))
            {
                return false;
            }

            sourcePath = backupSourcePath;
        }

        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Некорректный путь jar файла версии."));
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return true;
    }

    private static bool TryCopyBundledVersionJar(string versionId, string destinationPath)
    {
        var sourceDirectory = ResolveBundledVersionDirectory(versionId);
        if (sourceDirectory is null)
        {
            return false;
        }

        var sourcePath = Path.Combine(sourceDirectory, $"{versionId}.jar");
        if (!File.Exists(sourcePath))
        {
            var backupSourcePath = sourcePath + ".bak";
            if (!File.Exists(backupSourcePath))
            {
                return false;
            }

            sourcePath = backupSourcePath;
        }

        if (PathsReferToSameFile(sourcePath, destinationPath))
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("РќРµРєРѕСЂСЂРµРєС‚РЅС‹Р№ РїСѓС‚СЊ jar С„Р°Р№Р»Р° РІРµСЂСЃРёРё."));
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return true;
    }

    private static string? ResolveBundledVersionDirectory(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        var directory = Path.Combine(AppContext.BaseDirectory, BundledVersionsDirectoryName, versionId);
        return Directory.Exists(directory) ? directory : null;
    }

    private static Dictionary<string, string> BuildLaunchReplacements(
        LaunchOptions options,
        JsonElement versionRoot,
        string gameDirectory,
        string assetsDirectory,
        string assetIndexId,
        string classpath,
        string nativesDirectory,
        string versionId,
        string librariesDirectory)
    {
        var isLegacyTarget = ShouldUseLegacyRuntimeCompatibility(versionId);

        // Use real MSA credentials when available, otherwise fall back to local offline or Vesper auth.
        var msa = options.MicrosoftSession;
        var vesper = options.VesperAuthSession;
        var uuid = msa?.Uuid ?? vesper?.Uuid ?? BuildOfflineUuid(options.Username);
        var accessToken = msa?.AccessToken
            ?? vesper?.AccessToken
            ?? BuildOfflineUuid(options.Username).Replace("-", string.Empty, StringComparison.Ordinal);

        // When we have a prepared offline skin session, prefer its UUID so every runtime
        // sees the same player identity and the local auth server can re-issue textures consistently.
        if (msa is null &&
            vesper is null &&
            !isLegacyTarget &&
            !string.IsNullOrWhiteSpace(options.OfflineSkinSessionUuid))
        {
            uuid = options.OfflineSkinSessionUuid!;
            accessToken = options.OfflineSkinSessionUuid!.Replace("-", string.Empty, StringComparison.Ordinal);
        }

        // For 1.16.5 offline, use TLauncher style UUID without hyphens in both uuid and token.
        if (msa == null && vesper is null && isLegacyTarget)
        {
            var offlineUuidWithoutHyphens = BuildOfflineUuid(options.Username).Replace("-", "", StringComparison.Ordinal);
            uuid = offlineUuidWithoutHyphens;
            accessToken = offlineUuidWithoutHyphens;
        }
        else if (vesper is not null && isLegacyTarget)
        {
            uuid = vesper.Uuid.Replace("-", string.Empty, StringComparison.Ordinal);
        }

        var username = msa?.Username ?? vesper?.Username ?? options.Username;

        var userPropertiesJson = !string.IsNullOrWhiteSpace(options.PrecomputedUserPropertiesJson)
            ? options.PrecomputedUserPropertiesJson!
            : BuildUserPropertiesJson(
                options.SelectedSkinPath,
                uuid,
                username,
                options.SelectedSkinIsSlim,
                ShouldUseDirectLoopbackSkinHost(versionId));

        // If the launcher prepared a custom skin payload, keep the launch in legacy user mode
        // so Minecraft does not replace the selected launcher skin with the account skin.
        var hasLauncherManagedSkin = !string.IsNullOrWhiteSpace(options.PrecomputedUserPropertiesJson) ||
                                     !string.IsNullOrWhiteSpace(options.SelectedSkinPath);
        string userType = msa != null && !hasLauncherManagedSkin ? "msa" : "legacy";

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = username,
            ["user_name"] = username,
            ["player_name"] = username,
            ["version_name"] = versionId,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = assetIndexId,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = accessToken,
            ["clientid"] = "offline-client",
            ["auth_xuid"] = "0",
            ["user_type"] = userType,
            ["version_type"] = options.Version.Type,
            ["natives_directory"] = nativesDirectory,
            ["nativelibrary_directory"] = nativesDirectory,
            ["launcher_name"] = "Vesper Launcher",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = librariesDirectory,
            ["game_libraries_directory"] = librariesDirectory,
            ["user_properties"] = userPropertiesJson,
            ["user_properties_map"] = userPropertiesJson,
            ["profile_properties"] = userPropertiesJson,
            ["profileProperties"] = userPropertiesJson,
            ["resolution_width"] = "1280",
            ["resolution_height"] = "720"
        };

        var quickPlayMultiplayer = BuildQuickPlayMultiplayerValue(
            options.DirectConnectServerAddress,
            options.DirectConnectServerPort);
        values["quickPlayMultiplayer"] = quickPlayMultiplayer;
        values["quickPlaySingleplayer"] = string.Empty;
        values["quickPlayRealms"] = string.Empty;
        values["quickPlayPath"] = BuildQuickPlayPath(gameDirectory, quickPlayMultiplayer);

        if (versionRoot.TryGetProperty("type", out var typeElement))
        {
            values["version_type"] = typeElement.GetString() ?? options.Version.Type;
        }

        return values;
    }

    private static string BuildQuickPlayMultiplayerValue(string? serverAddress, int? serverPort)
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            return string.Empty;
        }

        var normalizedAddress = serverAddress.Trim();
        if (serverPort is int port &&
            port > 0 &&
            port <= 65535)
        {
            return $"{normalizedAddress}:{port}";
        }

        return normalizedAddress;
    }

    private static string BuildQuickPlayPath(string gameDirectory, string quickPlayMultiplayer)
    {
        try
        {
            var quickPlayDirectory = Path.Combine(gameDirectory, ".launcher", "quickplay");
            Directory.CreateDirectory(quickPlayDirectory);

            var safeName = string.IsNullOrWhiteSpace(quickPlayMultiplayer)
                ? "default"
                : Regex.Replace(quickPlayMultiplayer, @"[^A-Za-z0-9._-]+", "_");

            return Path.Combine(quickPlayDirectory, $"{safeName}.json");
        }
        catch
        {
            return Path.Combine(gameDirectory, "quickplay.json");
        }
    }

    private static string BuildUserPropertiesJson(
        string? selectedSkinPath,
        string uuid,
        string username,
        bool isSlimModel,
        bool preferDirectLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(selectedSkinPath))
        {
            return "{}";
        }

        try
        {
            var fullSkinPath = Path.GetFullPath(selectedSkinPath);
            if (!File.Exists(fullSkinPath))
            {
                return "{}";
            }

            var skinUri = TryGetHostedSkinUrl(fullSkinPath, preferDirectLoopback) ?? new Uri(fullSkinPath, UriKind.Absolute).AbsoluteUri;
            return BuildUserPropertiesJsonFromSkinUrl(skinUri, uuid, username, isSlimModel);
        }
        catch
        {
            return "{}";
        }
    }

    private static string BuildUserPropertiesJsonFromSkinUrl(
        string? skinUri,
        string uuid,
        string username,
        bool isSlimModel)
    {
        if (string.IsNullOrWhiteSpace(skinUri))
        {
            return "{}";
        }

        return BuildUserPropertiesJsonFromTextureValue(
            BuildTextureValueFromSkinUrl(skinUri, uuid, username, isSlimModel),
            textureSignature: null);
    }

    private static string BuildTextureValueFromSkinUrl(
        string skinUri,
        string uuid,
        string username,
        bool isSlimModel)
    {
        var skinTexture = new Dictionary<string, object?>
        {
            ["url"] = skinUri
        };
        if (isSlimModel)
        {
            skinTexture["metadata"] = new Dictionary<string, string>
            {
                ["model"] = "slim"
            };
        }

        var texturePayload = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["profileId"] = uuid.Replace("-", string.Empty, StringComparison.Ordinal),
            ["profileName"] = username,
            ["signatureRequired"] = false,
            ["textures"] = new Dictionary<string, object?>
            {
                ["SKIN"] = skinTexture
            }
        };

        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(texturePayload)));
    }

    private static string BuildUserPropertiesJsonFromTextureValue(string textureValue, string? textureSignature)
    {
        if (string.IsNullOrWhiteSpace(textureValue))
        {
            return "{}";
        }

        var userProperties = new[]
        {
            new Dictionary<string, string>
            {
                ["name"] = "textures",
                ["value"] = textureValue
            }
        };

        if (!string.IsNullOrWhiteSpace(textureSignature))
        {
            userProperties[0]["signature"] = textureSignature!;
        }

        return JsonSerializer.Serialize(userProperties);
    }

    private static string BuildSignedUserPropertiesJson(string textureValue, string? textureSignature)
    {
        var property = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "textures",
            ["value"] = textureValue
        };

        if (!string.IsNullOrWhiteSpace(textureSignature))
        {
            property["signature"] = textureSignature;
        }

        return JsonSerializer.Serialize(new[] { property });
    }

    private static string? TryExtractProfileUuidFromTextureValue(string textureValue)
    {
        if (string.IsNullOrWhiteSpace(textureValue))
        {
            return null;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(textureValue);
            using var document = JsonDocument.Parse(jsonBytes);
            if (!document.RootElement.TryGetProperty("profileId", out var profileIdElement))
            {
                return null;
            }

            return NormalizeMineSkinProfileId(profileIdElement.GetString());
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractTextureUrlFromTextureValue(string textureValue)
    {
        if (string.IsNullOrWhiteSpace(textureValue))
        {
            return null;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(textureValue);
            using var document = JsonDocument.Parse(jsonBytes);
            if (!document.RootElement.TryGetProperty("textures", out var texturesElement) ||
                !texturesElement.TryGetProperty("SKIN", out var skinElement) ||
                !skinElement.TryGetProperty("url", out var urlElement))
            {
                return null;
            }

            return NormalizeTextureUrl(urlElement.GetString());
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeTextureUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return rawUrl;
        }

        if (uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1
            };

            return builder.Uri.AbsoluteUri;
        }

        return uri.AbsoluteUri;
    }

    private static bool UsesLauncherHostedSkinUrl(string? textureUrl)
    {
        if (string.IsNullOrWhiteSpace(textureUrl) ||
            !Uri.TryCreate(textureUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase) &&
               !uri.IsDefaultPort;
    }

    private static string? NormalizeMineSkinProfileId(string? rawProfileId)
    {
        if (string.IsNullOrWhiteSpace(rawProfileId))
        {
            return null;
        }

        var compact = rawProfileId.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length != 32)
        {
            return null;
        }

        return $"{compact[..8]}-{compact[8..12]}-{compact[12..16]}-{compact[16..20]}-{compact[20..32]}";
    }

    private static string ComputeFileHashForName(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildMineSkinCacheKey(string filePath, bool isSlimModel)
        => $"{(isSlimModel ? "slim" : "classic")}:{ComputeFileHashForName(filePath)}";

    private static string PrepareSkinFileForLaunch(string fullSkinPath, bool isSlimModel)
    {
        try
        {
            var sourceTexture = LoadSkinTextureForLaunch(fullSkinPath);
            if (sourceTexture is null)
            {
                return fullSkinPath;
            }

            var preparedTexture = PrepareSkinTextureForLaunch(sourceTexture, isSlimModel);
            var preparedDirectory = Path.Combine(BaseStorageDirectory.Value, ".launcher-cache", "prepared-skins");
            Directory.CreateDirectory(preparedDirectory);

            var preparedPath = Path.Combine(
                preparedDirectory,
                $"{ComputeFileHashForName(fullSkinPath)}-{(isSlimModel ? "slim" : "classic")}.png");

            using var image = Image.LoadPixelData<Rgba32>(
                preparedTexture.Pixels,
                preparedTexture.Width,
                preparedTexture.Height);
            image.SaveAsPng(preparedPath);
            return File.Exists(preparedPath) ? preparedPath : fullSkinPath;
        }
        catch
        {
            return fullSkinPath;
        }
    }

    private static SkinTexture? LoadSkinTextureForLaunch(string skinPath)
    {
        try
        {
            using var image = Image.Load<Rgba32>(skinPath);
            if (image.Width < 64 || image.Height < 32)
            {
                return null;
            }

            var isModernLayout = image.Width == image.Height;
            var isLegacyLayout = image.Width == image.Height * 2;
            if (!isModernLayout && !isLegacyLayout)
            {
                return null;
            }

            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            return new SkinTexture(image.Width, image.Height, pixels);
        }
        catch
        {
            return null;
        }
    }

    private static SkinTexture PrepareSkinTextureForLaunch(SkinTexture source, bool isSlimModel)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = (byte[])source.Pixels.Clone();

        ResolveSkinLayoutScale(width, height, out var scaleX, out var scaleY);
        var isModernLayout = width == height;

        BakeSkinOverlayIntoBasePixels(pixels, width, height, scaleX, scaleY, isModernLayout, isSlimModel);
        EnsureBaseSkinOpacity(pixels, width, height, scaleX, scaleY, isModernLayout, isSlimModel);

        return new SkinTexture(width, height, pixels);
    }

    private static void ResolveSkinLayoutScale(int textureWidth, int textureHeight, out double scaleX, out double scaleY)
    {
        scaleX = textureWidth / 64d;
        scaleY = textureWidth == textureHeight * 2
            ? textureHeight / 32d
            : textureHeight / 64d;
    }

    private static SkinRect ScaleSkinRect(int x, int y, int rectWidth, int rectHeight, double scaleX, double scaleY)
    {
        var scaledX = Math.Max(0, (int)Math.Round(x * scaleX));
        var scaledY = Math.Max(0, (int)Math.Round(y * scaleY));
        var scaledWidth = Math.Max(1, (int)Math.Round(rectWidth * scaleX));
        var scaledHeight = Math.Max(1, (int)Math.Round(rectHeight * scaleY));
        return new SkinRect(scaledX, scaledY, scaledWidth, scaledHeight);
    }

    private static void BakeSkinOverlayIntoBasePixels(
        byte[] pixels,
        int width,
        int height,
        double scaleX,
        double scaleY,
        bool isModernLayout,
        bool isSlimModel)
    {
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(40, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(8, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(32, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(0, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(48, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(16, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(56, 8, 8, 8, scaleX, scaleY), ScaleSkinRect(24, 8, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(40, 0, 8, 8, scaleX, scaleY), ScaleSkinRect(8, 0, 8, 8, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(48, 0, 8, 8, scaleX, scaleY), ScaleSkinRect(16, 0, 8, 8, scaleX, scaleY));

        if (!isModernLayout)
        {
            return;
        }

        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(20, 36, 8, 12, scaleX, scaleY), ScaleSkinRect(20, 20, 8, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(16, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(16, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(28, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(28, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(32, 36, 8, 12, scaleX, scaleY), ScaleSkinRect(32, 20, 8, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(20, 32, 8, 4, scaleX, scaleY), ScaleSkinRect(20, 16, 8, 4, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(28, 32, 8, 4, scaleX, scaleY), ScaleSkinRect(28, 16, 8, 4, scaleX, scaleY));

        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(4, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(4, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(0, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(0, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(8, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(8, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(12, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(12, 20, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(4, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(4, 16, 4, 4, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(8, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(8, 16, 4, 4, scaleX, scaleY));

        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(4, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(20, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(0, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(16, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(8, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(24, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(12, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(28, 52, 4, 12, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(4, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(20, 48, 4, 4, scaleX, scaleY));
        CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(8, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(24, 48, 4, 4, scaleX, scaleY));

        if (isSlimModel)
        {
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(44, 36, 3, 12, scaleX, scaleY), ScaleSkinRect(44, 20, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(40, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(40, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(47, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(47, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(51, 36, 3, 12, scaleX, scaleY), ScaleSkinRect(51, 20, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(44, 32, 3, 4, scaleX, scaleY), ScaleSkinRect(44, 16, 3, 4, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(47, 32, 3, 4, scaleX, scaleY), ScaleSkinRect(47, 16, 3, 4, scaleX, scaleY));

            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(52, 52, 3, 12, scaleX, scaleY), ScaleSkinRect(36, 52, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(48, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(32, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(55, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(39, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(59, 52, 3, 12, scaleX, scaleY), ScaleSkinRect(43, 52, 3, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(52, 48, 3, 4, scaleX, scaleY), ScaleSkinRect(36, 48, 3, 4, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(55, 48, 3, 4, scaleX, scaleY), ScaleSkinRect(39, 48, 3, 4, scaleX, scaleY));
        }
        else
        {
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(44, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(44, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(40, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(40, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(48, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(48, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(52, 36, 4, 12, scaleX, scaleY), ScaleSkinRect(52, 20, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(44, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(44, 16, 4, 4, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(48, 32, 4, 4, scaleX, scaleY), ScaleSkinRect(48, 16, 4, 4, scaleX, scaleY));

            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(52, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(36, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(48, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(32, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(56, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(40, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(60, 52, 4, 12, scaleX, scaleY), ScaleSkinRect(44, 52, 4, 12, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(52, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(36, 48, 4, 4, scaleX, scaleY));
            CopyVisibleSkinPixels(pixels, width, height, ScaleSkinRect(56, 48, 4, 4, scaleX, scaleY), ScaleSkinRect(40, 48, 4, 4, scaleX, scaleY));
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

    private static void CopyVisibleSkinPixels(byte[] pixels, int width, int height, SkinRect sourceRect, SkinRect destinationRect)
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

    private static void MakeSkinRegionOpaque(byte[] pixels, int width, int height, SkinRect region)
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

    private static MineSkinCacheEntry? TryLoadSharedSkinCacheEntry(string fullSkinPath, bool isSlimModel)
    {
        try
        {
            var cacheEntry = TryLoadMineSkinCacheEntry(BuildMineSkinCacheKey(fullSkinPath, isSlimModel));
            if (cacheEntry is null || string.IsNullOrWhiteSpace(cacheEntry.TextureValue))
            {
                return null;
            }

            var textureUrl = !string.IsNullOrWhiteSpace(cacheEntry.TextureUrl)
                ? NormalizeTextureUrl(cacheEntry.TextureUrl)
                : TryExtractTextureUrlFromTextureValue(cacheEntry.TextureValue);
            if (UsesLauncherHostedSkinUrl(textureUrl))
            {
                return null;
            }

            return cacheEntry;
        }
        catch
        {
            return null;
        }
    }

    private static MineSkinCacheEntry? TryLoadMineSkinCacheEntry(string cacheKey)
    {
        lock (MineSkinCacheLock)
        {
            try
            {
                if (!File.Exists(MineSkinCachePath))
                {
                    return null;
                }

                var json = File.ReadAllText(MineSkinCachePath);
                var cache = JsonSerializer.Deserialize<MineSkinCacheState>(json);
                if (cache?.Entries is null)
                {
                    return null;
                }

                return cache.Entries.TryGetValue(cacheKey, out var entry) ? entry : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void SaveMineSkinCacheEntry(string cacheKey, MineSkinCacheEntry entry)
    {
        lock (MineSkinCacheLock)
        {
            try
            {
                MineSkinCacheState cache;
                if (File.Exists(MineSkinCachePath))
                {
                    var existingJson = File.ReadAllText(MineSkinCachePath);
                    cache = JsonSerializer.Deserialize<MineSkinCacheState>(existingJson) ?? new MineSkinCacheState();
                }
                else
                {
                    cache = new MineSkinCacheState();
                }

                cache.Entries[cacheKey] = entry;
                var directory = Path.GetDirectoryName(MineSkinCachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var cacheJson = JsonSerializer.Serialize(cache, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(MineSkinCachePath, cacheJson);
            }
            catch
            {
                // ignore cache write issues
            }
        }
    }

    private static async Task<MineSkinCacheEntry?> TryGenerateAndCacheSharedSkinAsync(
        string fullSkinPath,
        bool isSlimModel,
        string username,
        CancellationToken cancellationToken)
    {
        var cachedEntry = TryLoadSharedSkinCacheEntry(fullSkinPath, isSlimModel);
        if (cachedEntry != null)
        {
            return cachedEntry;
        }

        var apiKey = NormalizeMineSkinApiKey(Environment.GetEnvironmentVariable("VESPER_MINESKIN_API_KEY"));
        if (!File.Exists(fullSkinPath))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, MineSkinGenerateUrl);
            request.Headers.UserAgent.ParseAdd("VesperLauncher/1.0");
            request.Headers.TryAddWithoutValidation("MineSkin-User-Agent", "VesperLauncher/1.0");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var form = new MultipartFormDataContent();
            var fileBytes = await File.ReadAllBytesAsync(fullSkinPath, cancellationToken).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "file", Path.GetFileName(fullSkinPath));
            form.Add(new StringContent(isSlimModel ? "slim" : "classic"), "variant");
            form.Add(new StringContent("unlisted"), "visibility");

            var requestedName = string.IsNullOrWhiteSpace(username)
                ? Path.GetFileNameWithoutExtension(fullSkinPath)
                : username.Trim();
            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                form.Add(new StringContent(requestedName[..Math.Min(requestedName.Length, 20)]), "name");
            }

            request.Content = form;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            if (!TryParseMineSkinCacheEntry(document.RootElement, out var entry))
            {
                return null;
            }

            SaveMineSkinCacheEntry(BuildMineSkinCacheKey(fullSkinPath, isSlimModel), entry);
            return entry;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeMineSkinApiKey(string? rawApiKey)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return null;
        }

        var trimmed = rawApiKey.Trim();
        const string bearerPrefix = "Bearer ";
        return trimmed.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[bearerPrefix.Length..].Trim()
            : trimmed;
    }

    private static bool TryParseMineSkinCacheEntry(JsonElement root, out MineSkinCacheEntry entry)
    {
        entry = null!;

        if (!root.TryGetProperty("skin", out var skinElement) ||
            !skinElement.TryGetProperty("texture", out var textureElement) ||
            !textureElement.TryGetProperty("data", out var textureDataElement))
        {
            return false;
        }

        var textureValue = textureDataElement.TryGetProperty("value", out var valueElement)
            ? valueElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(textureValue))
        {
            return false;
        }

        var textureSignature = textureDataElement.TryGetProperty("signature", out var signatureElement)
            ? signatureElement.GetString()
            : null;

        string? textureUrl = null;
        if (textureElement.TryGetProperty("url", out var urlElement) &&
            urlElement.ValueKind == JsonValueKind.Object &&
            urlElement.TryGetProperty("skin", out var skinUrlElement))
        {
            textureUrl = skinUrlElement.GetString();
        }

        entry = new MineSkinCacheEntry
        {
            TextureValue = textureValue,
            TextureSignature = textureSignature,
            TextureUrl = NormalizeTextureUrl(textureUrl),
            ProfileId = TryExtractProfileUuidFromTextureValue(textureValue),
            CachedAtUtc = DateTimeOffset.UtcNow
        };
        return true;
    }

    private static string? TryGetHostedSkinUrl(string fullSkinPath, bool preferDirectLoopback = false)
    {
        try
        {
            lock (LocalSkinHostLock)
            {
                LocalSkinHost ??= new LocalSkinHttpServer();
                var token = LocalSkinHost.SetSkinFile(fullSkinPath);
                if (preferDirectLoopback)
                {
                    return $"http://127.0.0.1:{LocalSkinHost.Port}/skin/{token}.png";
                }

                // Hosts-file DNS overrides break multiplayer name resolution.
                // Keep the fallback transport on loopback and advertise it in Vesper auth metadata.
                return $"http://127.0.0.1:{LocalSkinHost.Port}/skin/{token}.png";
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldUseDirectLoopbackSkinHost(string? versionId)
    {
        var normalizedVersionId = TryExtractLikelyMinecraftVersionId(versionId);
        if (string.IsNullOrWhiteSpace(normalizedVersionId))
        {
            return false;
        }

        return Version.TryParse(normalizedVersionId, out var parsedVersion) &&
               parsedVersion <= new Version(1, 16, 5);
    }

    private static int? TryGetLocalSkinProxyPort()
    {
        lock (LocalSkinHostLock)
        {
            return LocalSkinHost?.Port;
        }
    }

    private static string? TryPrepareSkinHostsOverrideFile(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        try
        {
            var launcherDirectory = Path.Combine(gameDirectory, ".launcher");
            Directory.CreateDirectory(launcherDirectory);
            var hostsOverridePath = Path.Combine(launcherDirectory, "skin-hosts.override");
            var hostsLines = BuildSkinHostsOverrideLines(gameDirectory);
            var hostsContent = string.Join(Environment.NewLine, hostsLines) + Environment.NewLine;
            File.WriteAllText(hostsOverridePath, hostsContent, Encoding.ASCII);
            return hostsOverridePath;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> BuildSkinHostsOverrideLines(string gameDirectory)
    {
        var mappings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1 textures.minecraft.net",
            "::1 textures.minecraft.net"
        };

        foreach (var host in EnumerateSavedServerHosts(gameDirectory))
        {
            try
            {
                foreach (var address in System.Net.Dns.GetHostAddresses(host))
                {
                    if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    {
                        continue;
                    }

                    mappings.Add($"{address} {host}");
                }
            }
            catch
            {
                // Keep the skin override working even if one saved server can't be resolved right now.
            }
        }

        return mappings.OrderBy(line => line, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateSavedServerHosts(string gameDirectory)
    {
        var seenHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var serversDatPath in EnumerateSavedServerDataFiles(gameDirectory))
        {
            foreach (var host in ExtractHostsFromServersDat(serversDatPath))
            {
                if (seenHosts.Add(host))
                {
                    yield return host;
                }
            }
        }

        foreach (var optionsPath in EnumerateSavedServerOptionFiles(gameDirectory))
        {
            foreach (var host in ExtractHostsFromOptionsFile(optionsPath))
            {
                if (seenHosts.Add(host))
                {
                    yield return host;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSavedServerDataFiles(string gameDirectory)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedGameDirectory = Path.GetFullPath(gameDirectory);

        foreach (var fileName in new[] { "servers.dat", "servers.dat_old" })
        {
            candidates.Add(Path.Combine(normalizedGameDirectory, fileName));

            var sharedGameDirectory = Path.GetFullPath(Path.Combine(normalizedGameDirectory, "..", ".."));
            candidates.Add(Path.Combine(sharedGameDirectory, fileName));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateSavedServerOptionFiles(string gameDirectory)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedGameDirectory = Path.GetFullPath(gameDirectory);
        var sharedGameDirectory = Path.GetFullPath(Path.Combine(normalizedGameDirectory, "..", ".."));

        candidates.Add(Path.Combine(normalizedGameDirectory, "options.txt"));
        candidates.Add(Path.Combine(sharedGameDirectory, "options.txt"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExtractHostsFromServersDat(string serversDatPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(serversDatPath);
        }
        catch
        {
            yield break;
        }

        for (var index = 0; index <= data.Length - 7; index++)
        {
            if (data[index] != 0x08 ||
                data[index + 1] != 0x00 ||
                data[index + 2] != 0x02 ||
                data[index + 3] != (byte)'i' ||
                data[index + 4] != (byte)'p')
            {
                continue;
            }

            var length = (data[index + 5] << 8) | data[index + 6];
            if (length <= 0 || index + 7 + length > data.Length)
            {
                continue;
            }

            string endpoint;
            try
            {
                endpoint = Encoding.UTF8.GetString(data, index + 7, length);
            }
            catch
            {
                continue;
            }

            var host = NormalizeMinecraftServerHost(endpoint);
            if (!string.IsNullOrWhiteSpace(host))
            {
                yield return host;
            }

            index += 6 + length;
        }
    }

    private static IEnumerable<string> ExtractHostsFromOptionsFile(string optionsPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(optionsPath);
        }
        catch
        {
            yield break;
        }

        foreach (var prefix in new[] { "lastServer:", "lastMpIp:" })
        {
            var rawValue = lines
                .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (rawValue is null)
            {
                continue;
            }

            var host = NormalizeMinecraftServerHost(rawValue[prefix.Length..]);
            if (!string.IsNullOrWhiteSpace(host))
            {
                yield return host;
            }
        }
    }

    private static string? NormalizeMinecraftServerHost(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var value = endpoint.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket > 1)
            {
                return value[1..closingBracket];
            }
        }

        var lastColon = value.LastIndexOf(':');
        if (lastColon > 0 &&
            lastColon < value.Length - 1 &&
            value.Count(ch => ch == ':') == 1 &&
            int.TryParse(value[(lastColon + 1)..], out _))
        {
            value = value[..lastColon];
        }

        value = value.TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void TryClearSkinAssetCache(string assetsDirectory)
    {
        if (string.IsNullOrWhiteSpace(assetsDirectory))
        {
            return;
        }

        try
        {
            var skinCacheDirectory = Path.Combine(assetsDirectory, "skins");
            if (!Directory.Exists(skinCacheDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(skinCacheDirectory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // ignore individual cache entries
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(skinCacheDirectory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                }
                catch
                {
                    // ignore stale directories
                }
            }
        }
        catch
        {
            // ignore cache cleanup failures
        }
    }

    private static void EnsureSavedServerBypassRoutes(string gameDirectory)
    {
        var routeTarget = TryGetPreferredServerRouteTarget();
        if (routeTarget is null)
        {
            return;
        }

        foreach (var host in EnumerateSavedServerHosts(gameDirectory))
        {
            try
            {
                foreach (var address in System.Net.Dns.GetHostAddresses(host))
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork ||
                        !IsUsableIpv4RouteAddress(address))
                    {
                        continue;
                    }

                    EnsureHostBypassRoute(address, routeTarget.Value);
                }
            }
            catch
            {
                // ignore DNS/route issues for individual servers
            }
        }
    }

    private static PreferredServerRouteTarget? TryGetPreferredServerRouteTarget()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsPreferredServerRouteInterface)
                .Select(CreatePreferredServerRouteTarget)
                .Where(target => target != null)
                .Select(target => target!.Value)
                .OrderByDescending(target => target.Priority)
                .ThenByDescending(target => target.InterfaceSpeed)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPreferredServerRouteInterface(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        var descriptor = $"{networkInterface.Name} {networkInterface.Description}".ToLowerInvariant();
        foreach (var marker in new[]
                 {
                     "wireguard", "wintun", "vpn", "tunnel", "tunnelbear", "radmin",
                     "tailscale", "zerotier", "hamachi", "tap", "tun", "virtual"
                 })
        {
            if (descriptor.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }
        }

        try
        {
            var properties = networkInterface.GetIPProperties();
            var ipv4Properties = properties.GetIPv4Properties();
            if (ipv4Properties is null)
            {
                return false;
            }

            var hasGateway = properties.GatewayAddresses.Any(entry => IsUsableIpv4RouteAddress(entry.Address));
            var hasLocalAddress = properties.UnicastAddresses.Any(entry => IsUsableIpv4RouteAddress(entry.Address));
            return hasGateway && hasLocalAddress;
        }
        catch
        {
            return false;
        }
    }

    private static PreferredServerRouteTarget? CreatePreferredServerRouteTarget(NetworkInterface networkInterface)
    {
        try
        {
            var properties = networkInterface.GetIPProperties();
            var ipv4Properties = properties.GetIPv4Properties();
            if (ipv4Properties is null)
            {
                return null;
            }

            var gatewayAddress = properties.GatewayAddresses
                .Select(entry => entry.Address)
                .FirstOrDefault(IsUsableIpv4RouteAddress);
            var localAddress = properties.UnicastAddresses
                .Select(entry => entry.Address)
                .FirstOrDefault(IsUsableIpv4RouteAddress);

            if (gatewayAddress is null || localAddress is null)
            {
                return null;
            }

            var priority = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 : 1;
            return new PreferredServerRouteTarget(
                gatewayAddress.ToString(),
                ipv4Properties.Index,
                networkInterface.Speed,
                priority);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsableIpv4RouteAddress(System.Net.IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (System.Net.IPAddress.Any.Equals(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static void EnsureHostBypassRoute(System.Net.IPAddress address, PreferredServerRouteTarget routeTarget)
    {
        var hostAddress = address.ToString();
        var addSucceeded = TryRunRouteCommand(
            $"ADD {hostAddress} MASK 255.255.255.255 {routeTarget.GatewayAddress} IF {routeTarget.InterfaceIndex} METRIC 1");
        if (!addSucceeded)
        {
            TryRunRouteCommand(
                $"CHANGE {hostAddress} MASK 255.255.255.255 {routeTarget.GatewayAddress} IF {routeTarget.InterfaceIndex} METRIC 1");
        }
    }

    private static bool TryRunRouteCommand(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "route.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct PreferredServerRouteTarget(
        string GatewayAddress,
        int InterfaceIndex,
        long InterfaceSpeed,
        int Priority);

    private sealed class LocalSkinHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _listenTask;
        private readonly object _stateLock = new();
        private string? _activeSkinPath;
        private string? _activeToken;

        public LocalSkinHttpServer()
        {
            _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _listenTask = Task.Run(ListenLoopAsync);
        }

        public int Port { get; }

        public string SetSkinFile(string skinPath)
        {
            var fullPath = Path.GetFullPath(skinPath);
            var token = Guid.NewGuid().ToString("N");
            lock (_stateLock)
            {
                _activeSkinPath = fullPath;
                _activeToken = token;
            }

            return token;
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    client?.Dispose();
                    break;
                }
                catch
                {
                    client?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                while (true)
                {
                    var headerLine = await reader.ReadLineAsync(cancellationToken);
                    if (headerLine is null || headerLine.Length == 0)
                    {
                        break;
                    }
                }

                var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestParts.Length < 2)
                {
                    await WriteResponseAsync(stream, statusCode: 405, statusText: "Method Not Allowed", contentType: "text/plain", body: Array.Empty<byte>(), cancellationToken);
                    return;
                }

                var method = requestParts[0].Trim();
                var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
                var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                if (!isGet && !isHead)
                {
                    await WriteResponseAsync(stream, statusCode: 405, statusText: "Method Not Allowed", contentType: "text/plain", body: Array.Empty<byte>(), cancellationToken);
                    return;
                }

                var rawTarget = requestParts[1].Trim();
                var rawPath = rawTarget;
                if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteTarget))
                {
                    rawPath = absoluteTarget.AbsolutePath;
                }

                var querySeparatorIndex = rawPath.IndexOfAny(['?', '#']);
                if (querySeparatorIndex >= 0)
                {
                    rawPath = rawPath[..querySeparatorIndex];
                }

                if (!rawPath.StartsWith("/skin/", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, statusCode: 404, statusText: "Not Found", contentType: "text/plain", body: Array.Empty<byte>(), cancellationToken);
                    return;
                }

                var tokenWithExtension = Uri.UnescapeDataString(rawPath["/skin/".Length..]);
                if (!tokenWithExtension.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, statusCode: 404, statusText: "Not Found", contentType: "text/plain", body: Array.Empty<byte>(), cancellationToken);
                    return;
                }

                var token = tokenWithExtension[..^4];
                string? currentToken;
                string? currentSkinPath;
                lock (_stateLock)
                {
                    currentToken = _activeToken;
                    currentSkinPath = _activeSkinPath;
                }

                if (!string.Equals(token, currentToken, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(currentSkinPath) ||
                    !File.Exists(currentSkinPath))
                {
                    await WriteResponseAsync(stream, statusCode: 404, statusText: "Not Found", contentType: "text/plain", body: Array.Empty<byte>(), cancellationToken);
                    return;
                }

                var body = isHead
                    ? Array.Empty<byte>()
                    : await File.ReadAllBytesAsync(currentSkinPath, cancellationToken);
                await WriteResponseAsync(stream, statusCode: 200, statusText: "OK", contentType: "image/png", body: body, cancellationToken);
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            string statusText,
            string contentType,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var header =
                $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                "Connection: close\r\n" +
                $"Content-Type: {contentType}\r\n" +
                "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, cancellationToken);
            if (body.Length > 0)
            {
                await stream.WriteAsync(body, cancellationToken);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static IReadOnlyList<string> BuildJvmArguments(
        JsonElement versionRoot,
        LaunchOptions options,
        IReadOnlyDictionary<string, string> replacements,
        int javaMajorVersion,
        string classpath,
        string nativesDirectory,
        string gameDirectory,
        string javaTemporaryDirectory,
        string? authlibInjectorPath,
        VesperSkinBridgePlan skinBridgePlan)
    {
        var args = new List<string>();

        var jvmPlan = new JvmArgBuilder().Build(new JvmArgBuildRequest(
            options.Version.Id,
            options.MemoryMb,
            javaMajorVersion));
        args.AddRange(jvmPlan.Arguments);

        var hasJvmArgsFromMetadata = false;
        if (versionRoot.TryGetProperty("arguments", out var argumentsElement) &&
            argumentsElement.TryGetProperty("jvm", out var jvmElement) &&
            jvmElement.ValueKind == JsonValueKind.Array)
        {
            var metadataJvmArgs = new List<string>(ParseArgumentArray(jvmElement, replacements));
            RemoveUnsupportedModuleJvmArguments(metadataJvmArgs, javaMajorVersion);
            args.AddRange(metadataJvmArgs);
            hasJvmArgsFromMetadata = metadataJvmArgs.Count > 0;
        }

        if (!string.IsNullOrWhiteSpace(javaTemporaryDirectory))
        {
            args.RemoveAll(arg => arg.StartsWith("-Djava.io.tmpdir=", StringComparison.OrdinalIgnoreCase));
            args.Add($"-Djava.io.tmpdir={javaTemporaryDirectory}");
        }

        // Never replace the JVM hosts file for skins: it breaks DNS for multiplayer servers
        // and causes UnknownHost/endless connecting on arbitrary server addresses.

        // Java 9+ compatibility for versions that need module opens/exports.
        args.Add("-XX:+IgnoreUnrecognizedVMOptions");
        if (javaMajorVersion >= 9)
        {
            args.Add("--add-exports=java.base/sun.security.util=ALL-UNNAMED");
            args.Add("--add-exports=jdk.naming.dns/com.sun.jndi.dns=java.naming");
            args.Add("--add-opens=java.base/java.util.jar=ALL-UNNAMED");
            args.Add("--add-opens=java.base/java.lang=ALL-UNNAMED");
            args.Add("--add-opens=java.base/java.lang.reflect=ALL-UNNAMED");
            args.Add("--add-opens=java.base/java.net=ALL-UNNAMED");
            args.Add("--add-opens=java.base/java.util=ALL-UNNAMED");
        }
        
        // For 1.16.5 offline mode, we need to ensure the multiplayer button is enabled.
        // If we have Ely.by Authlib, it usually handles this, but we'll add the force flag.
        if (options.MicrosoftSession == null && options.Version.Id.Contains("1.16.5", StringComparison.OrdinalIgnoreCase))
        {
            // Omit custom hosts for now and let the library use its internal defaults, 
            // similar to Neonar Client which works without them.
        }

        // Force multiplayer button enabled
        args.Add("-Dcom.mojang.minecraft.multiplayer=true");

        if (skinBridgePlan.Enabled && options.VesperAuthSession != null)
        {
            args.Add("-Dely.authlib.fetchMissingSkinsByUsername=true");
            args.Add("-Dely.authlib.skipValidatePropertySignature=true");
            args.Add($"-Dely.authlib.offlineProfileLookupUrl={BuildVesperElyProfileLookupUrl(options.VesperAuthSession!.ApiBaseUrl)}");
        }

        if (options.VesperAuthSession != null && !string.IsNullOrWhiteSpace(authlibInjectorPath))
        {
            var javaAgentPath = GetJavaCompatiblePath(authlibInjectorPath);
            args.Add("-Dauthlibinjector.noLogFile");
            args.Add("-Dauthlibinjector.noShowServerName");
            args.Add("-Dauthlibinjector.mojangAntiFeatures=enabled");
            args.Add("-Dauthlibinjector.profileKey=disabled");
            args.Add("-Dauthlibinjector.usernameCheck=disabled");
            args.Add($"-javaagent:{javaAgentPath}={options.VesperAuthSession.ApiBaseUrl}");
        }

        if (!hasJvmArgsFromMetadata)
        {
            args.Add($"-Djava.library.path={nativesDirectory}");
            args.Add("-cp");
            args.Add(classpath);
        }

        if (options.Profile == LauncherProfile.CheatClient)
        {
            args.Add("-Dcheatcraft.profile=cheat");
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraJvmArgs))
        {
            args.AddRange(SplitCommandLine(options.ExtraJvmArgs));
        }

        return args;
    }

    private static (int InitialMb, int MaximumMb) ResolveEffectiveJvmMemorySettings(
        LaunchOptions options,
        int javaMajorVersion)
    {
        var requestedMaximumMb = Math.Max(1024, options.MemoryMb);
        var effectiveMaximumMb = requestedMaximumMb;

        var installedSystemMemoryMb = TryGetInstalledSystemMemoryMb();
        if (installedSystemMemoryMb.HasValue)
        {
            var totalMb = installedSystemMemoryMb.Value;
            var reservedForSystemMb = totalMb <= 6144 ? 1536 : 2048;
            var safeMaximumMb = Math.Max(1024, totalMb - reservedForSystemMb);
            effectiveMaximumMb = Math.Min(effectiveMaximumMb, safeMaximumMb);
        }

        if (javaMajorVersion > 0 &&
            javaMajorVersion <= 8 &&
            RequiresLegacyJava(options.Version.Id))
        {
            // Leave more native headroom for legacy Java 8 render paths.
            // This prevents OptiFine/Fabric 1.16.x from exhausting direct/native memory
            // while loading chunks, shaders, and block models.
            effectiveMaximumMb = Math.Min(requestedMaximumMb, 3072);
        }

        effectiveMaximumMb = Math.Max(1024, effectiveMaximumMb);
        var effectiveInitialMb = Math.Clamp(effectiveMaximumMb / 2, 1024, effectiveMaximumMb);
        effectiveInitialMb = Math.Clamp(effectiveInitialMb, 512, effectiveMaximumMb);
        return (effectiveInitialMb, effectiveMaximumMb);
    }

    private static IReadOnlyList<string> BuildGameArguments(
        JsonElement versionRoot,
        LaunchOptions options,
        IReadOnlyDictionary<string, string> replacements)
    {
        var launchFeatureFlags = BuildLaunchFeatureFlags(replacements);
        var supportsQuickPlayMultiplayer = SupportsQuickPlayMultiplayer(versionRoot);
        List<string> result;

        if (versionRoot.TryGetProperty("arguments", out var argumentsElement) &&
            argumentsElement.TryGetProperty("game", out var gameElement) &&
            gameElement.ValueKind == JsonValueKind.Array)
        {
            result = new List<string>(ParseArgumentArray(gameElement, replacements, launchFeatureFlags));
        }
        else if (versionRoot.TryGetProperty("minecraftArguments", out var legacyArgumentsElement) &&
            legacyArgumentsElement.ValueKind == JsonValueKind.String)
        {
            var legacyArgs = ReplacePlaceholders(legacyArgumentsElement.GetString() ?? string.Empty, replacements);
            result = new List<string>(SplitCommandLine(legacyArgs));
        }
        else
        {
            result = new List<string>();
        }

        var hasCustomUserProperties = replacements.TryGetValue("user_properties", out var userPropertiesValue) &&
                                      !string.Equals(userPropertiesValue, "{}", StringComparison.Ordinal);
        if ((hasCustomUserProperties ||
             (replacements.TryGetValue("user_type", out var ut) && ut != "legacy")) &&
            !result.Contains("--userProperties", StringComparer.OrdinalIgnoreCase))
        {
            result.Add("--userProperties");
            result.Add(hasCustomUserProperties ? userPropertiesValue! : "{}");
        }

        if (hasCustomUserProperties &&
            !result.Contains("--profileProperties", StringComparer.OrdinalIgnoreCase))
        {
            result.Add("--profileProperties");
            result.Add(userPropertiesValue!);
        }

        var hasQuickPlayMultiplayer = supportsQuickPlayMultiplayer &&
                                      launchFeatureFlags.TryGetValue("is_quick_play_multiplayer", out var isQuickPlayMultiplayer) &&
                                      isQuickPlayMultiplayer;

        if (!hasQuickPlayMultiplayer &&
            !string.IsNullOrWhiteSpace(options.DirectConnectServerAddress))
        {
            result.Add("--server");
            result.Add(options.DirectConnectServerAddress.Trim());

            if (options.DirectConnectServerPort is int port &&
                port > 0 &&
                port <= 65535)
            {
                result.Add("--port");
                result.Add(port.ToString(CultureInfo.InvariantCulture));
            }
        }

        return result;
    }

    private static bool SupportsQuickPlayMultiplayer(JsonElement versionRoot)
    {
        if (!versionRoot.TryGetProperty("arguments", out var argumentsElement) ||
            !argumentsElement.TryGetProperty("game", out var gameElement))
        {
            return false;
        }

        return gameElement.GetRawText().Contains("quickPlayMultiplayer", StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, bool> BuildLaunchFeatureFlags(
        IReadOnlyDictionary<string, string> replacements)
    {
        var hasQuickPlayMultiplayer =
            replacements.TryGetValue("quickPlayMultiplayer", out var quickPlayMultiplayer) &&
            !string.IsNullOrWhiteSpace(quickPlayMultiplayer);

        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = true,
            ["has_quick_plays_support"] = hasQuickPlayMultiplayer,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = hasQuickPlayMultiplayer,
            ["is_quick_play_realms"] = false
        };
    }

    private static void RemoveUnsupportedModuleJvmArguments(List<string> args, int javaMajorVersion)
    {
        if (javaMajorVersion >= 9 || args.Count == 0)
        {
            return;
        }

        for (var index = args.Count - 1; index >= 0; index--)
        {
            var current = args[index];
            if (IsSingleTokenModuleJvmArgument(current))
            {
                args.RemoveAt(index);
                continue;
            }

            if (!IsModuleJvmArgumentWithSeparateValue(current))
            {
                continue;
            }

            args.RemoveAt(index);
            if (index < args.Count)
            {
                args.RemoveAt(index);
            }
        }
    }

    private static bool IsSingleTokenModuleJvmArgument(string arg)
    {
        return arg.StartsWith("--add-opens=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--add-exports=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--add-reads=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--add-modules=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--module-path=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--upgrade-module-path=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--limit-modules=", StringComparison.OrdinalIgnoreCase) ||
               arg.StartsWith("--patch-module=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModuleJvmArgumentWithSeparateValue(string arg)
    {
        return arg.Equals("--add-opens", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--add-exports", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--add-reads", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--add-modules", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--module-path", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--upgrade-module-path", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--limit-modules", StringComparison.OrdinalIgnoreCase) ||
               arg.Equals("--patch-module", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseArgumentArray(
        JsonElement arrayElement,
        IReadOnlyDictionary<string, string> replacements,
        IReadOnlyDictionary<string, bool>? featureFlags = null)
    {
        var result = new List<string>();

        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(ReplacePlaceholders(item.GetString() ?? string.Empty, replacements));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object || !EvaluateRules(item, featureFlags))
            {
                continue;
            }

            if (!item.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            if (valueElement.ValueKind == JsonValueKind.String)
            {
                result.Add(ReplacePlaceholders(valueElement.GetString() ?? string.Empty, replacements));
                continue;
            }

            if (valueElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var argPart in valueElement.EnumerateArray())
            {
                if (argPart.ValueKind == JsonValueKind.String)
                {
                    result.Add(ReplacePlaceholders(argPart.GetString() ?? string.Empty, replacements));
                }
            }
        }

        return result;
    }

    private static bool EvaluateRules(
        JsonElement jsonObject,
        IReadOnlyDictionary<string, bool>? featureFlags = null)
    {
        if (!jsonObject.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var allow = false;
        foreach (var rule in rulesElement.EnumerateArray())
        {
            if (!RuleMatchesCurrentRuntime(rule, featureFlags))
            {
                continue;
            }

            var action = rule.GetProperty("action").GetString() ?? "disallow";
            allow = action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }

        return allow;
    }

    private static bool RuleMatchesCurrentRuntime(
        JsonElement rule,
        IReadOnlyDictionary<string, bool>? featureFlags = null)
    {
        if (rule.TryGetProperty("os", out var osElement) && osElement.ValueKind == JsonValueKind.Object)
        {
            if (osElement.TryGetProperty("name", out var osNameElement) && !MatchesOperatingSystem(osNameElement.GetString()))
            {
                return false;
            }

            if (osElement.TryGetProperty("arch", out var archElement) && !MatchesArchitecture(archElement.GetString()))
            {
                return false;
            }

            if (osElement.TryGetProperty("version", out var versionPatternElement) && versionPatternElement.ValueKind == JsonValueKind.String)
            {
                var pattern = versionPatternElement.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pattern) &&
                    !Regex.IsMatch(Environment.OSVersion.VersionString, pattern, RegexOptions.IgnoreCase))
                {
                    return false;
                }
            }
        }

        if (!rule.TryGetProperty("features", out var featuresElement) || featuresElement.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var feature in featuresElement.EnumerateObject())
        {
            if (feature.Value.ValueKind != JsonValueKind.True && feature.Value.ValueKind != JsonValueKind.False)
            {
                return false;
            }

            var expected = feature.Value.GetBoolean();
            if (!FeatureRuleMatches(feature.Name, expected, featureFlags))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FeatureRuleMatches(
        string featureName,
        bool expectedValue,
        IReadOnlyDictionary<string, bool>? featureFlags = null)
    {
        if (featureFlags is not null &&
            featureFlags.TryGetValue(featureName, out var contextualValue))
        {
            return contextualValue == expectedValue;
        }

        // Supported launcher features. Everything unknown is treated as false.
        var actualValue = featureName switch
        {
            "is_demo_user" => false,
            "has_custom_resolution" => true,
            "has_quick_plays_support" => false,
            "is_quick_play_singleplayer" => false,
            "is_quick_play_multiplayer" => false,
            "is_quick_play_realms" => false,
            _ => false
        };

        return actualValue == expectedValue;
    }

    private static bool MatchesOperatingSystem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        return name.ToLowerInvariant() switch
        {
            "windows" => OperatingSystem.IsWindows(),
            "linux" => OperatingSystem.IsLinux(),
            "osx" or "mac" => OperatingSystem.IsMacOS(),
            _ => false
        };
    }

    private static bool MatchesArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return true;
        }

        var normalized = architecture.ToLowerInvariant();
        return normalized switch
        {
            "x86" => !Environment.Is64BitOperatingSystem,
            "x64" or "amd64" => Environment.Is64BitOperatingSystem,
            _ => true
        };
    }

    private static string ResolveWindowsNativeClassifierKey(JsonElement nativesElement)
    {
        if (!nativesElement.TryGetProperty("windows", out var windowsElement) || windowsElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var value = windowsElement.GetString() ?? string.Empty;
        if (value.Contains("${arch}", StringComparison.Ordinal))
        {
            value = value.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.Ordinal);
        }

        return value;
    }

    private static string ResolveJavaExecutable(string javaExecutable, string versionId)
    {
        var normalized = string.IsNullOrWhiteSpace(javaExecutable) ? "javaw" : Environment.ExpandEnvironmentVariables(javaExecutable.Trim());
        var isExplicitPath = normalized.Contains('\\') || normalized.Contains('/');

        if (isExplicitPath && !File.Exists(normalized))
        {
            throw new FileNotFoundException("Файл java не найден.", normalized);
        }

        if (RequiresLegacyJava(versionId))
        {
            if (isExplicitPath)
            {
                var explicitMajor = GetJavaMajorVersion(normalized);
                if (explicitMajor <= 0 || explicitMajor > 21)
                {
                    var explicitFallback = TryResolveCompatibleJavaForLegacyVersions();
                    if (!string.IsNullOrWhiteSpace(explicitFallback))
                    {
                        return explicitFallback;
                    }
                }
            }

            if (IsSimpleJavaAlias(normalized))
            {
                var compatibleJava = TryResolveCompatibleJavaForLegacyVersions();
                if (!string.IsNullOrWhiteSpace(compatibleJava))
                {
                    return compatibleJava;
                }
            }
        }

        return normalized;
    }

    private static async Task<string> ResolveJavaExecutableAsync(
        string javaExecutable,
        JsonElement versionRoot,
        string versionId,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requiredMajor = ResolveRequiredJavaMajor(versionRoot, versionId);
        var normalized = ResolveJavaExecutable(javaExecutable, versionId);

        var explicitOrAliasPath = ResolveJavaPathFromInput(normalized);
        var preferInstalledJavaOverExplicitPath =
            !string.IsNullOrWhiteSpace(explicitOrAliasPath) &&
            IsOracleJavaPathLauncher(explicitOrAliasPath);

        if (!string.IsNullOrWhiteSpace(explicitOrAliasPath))
        {
            var explicitMajor = GetJavaMajorVersion(explicitOrAliasPath);
            if (!preferInstalledJavaOverExplicitPath &&
                IsJavaMajorCompatible(explicitMajor, requiredMajor, versionId))
            {
                return explicitOrAliasPath;
            }
        }

        var installedMatch = FindInstalledJavaForVersion(requiredMajor, versionId);
        if (!string.IsNullOrWhiteSpace(installedMatch))
        {
            return installedMatch;
        }

        if (!string.IsNullOrWhiteSpace(explicitOrAliasPath))
        {
            var explicitMajor = GetJavaMajorVersion(explicitOrAliasPath);
            if (IsJavaMajorCompatible(explicitMajor, requiredMajor, versionId))
            {
                return explicitOrAliasPath;
            }
        }

        var javaStage = $"Скачиваю Java {requiredMajor} для версии {versionId}...";
        progress?.Report(new LauncherProgress(javaStage, 0, 1));
        var downloaded = await EnsureDownloadedJavaRuntimeAsync(
            requiredMajor,
            cancellationToken,
            CreateStageDownloadProgressReporter(progress, javaStage, 0, 1, 1));
        return downloaded;
    }

    private static int ResolveRequiredJavaMajor(JsonElement versionRoot, string versionId)
    {
        if (versionRoot.TryGetProperty("javaVersion", out var javaVersionElement) &&
            javaVersionElement.ValueKind == JsonValueKind.Object &&
            javaVersionElement.TryGetProperty("majorVersion", out var majorElement) &&
            majorElement.TryGetInt32(out var majorVersion) &&
            majorVersion > 0)
        {
            return majorVersion;
        }

        var baseVersionId = TryDetermineBaseMinecraftVersionId(versionId, versionRoot) ?? versionId;
        if (TryExtractMinecraftVersion(baseVersionId, out var minecraftVersion))
        {
            var mapped = MapRequiredJavaMajor(minecraftVersion);
            if (mapped.HasValue)
            {
                return mapped.Value;
            }
        }

        return RequiresLegacyJava(baseVersionId) ? 8 : 17;
    }

    private static bool IsJavaMajorCompatible(int installedMajor, int requiredMajor, string versionId)
    {
        if (installedMajor <= 0)
        {
            return false;
        }

        return ResolveCompatibleJavaMajors(requiredMajor, versionId).Contains(installedMajor);
    }

    private static string? ResolveJavaPathFromInput(string javaInput)
    {
        if (string.IsNullOrWhiteSpace(javaInput))
        {
            return null;
        }

        var normalized = Environment.ExpandEnvironmentVariables(javaInput.Trim());
        var isExplicitPath = normalized.Contains('\\') || normalized.Contains('/');
        if (isExplicitPath)
        {
            return File.Exists(normalized) ? normalized : null;
        }

        if (!IsSimpleJavaAlias(normalized))
        {
            return File.Exists(normalized) ? normalized : null;
        }

        foreach (var candidate in ResolveExecutableFromPath(normalized))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindInstalledJavaForVersion(int requiredMajor, string versionId)
    {
        var candidates = EnumerateJavaExecutableCandidates()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { Path = path, Major = GetJavaMajorVersion(path) })
            .Where(item => IsJavaMajorCompatible(item.Major, requiredMajor, versionId))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var compatibleMajor in ResolveCompatibleJavaMajors(requiredMajor, versionId))
        {
            var preferred = candidates.FirstOrDefault(item =>
                item.Major == compatibleMajor &&
                !IsOracleJavaPathLauncher(item.Path));
            if (preferred is not null)
            {
                return preferred.Path;
            }

            preferred = candidates.FirstOrDefault(item => item.Major == compatibleMajor);
            if (preferred is not null)
            {
                return preferred.Path;
            }
        }

        var exact = candidates.FirstOrDefault(item => item.Major == requiredMajor);
        if (exact is not null && !IsOracleJavaPathLauncher(exact.Path))
        {
            return exact.Path;
        }

        var nonOracleCandidate = candidates
            .Where(item => !IsOracleJavaPathLauncher(item.Path))
            .OrderByDescending(item => item.Major)
            .FirstOrDefault();
        if (nonOracleCandidate is not null)
        {
            return nonOracleCandidate.Path;
        }

        return exact?.Path ?? candidates.OrderByDescending(item => item.Major).First().Path;
    }

    private static bool IsOracleJavaPathLauncher(string? javaExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(javaExecutablePath.Trim()));
            return fullPath.Contains(@"\Common Files\Oracle\Java\javapath", StringComparison.OrdinalIgnoreCase) ||
                   fullPath.Contains(@"\Common Files\Oracle\Java\javapath_target_", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<int> ResolveCompatibleJavaMajors(int requiredMajor, string versionId)
    {
        if (TryExtractMinecraftVersion(versionId, out var minecraftVersion))
        {
            if (minecraftVersion.Major == 1 && minecraftVersion.Minor <= 16)
            {
                return [8];
            }

            if (minecraftVersion.Major == 1 && minecraftVersion.Minor == 17)
            {
                return [16];
            }

            if (minecraftVersion.Major == 1 && minecraftVersion.Minor == 20 && minecraftVersion.Build >= 5)
            {
                return [21];
            }

            if (minecraftVersion.Major == 1 && minecraftVersion.Minor >= 21)
            {
                return [21];
            }

            if (minecraftVersion.Major == 1 && minecraftVersion.Minor >= 18)
            {
                return [17];
            }
        }

        return requiredMajor > 0 ? [requiredMajor] : [];
    }

    private static async Task<string> EnsureDownloadedJavaRuntimeAsync(
        int majorVersion,
        CancellationToken cancellationToken,
        Action<long, long?>? progressCallback = null)
    {
        var arch = GetAdoptiumArchitecture();
        var runtimeId = $"temurin-{majorVersion}-{arch}";
        var runtimesRoot = Path.Combine(BaseStorageDirectory.Value, "java-runtimes");
        var runtimeDirectory = Path.Combine(runtimesRoot, runtimeId);

        var existing = FindJavaExecutableInDirectory(runtimeDirectory, majorVersion);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        await JavaRuntimeInstallLock.WaitAsync(cancellationToken);
        try
        {
            existing = FindJavaExecutableInDirectory(runtimeDirectory, majorVersion);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            Directory.CreateDirectory(runtimesRoot);
            EnsureSufficientFreeSpace(runtimesRoot, 768L * 1024 * 1024, $"установки Java {majorVersion}");

            var archivePath = Path.Combine(runtimesRoot, $"{runtimeId}.zip");
            var extractDirectory = Path.Combine(runtimesRoot, $"{runtimeId}.extract");
            TryDeleteFileQuietly(archivePath);
            TryDeleteDirectoryQuietly(extractDirectory);

            try
            {
                await DownloadJavaRuntimeArchiveAsync(majorVersion, arch, archivePath, cancellationToken, progressCallback);

                Directory.CreateDirectory(extractDirectory);
                ZipFile.ExtractToDirectory(archivePath, extractDirectory, overwriteFiles: true);

                if (Directory.Exists(runtimeDirectory))
                {
                    Directory.Delete(runtimeDirectory, recursive: true);
                }

                Directory.Move(extractDirectory, runtimeDirectory);
                TryDeleteFileQuietly(archivePath);

                var downloaded = FindJavaExecutableInDirectory(runtimeDirectory, majorVersion);
                if (string.IsNullOrWhiteSpace(downloaded))
                {
                    throw new InvalidDataException($"Не удалось найти javaw.exe после установки Java {majorVersion}.");
                }

                return downloaded;
            }
            catch
            {
                TryDeleteFileQuietly(archivePath);
                TryDeleteDirectoryQuietly(extractDirectory);
                throw;
            }
        }
        finally
        {
            JavaRuntimeInstallLock.Release();
        }
    }

    private static async Task DownloadJavaRuntimeArchiveAsync(
        int majorVersion,
        string arch,
        string archivePath,
        CancellationToken cancellationToken,
        Action<long, long?>? progressCallback = null)
    {
        Exception? lastError = null;

        foreach (var candidateUrl in EnumerateJavaRuntimeDownloadUrls(majorVersion, arch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFileWithRetriesAsync(
                    candidateUrl,
                    archivePath,
                    null,
                    cancellationToken,
                    progressCallback,
                    maxAttempts: 4);
                var archiveInfo = new FileInfo(archivePath);
                if (archiveInfo.Exists && archiveInfo.Length > 0)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                TryDeleteFileQuietly(archivePath);
            }
        }

        throw new IOException(
            $"Не удалось скачать Java {majorVersion} для архитектуры {arch}.",
            lastError);
    }

    private static IEnumerable<string> EnumerateJavaRuntimeDownloadUrls(int majorVersion, string arch)
    {
        if (majorVersion != 16)
        {
            yield return $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/windows/{arch}/jre/hotspot/normal/eclipse";
        }

        yield return $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/windows/{arch}/jdk/hotspot/normal/eclipse";
    }

    private static string? FindJavaExecutableInDirectory(string directory, int requiredMajor)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var javawCandidates = Directory.EnumerateFiles(directory, "javaw.exe", SearchOption.AllDirectories).ToArray();
        foreach (var javaw in javawCandidates)
        {
            if (GetJavaMajorVersion(javaw) == requiredMajor)
            {
                return javaw;
            }
        }

        var javaCandidates = Directory.EnumerateFiles(directory, "java.exe", SearchOption.AllDirectories).ToArray();
        foreach (var java in javaCandidates)
        {
            if (GetJavaMajorVersion(java) == requiredMajor)
            {
                return java;
            }
        }

        // Some Windows 11 systems block probing a freshly extracted java.exe/javaw.exe
        // even though the runtime files are present and usable for the actual game launch.
        return FindBundledJavaFallback(javawCandidates, javaCandidates);
    }

    private static string? FindBundledJavaFallback(string[] javawCandidates, string[] javaCandidates)
    {
        static bool LooksLikeBundledJavaBinary(string path)
        {
            var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.EndsWith($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}javaw.exe", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}java.exe", StringComparison.OrdinalIgnoreCase);
        }

        return javawCandidates.FirstOrDefault(LooksLikeBundledJavaBinary) ??
               javawCandidates.FirstOrDefault() ??
               javaCandidates.FirstOrDefault(LooksLikeBundledJavaBinary) ??
               javaCandidates.FirstOrDefault();
    }

    private static string GetAdoptiumArchitecture()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x32",
            Architecture.Arm64 => "aarch64",
            _ => "x64"
        };
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
            // Ignore and fall back to the user-selected memory value.
        }

        return null;
    }

    private static bool RequiresLegacyJava(string versionId)
    {
        if (TryExtractMinecraftVersion(versionId, out var minecraftVersion))
        {
            return minecraftVersion.Major == 1 && minecraftVersion.Minor <= 16;
        }

        return versionId.Contains("1.16.5", StringComparison.Ordinal);
    }

    private static bool TryExtractMinecraftVersion(string versionId, out Version minecraftVersion)
    {
        minecraftVersion = new Version(0, 0);
        var candidate = TryExtractLikelyMinecraftVersionId(versionId);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!Version.TryParse(candidate, out var parsed) || parsed is null)
        {
            return false;
        }

        minecraftVersion = parsed;
        return true;
    }

    private static int? MapRequiredJavaMajor(Version minecraftVersion)
    {
        if (minecraftVersion.Major != 1)
        {
            return null;
        }

        var minor = minecraftVersion.Minor;
        var patch = minecraftVersion.Build >= 0 ? minecraftVersion.Build : 0;

        if (minor <= 16)
        {
            return 8;
        }

        if (minor == 17)
        {
            return 16;
        }

        if (minor == 20 && patch >= 5)
        {
            return 21;
        }

        if (minor >= 21)
        {
            return 21;
        }

        if (minor >= 18)
        {
            return 17;
        }

        return null;
    }

    private static void CleanupLauncherTemporaryData()
    {
        CleanupStaleDirectoryChildren(Path.Combine(BaseStorageDirectory.Value, ".launcher-temp"), LauncherTempRetention);
        CleanupStaleDirectoryChildren(Path.Combine(AppContext.BaseDirectory, ".launcher-updates"), UpdateCacheRetention);
    }

    private static void CleanupObsoleteJavaRuntimeArtifacts()
    {
        var runtimesRoot = Path.Combine(BaseStorageDirectory.Value, "java-runtimes");
        if (!Directory.Exists(runtimesRoot))
        {
            return;
        }

        foreach (var runtimeDirectory in new DirectoryInfo(runtimesRoot).GetDirectories())
        {
            var major = TryParseManagedJavaRuntimeMajor(runtimeDirectory.Name);
            if (major <= 0 || SupportedBundledJavaRuntimeMajors.Contains(major))
            {
                continue;
            }

            TryDeleteDirectoryQuietly(runtimeDirectory.FullName);
        }

        foreach (var runtimeArchivePath in Directory.EnumerateFiles(runtimesRoot, "*.zip", SearchOption.TopDirectoryOnly))
        {
            if (IsPathOlderThan(runtimeArchivePath, LauncherTempRetention))
            {
                TryDeleteFileQuietly(runtimeArchivePath);
            }
        }

        foreach (var extractDirectory in Directory.EnumerateDirectories(runtimesRoot, "*.extract", SearchOption.TopDirectoryOnly))
        {
            if (IsPathOlderThan(extractDirectory, LauncherTempRetention))
            {
                TryDeleteDirectoryQuietly(extractDirectory);
            }
        }
    }

    private static void CleanupProfileNativesDirectory(string nativesRoot)
    {
        if (!Directory.Exists(nativesRoot))
        {
            return;
        }

        var versionDirectories = new DirectoryInfo(nativesRoot)
            .GetDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();

        foreach (var versionDirectory in versionDirectories)
        {
            try
            {
                CleanupOldNativesDirectories(versionDirectory.FullName, MaxRetainedNativesLaunchDirectoriesPerVersion);
            }
            catch
            {
                // ignore cleanup issues inside natives cache
            }
        }

        for (var index = MaxRetainedNativesVersionDirectoriesPerProfile; index < versionDirectories.Length; index++)
        {
            TryDeleteDirectoryQuietly(versionDirectories[index].FullName);
        }
    }

    private static void CleanupStaleDirectoryChildren(string directoryPath, TimeSpan retention)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var childFilePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsPathOlderThan(childFilePath, retention))
            {
                TryDeleteFileQuietly(childFilePath);
            }
        }

        foreach (var childDirectoryPath in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsPathOlderThan(childDirectoryPath, retention))
            {
                TryDeleteDirectoryQuietly(childDirectoryPath);
            }
        }
    }

    private static bool IsPathOlderThan(string path, TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
        {
            return true;
        }

        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= retention;
        }
        catch
        {
            return false;
        }
    }

    private static int TryParseManagedJavaRuntimeMajor(string runtimeId)
    {
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return 0;
        }

        var normalized = Path.GetFileNameWithoutExtension(runtimeId.Trim());
        var segments = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || !segments[0].Equals("temurin", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return int.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
            ? major
            : 0;
    }

    private static void TryRunMaintenanceStep(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // keep launcher startup resilient even if cache cleanup fails
        }
    }

    private static bool TryResolveOptiFabricDownloadInfo(
        string minecraftVersionId,
        out OptiFabricDownloadInfo downloadInfo)
    {
        var normalizedMinecraftVersionId = NormalizeMinecraftBaseVersionId(minecraftVersionId);
        foreach (var candidate in OptiFabricDownloads)
        {
            if (!candidate.SupportedVersions.Contains(normalizedMinecraftVersionId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            downloadInfo = candidate;
            return true;
        }

        downloadInfo = null!;
        return false;
    }

    private static string BuildCurseForgeCdnDownloadUrl(int fileId, string fileName)
    {
        if (fileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileId), "Некорректный идентификатор файла CurseForge.");
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Имя файла CurseForge не указано.", nameof(fileName));
        }

        var folderA = fileId / 1000;
        var folderB = (fileId % 1000).ToString("000", CultureInfo.InvariantCulture);
        return string.Format(
            CultureInfo.InvariantCulture,
            CurseForgeCdnDownloadUrlTemplate,
            folderA,
            folderB,
            Uri.EscapeDataString(fileName));
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

    private static bool IsFabricOptiFineIncompatibleModFile(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".jar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryReadFabricModIds(filePath, out var modIds) &&
            modIds.Any(id => FabricOptiFineIncompatibleModIds.Contains(id)))
        {
            return true;
        }

        var fileName = Path.GetFileName(filePath);
        return FabricOptiFineIncompatibleFileNameTokens.Any(token =>
            fileName.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool NeedsFabricApiAutoInstall(IEnumerable<string> installedModFiles)
    {
        foreach (var filePath in installedModFiles)
        {
            if (!string.Equals(Path.GetExtension(filePath), ".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsOptiFineModFileName(filePath) || IsOptiFabricModFileName(filePath))
            {
                continue;
            }

            if (TryReadFabricModIds(filePath, out var modIds) &&
                modIds.Length > 0 &&
                modIds.All(id => FabricOptiFineIntrinsicModIds.Contains(id)))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static string? GetRequiredMinimumFabricLoaderVersion(IEnumerable<string> modFilePaths)
    {
        string? requiredMinimumVersion = null;
        foreach (var filePath in modFilePaths)
        {
            var candidateVersion = TryGetRequiredMinimumFabricLoaderVersionFromMod(filePath);
            if (string.IsNullOrWhiteSpace(candidateVersion))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(requiredMinimumVersion) ||
                ModLoaderVersionComparer.Instance.Compare(candidateVersion, requiredMinimumVersion) > 0)
            {
                requiredMinimumVersion = candidateVersion;
            }
        }

        return requiredMinimumVersion;
    }

    private static string? TryGetRequiredMinimumFabricLoaderVersionFromMod(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !File.Exists(filePath) ||
            !string.Equals(Path.GetExtension(filePath), ".jar", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var metadataEntry = archive.GetEntry("fabric.mod.json");
            if (metadataEntry is null)
            {
                return null;
            }

            using var stream = metadataEntry.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("depends", out var dependsElement) ||
                dependsElement.ValueKind != JsonValueKind.Object ||
                !dependsElement.TryGetProperty("fabricloader", out var loaderDependencyElement))
            {
                return null;
            }

            return ExtractMinimumFabricLoaderVersion(loaderDependencyElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractMinimumFabricLoaderVersion(JsonElement dependencyElement)
    {
        string? minimumVersion = null;

        void ApplyCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(minimumVersion) ||
                ModLoaderVersionComparer.Instance.Compare(candidate, minimumVersion) > 0)
            {
                minimumVersion = candidate;
            }
        }

        switch (dependencyElement.ValueKind)
        {
            case JsonValueKind.String:
                ApplyCandidate(ExtractMinimumFabricLoaderVersionFromConstraint(dependencyElement.GetString()));
                break;
            case JsonValueKind.Array:
                foreach (var item in dependencyElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        ApplyCandidate(ExtractMinimumFabricLoaderVersionFromConstraint(item.GetString()));
                    }
                }

                break;
        }

        return minimumVersion;
    }

    private static string? ExtractMinimumFabricLoaderVersionFromConstraint(string? constraintText)
    {
        if (string.IsNullOrWhiteSpace(constraintText))
        {
            return null;
        }

        string? minimumVersion = null;
        var tokens = constraintText
            .Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            string? candidate = null;
            if (trimmed.StartsWith(">=", StringComparison.Ordinal) ||
                trimmed.StartsWith(">", StringComparison.Ordinal) ||
                trimmed.StartsWith("^", StringComparison.Ordinal) ||
                trimmed.StartsWith("~", StringComparison.Ordinal) ||
                trimmed.StartsWith("=", StringComparison.Ordinal))
            {
                candidate = trimmed.TrimStart('>', '<', '=', '^', '~');
            }
            else if (!trimmed.StartsWith("<", StringComparison.Ordinal) &&
                     char.IsDigit(trimmed[0]))
            {
                candidate = trimmed;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(minimumVersion) ||
                ModLoaderVersionComparer.Instance.Compare(candidate, minimumVersion) > 0)
            {
                minimumVersion = candidate;
            }
        }

        return minimumVersion;
    }

    private static bool TryReadFabricModIds(string filePath, out string[] modIds)
    {
        modIds = [];
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            var metadataEntry = archive.GetEntry("fabric.mod.json");
            if (metadataEntry is null)
            {
                return false;
            }

            using var stream = metadataEntry.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String)
            {
                var id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }

            if (document.RootElement.TryGetProperty("provides", out var providesElement) &&
                providesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var providesItem in providesElement.EnumerateArray())
                {
                    if (providesItem.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var providedId = providesItem.GetString();
                    if (!string.IsNullOrWhiteSpace(providedId))
                    {
                        ids.Add(providedId);
                    }
                }
            }

            modIds = ids.ToArray();
            return modIds.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan GetRetryDelay(HttpResponseHeaders headers, HttpStatusCode statusCode, int attempt)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter?.Delta is { } retryDelta && retryDelta > TimeSpan.Zero)
        {
            var maxDelay = statusCode == HttpStatusCode.TooManyRequests
                ? TimeSpan.FromSeconds(30)
                : TimeSpan.FromSeconds(15);
            return retryDelta > maxDelay
                ? maxDelay
                : retryDelta;
        }

        if (retryAfter?.Date is { } retryDate)
        {
            var delta = retryDate - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                var maxDelay = statusCode == HttpStatusCode.TooManyRequests
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(15);
                return delta > maxDelay
                    ? maxDelay
                    : delta;
            }
        }

        var fallbackSeconds = statusCode == HttpStatusCode.TooManyRequests
            ? Math.Min(30, 4 + attempt * 5)
            : Math.Min(6, attempt * 2);
        return TimeSpan.FromSeconds(fallbackSeconds);
    }

    private static string? TryGetFabricLoaderVersion(JsonElement versionRoot)
    {
        if (!versionRoot.TryGetProperty("libraries", out var librariesElement) ||
            librariesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var library in librariesElement.EnumerateArray())
        {
            if (!library.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var libraryName = nameElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(libraryName))
            {
                continue;
            }

            const string prefix = "net.fabricmc:fabric-loader:";
            if (libraryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return libraryName[prefix.Length..].Trim();
            }

            if (libraryName.StartsWith("fabric-loader:", StringComparison.OrdinalIgnoreCase))
            {
                var segments = libraryName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segments.Length >= 2)
                {
                    return segments[^1];
                }
            }
        }

        return null;
    }

    private static bool HasFabricOptiFineRuntimeSupport(JsonElement versionRoot)
    {
        if (!versionRoot.TryGetProperty("libraries", out var librariesElement) ||
            librariesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var hasFabricLoader = false;
        foreach (var library in librariesElement.EnumerateArray())
        {
            if (!library.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var libraryName = nameElement.GetString()?.Trim();
            if (libraryName?.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase) == true ||
                libraryName?.StartsWith("fabric-loader:", StringComparison.OrdinalIgnoreCase) == true)
            {
                hasFabricLoader = true;
            }

            if (libraryName?.StartsWith("net.fabricmc:tiny-remapper:", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return hasFabricLoader;
    }

    private static void DeleteMatchingModFiles(string modsDirectory, Func<string, bool> predicate)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            if (!predicate(filePath))
            {
                continue;
            }

            File.Delete(filePath);
        }
    }

    private void ResetFabricProcessedModCache(
        string installedVersionId,
        LauncherProfile profile = LauncherProfile.Vanilla)
    {
        if (string.IsNullOrWhiteSpace(installedVersionId))
        {
            return;
        }

        var instanceDirectory = GetVersionInstanceDirectory(profile, installedVersionId.Trim());
        TryDeleteDirectoryQuietly(Path.Combine(instanceDirectory, ".fabric", "processedMods"));
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private static void TryDeleteDirectoryQuietly(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore cache cleanup failures
        }
    }

    private static bool IsSimpleJavaAlias(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "java" or "javaw" or "java.exe" or "javaw.exe";
    }

    private static string? TryResolveCompatibleJavaForLegacyVersions()
    {
        var candidates = EnumerateJavaExecutableCandidates()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { Path = path, Major = GetJavaMajorVersion(path) })
            .Where(item => item.Major > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var preferredMajor in new[] { 8, 11, 16, 17 })
        {
            var preferred = candidates.FirstOrDefault(item => item.Major == preferredMajor);
            if (preferred is not null)
            {
                return preferred.Path;
            }
        }

        var acceptable = candidates
            .Where(item => item.Major is >= 8 and <= 17)
            .OrderBy(item => item.Major)
            .FirstOrDefault();

        return acceptable?.Path;
    }

    private static IEnumerable<string> EnumerateJavaExecutableCandidates()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var envVar in new[] { "JAVA_HOME", "JAVA8_HOME", "JAVA17_HOME", "JDK_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(Environment.ExpandEnvironmentVariables(value), "bin", "javaw.exe"),
                         Path.Combine(Environment.ExpandEnvironmentVariables(value), "bin", "java.exe")
                     })
            {
                if (File.Exists(candidate))
                {
                    result.Add(candidate);
                }
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var javaRoot = Path.Combine(root, "Java");
            if (Directory.Exists(javaRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(javaRoot))
                {
                    foreach (var relative in new[]
                             {
                                 Path.Combine("bin", "javaw.exe"),
                                 Path.Combine("bin", "java.exe"),
                                 Path.Combine("jre", "bin", "javaw.exe"),
                                 Path.Combine("jre", "bin", "java.exe")
                             })
                    {
                        var candidate = Path.Combine(directory, relative);
                        if (File.Exists(candidate))
                        {
                            result.Add(candidate);
                        }
                    }
                }
            }

            var oracleJavaRoot = Path.Combine(root, "Common Files", "Oracle", "Java");
            if (Directory.Exists(oracleJavaRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(oracleJavaRoot))
                {
                    foreach (var relative in new[] { "javaw.exe", "java.exe", Path.Combine("bin", "javaw.exe"), Path.Combine("bin", "java.exe") })
                    {
                        var candidate = Path.Combine(directory, relative);
                        if (File.Exists(candidate))
                        {
                            result.Add(candidate);
                        }
                    }
                }
            }
        }

        foreach (var alias in new[] { "javaw", "java" })
        {
            foreach (var path in ResolveExecutableFromPath(alias))
            {
                if (File.Exists(path))
                {
                    result.Add(path);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ResolveExecutableFromPath(string executableName)
    {
        var result = new List<string>();
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = executableName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                return result;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    result.Add(line);
                }
            }
        }
        catch
        {
            return result;
        }

        return result;
    }

    private static int GetJavaMajorVersion(string javaExecutablePath)
    {
        try
        {
            var probeExecutablePath = ResolveJavaProbeExecutable(javaExecutablePath);
            if (string.IsNullOrWhiteSpace(probeExecutablePath) || !File.Exists(probeExecutablePath))
            {
                return -1;
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = probeExecutablePath,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                return -1;
            }

            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit(3000);

            var versionText = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            var match = JavaVersionRegex.Match(versionText);
            if (!match.Success)
            {
                return -1;
            }

            var rawVersion = match.Groups["version"].Value;
            if (rawVersion.StartsWith("1.", StringComparison.Ordinal))
            {
                var legacyParts = rawVersion.Split('.');
                if (legacyParts.Length >= 2 && int.TryParse(legacyParts[1], out var legacyMajor))
                {
                    return legacyMajor;
                }
            }

            var majorPart = rawVersion.Split('.', 2)[0];
            return int.TryParse(majorPart, out var major) ? major : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string ResolveJavaProbeExecutable(string javaExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath))
        {
            return string.Empty;
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(javaExecutablePath.Trim());
        var fileName = Path.GetFileName(expandedPath);
        if (!fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return expandedPath;
        }

        var directory = Path.GetDirectoryName(expandedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return expandedPath;
        }

        var consoleJavaPath = Path.Combine(directory, "java.exe");
        return File.Exists(consoleJavaPath) ? consoleJavaPath : expandedPath;
    }

    private static string PrepareNativesDirectory(string gameDirectory, string versionId)
    {
        var nativesRoot = Path.Combine(gameDirectory, "natives", versionId);
        Directory.CreateDirectory(nativesRoot);

        // Use per-launch folder to avoid "file is locked" failures when another instance is still running.
        var launchFolderName = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var nativesDirectory = Path.Combine(nativesRoot, launchFolderName);
        Directory.CreateDirectory(nativesDirectory);

        try
        {
            CleanupOldNativesDirectories(nativesRoot, keepCount: 5);
        }
        catch
        {
            // ignore cleanup issues, launch should still proceed
        }

        return nativesDirectory;
    }

    private static void CleanupOldNativesDirectories(string nativesRoot, int keepCount)
    {
        if (keepCount < 1 || !Directory.Exists(nativesRoot))
        {
            return;
        }

        var directories = new DirectoryInfo(nativesRoot)
            .GetDirectories()
            .OrderByDescending(directory => directory.CreationTimeUtc)
            .ToArray();

        for (var index = keepCount; index < directories.Length; index++)
        {
            try
            {
                directories[index].Delete(recursive: true);
            }
            catch
            {
                // ignore cleanup issues
            }
        }
    }

    private static void ExtractNatives(string archivePath, string targetDirectory)
    {
        if (!File.Exists(archivePath))
        {
            return;
        }

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extension = Path.GetExtension(entry.Name);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".so", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.Combine(targetDirectory, entry.Name);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static List<DownloadEntry> BuildAssetDownloadEntries(string assetIndexPath, string assetsDirectory)
    {
        var result = new List<DownloadEntry>();

        using var indexDocument = JsonDocument.Parse(File.ReadAllText(assetIndexPath));
        if (!indexDocument.RootElement.TryGetProperty("objects", out var objectsElement) ||
            objectsElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var asset in objectsElement.EnumerateObject())
        {
            var hash = asset.Value.TryGetProperty("hash", out var hashElement)
                ? hashElement.GetString() ?? string.Empty
                : string.Empty;

            if (hash.Length < 2)
            {
                continue;
            }

            var firstPart = hash[..2];
            var objectPath = Path.Combine(assetsDirectory, "objects", firstPart, hash);
            var assetUrl = $"https://resources.download.minecraft.net/{firstPart}/{hash}";
            result.Add(new DownloadEntry(assetUrl, objectPath, hash, "asset"));
        }

        return result;
    }

    private static IReadOnlyList<DownloadEntry> DeduplicateDownloadEntries(IEnumerable<DownloadEntry> entries)
    {
        var result = new List<DownloadEntry>();
        var seenDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Url) ||
                string.IsNullOrWhiteSpace(entry.DestinationPath))
            {
                continue;
            }

            var destinationKey = Path.GetFullPath(entry.DestinationPath);
            if (seenDestinations.Add(destinationKey))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static async Task<bool> DownloadEntriesAsync(
        IReadOnlyList<DownloadEntry> entries,
        IProgress<LauncherProgress>? progress,
        CancellationToken cancellationToken)
    {
        var queue = DeduplicateDownloadEntries(entries);
        var total = queue.Count;
        if (total == 0)
        {
            return false;
        }

        var downloadedAny = 0;
        var completed = 0;
        var fractions = new double[total];
        var progressLock = new object();
        long lastProgressTick = 0;

        void ReportProgress(string stage, int index, double fraction, bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            LauncherProgress progressValue;
            lock (progressLock)
            {
                if (index >= 0 && index < fractions.Length)
                {
                    fractions[index] = Math.Clamp(fraction, 0d, 1d);
                }

                var tick = Environment.TickCount64;
                if (!force && tick - lastProgressTick < 120)
                {
                    return;
                }

                lastProgressTick = tick;
                var current = completed + fractions.Sum();
                progressValue = new LauncherProgress(stage, current, total);
            }

            progress.Report(progressValue);
        }

        progress?.Report(new LauncherProgress($"Скачивание файлов (0/{total})...", 0, total));

        using var semaphore = new SemaphoreSlim(VersionFileDownloadConcurrency, VersionFileDownloadConcurrency);
        var tasks = queue.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            var stage = $"Скачивание ({index + 1}/{total}): {item.Label}";

            var finished = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(stage, index, 0, force: index == 0);

                var downloaded = await DownloadFileIfNeededAsync(
                    item.Url,
                    item.DestinationPath,
                    item.Sha1,
                    cancellationToken,
                    (downloadedBytes, totalBytes) =>
                    {
                        if (totalBytes is > 0)
                        {
                            ReportProgress(stage, index, (double)downloadedBytes / totalBytes.Value);
                        }
                    });

                if (downloaded)
                {
                    Interlocked.Exchange(ref downloadedAny, 1);
                }

                finished = true;
            }
            catch
            {
                ReportProgress("Ошибка скачивания: " + item.Label, index, 0, force: true);
                throw;
            }
            finally
            {
                LauncherProgress? progressValue = null;
                lock (progressLock)
                {
                    fractions[index] = 0;
                    if (finished)
                    {
                        completed++;
                    }
                    lastProgressTick = Environment.TickCount64;
                    progressValue = new LauncherProgress(
                        $"Скачивание файлов ({completed}/{total})...",
                        completed + fractions.Sum(),
                        total);
                }

                progress?.Report(progressValue);
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        progress?.Report(new LauncherProgress($"Скачивание файлов ({total}/{total}) завершено.", total, total));
        return Volatile.Read(ref downloadedAny) != 0;
    }

    private static Action<long, long?>? CreateStageDownloadProgressReporter(
        IProgress<LauncherProgress>? progress,
        string stage,
        double baseCurrent,
        double stageSpan,
        double total)
    {
        if (progress is null || total <= 0 || stageSpan <= 0)
        {
            return null;
        }

        double lastFraction = -1;
        long lastTick = 0;

        return (downloadedBytes, totalBytes) =>
        {
            if (totalBytes is not > 0)
            {
                return;
            }

            var fraction = Math.Clamp((double)downloadedBytes / totalBytes.Value, 0d, 1d);
            var tick = Environment.TickCount64;
            var isFinal = downloadedBytes >= totalBytes.Value;
            if (!isFinal &&
                lastFraction >= 0 &&
                fraction - lastFraction < 0.01 &&
                tick - lastTick < 120)
            {
                return;
            }

            lastFraction = fraction;
            lastTick = tick;
            progress.Report(new LauncherProgress(stage, baseCurrent + stageSpan * fraction, total));
        };
    }

    private static async Task CopyToWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        Action<long, long?>? progressCallback,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        progressCallback?.Invoke(0, totalBytes);

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            progressCallback?.Invoke(totalRead, totalBytes);
        }
    }

    private static async Task<bool> DeleteFileIfSha1MismatchAsync(
        string filePath,
        string? expectedSha1,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedSha1))
        {
            return false;
        }

        try
        {
            var currentHash = await ComputeSha1Async(filePath, cancellationToken);
            if (currentHash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            // If we cannot read the file, treat it as damaged and force a clean restore.
        }

        TryDeleteFileQuietly(filePath);
        return true;
    }

    private static async Task<bool> DownloadFileIfNeededAsync(
        string url,
        string destinationPath,
        string? expectedSha1,
        CancellationToken cancellationToken,
        Action<long, long?>? progressCallback = null)
    {
        if (File.Exists(destinationPath))
        {
            if (string.IsNullOrWhiteSpace(expectedSha1))
            {
                return false;
            }

            var currentHash = await ComputeSha1Async(destinationPath, cancellationToken);
            if (currentHash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("РќРµРІР°Р»РёРґРЅС‹Р№ РїСѓС‚СЊ РЅР°Р·РЅР°С‡РµРЅРёСЏ."));

        var tempFile = destinationPath + ".tmp";

        var downloadAttempt = await GetDownloadResponseWithFallbackAsync(url, cancellationToken);
        using var response = downloadAttempt.Response;
        var contentLength = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyToWithProgressAsync(source, destination, contentLength, progressCallback, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            var downloadedHash = await ComputeSha1Async(tempFile, cancellationToken);
            if (!downloadedHash.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempFile);
                throw new InvalidDataException($"SHA1 РЅРµ СЃРѕРІРїР°Р» РґР»СЏ С„Р°Р№Р»Р°: {destinationPath}");
            }
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempFile, destinationPath);
        return true;
    }

    private static async Task<bool> DownloadFileWithRetriesAsync(
        string url,
        string destinationPath,
        string? expectedSha1,
        CancellationToken cancellationToken,
        Action<long, long?>? progressCallback = null,
        int maxAttempts = 3)
    {
        if (maxAttempts <= 1)
        {
            return await DownloadFileIfNeededAsync(url, destinationPath, expectedSha1, cancellationToken, progressCallback);
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await DownloadFileIfNeededAsync(url, destinationPath, expectedSha1, cancellationToken, progressCallback);
            }
            catch (Exception ex) when (attempt < maxAttempts &&
                                       ex is HttpRequestException or IOException or TaskCanceledException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }

        throw lastError ?? new IOException($"Не удалось скачать файл после {maxAttempts} попыток: {url}");
    }

    private static async Task<bool> DownloadFileWithSha256IfNeededAsync(
        string url,
        string destinationPath,
        string? expectedSha256,
        CancellationToken cancellationToken,
        Action<long, long?>? progressCallback = null)
    {
        if (File.Exists(destinationPath))
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return false;
            }

            var currentHash = await ComputeSha256Async(destinationPath, cancellationToken);
            if (currentHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Некорректный путь назначения."));
        var tempFile = destinationPath + ".tmp";

        var downloadAttempt = await GetDownloadResponseWithFallbackAsync(url, cancellationToken);
        using var response = downloadAttempt.Response;
        var contentLength = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyToWithProgressAsync(source, destination, contentLength, progressCallback, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var downloadedHash = await ComputeSha256Async(tempFile, cancellationToken);
            if (!downloadedHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempFile);
                throw new InvalidDataException($"SHA256 не совпал для файла: {destinationPath}");
            }
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(tempFile, destinationPath);
        return true;
    }

    private sealed record DownloadAttemptResult(
        HttpResponseMessage Response,
        string EffectiveUrl,
        IReadOnlyList<string> AttemptedUrls);

    private static async Task<DownloadAttemptResult> GetDownloadResponseWithFallbackAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var attempted = new List<string>();
        HttpStatusCode? lastStatus = null;
        Exception? lastError = null;
        const int maxAttemptsPerCandidate = 3;

        foreach (var candidate in EnumerateDownloadFallbackUrls(url))
        {
            if (attempted.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            attempted.Add(candidate);
            for (var attempt = 1; attempt <= maxAttemptsPerCandidate; attempt++)
            {
                try
                {
                    var response = await Http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return new DownloadAttemptResult(response, candidate, attempted);
                    }

                    lastStatus = response.StatusCode;
                    if (attempt < maxAttemptsPerCandidate && IsTransientHttpStatus(response.StatusCode))
                    {
                        await Task.Delay(GetRetryDelay(response.Headers, response.StatusCode, attempt), cancellationToken);
                        response.Dispose();
                        continue;
                    }

                    response.Dispose();
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                           !cancellationToken.IsCancellationRequested)
                {
                    lastError = ex;
                    if (attempt < maxAttemptsPerCandidate)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(6, attempt * 2)), cancellationToken);
                        continue;
                    }
                }

                break;
            }
        }

        var statusText = lastStatus.HasValue ? $"{(int)lastStatus.Value} {lastStatus.Value}" : "unknown";
        throw new HttpRequestException(
            $"Не удалось скачать файл. Статус: {statusText}. URL: {url}. " +
            $"Проверено: {string.Join(" -> ", attempted)}",
            lastError);
    }

    private static IEnumerable<string> EnumerateDownloadFallbackUrls(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            yield break;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            yield return url;
            yield break;
        }

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (IsVersionManifestUri(uri))
        {
            if (emitted.Add(BmclApiVersionManifestUrl))
            {
                yield return BmclApiVersionManifestUrl;
            }

            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var alternateManifestUrl = uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase)
                ? "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"
                : "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            if (emitted.Add(alternateManifestUrl))
            {
                yield return alternateManifestUrl;
            }

            yield break;
        }

        if (uri.Host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var bmclAssetUrl = $"{BmclApiBaseUrl}/assets{uri.AbsolutePath}";
            if (emitted.Add(bmclAssetUrl))
            {
                yield return bmclAssetUrl;
            }

            var hash = TryExtractMinecraftObjectHash(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(hash))
            {
                var pistonObjectUrl = $"https://piston-data.mojang.com/v1/objects/{hash}";
                if (emitted.Add(pistonObjectUrl))
                {
                    yield return pistonObjectUrl;
                }
            }

            yield break;
        }

        if (uri.Host.Equals("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            if (uri.AbsolutePath.Contains("/net/minecraftforge/", StringComparison.OrdinalIgnoreCase))
            {
                var forgeMirrorUrl = $"{ForgeMavenBaseUrl.TrimEnd('/')}{uri.AbsolutePath}";
                if (emitted.Add(forgeMirrorUrl))
                {
                    yield return forgeMirrorUrl;
                }
            }

            var bmclLibraryUrl = $"{BmclApiBaseUrl}/maven{uri.AbsolutePath}";
            if (emitted.Add(bmclLibraryUrl))
            {
                yield return bmclLibraryUrl;
            }

            yield break;
        }

        if (uri.Host.Equals("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var bmclForgeUrl = $"{BmclApiBaseUrl}/maven{uri.AbsolutePath}";
            if (emitted.Add(bmclForgeUrl))
            {
                yield return bmclForgeUrl;
            }

            yield break;
        }

        if (uri.Host.Equals("files.minecraftforge.net", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
        {
            var bmclForgeFileUrl = $"{BmclApiBaseUrl}{uri.AbsolutePath}";
            if (emitted.Add(bmclForgeFileUrl))
            {
                yield return bmclForgeFileUrl;
            }

            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var forgeMavenUrl = $"{ForgeMavenBaseUrl.TrimEnd('/')}{uri.AbsolutePath["/maven".Length..]}";
            if (emitted.Add(forgeMavenUrl))
            {
                yield return forgeMavenUrl;
            }

            yield break;
        }

        if (uri.Host.Equals("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
        {
            var bmclFabricUrl = $"{BmclApiBaseUrl}/fabric-meta{uri.AbsolutePath}";
            if (emitted.Add(bmclFabricUrl))
            {
                yield return bmclFabricUrl;
            }

            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            yield break;
        }

        if (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var launchermetaUrl = $"https://launchermeta.mojang.com{uri.AbsolutePath}";
            if (emitted.Add(launchermetaUrl))
            {
                yield return launchermetaUrl;
            }

            yield break;
        }

        if (uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var pistonMetaUrl = $"https://piston-meta.mojang.com{uri.AbsolutePath}";
            if (emitted.Add(pistonMetaUrl))
            {
                yield return pistonMetaUrl;
            }

            yield break;
        }

        if (uri.Host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase))
        {
            var pistonDataUrl = $"https://piston-data.mojang.com{uri.AbsolutePath}";
            if (emitted.Add(pistonDataUrl))
            {
                yield return pistonDataUrl;
            }

            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var hash = TryExtractMinecraftObjectHash(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(hash) && hash.Length >= 2)
            {
                var bmclObjectUrl = $"{BmclApiBaseUrl}/assets/{hash[..2]}/{hash}";
                if (emitted.Add(bmclObjectUrl))
                {
                    yield return bmclObjectUrl;
                }

                var resourceObjectUrl = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
                if (emitted.Add(resourceObjectUrl))
                {
                    yield return resourceObjectUrl;
                }
            }

            yield break;
        }

        if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            var launcherUrl = $"https://launcher.mojang.com{uri.AbsolutePath}";
            if (emitted.Add(launcherUrl))
            {
                yield return launcherUrl;
            }

            var hash = TryExtractMinecraftObjectHash(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(hash) && hash.Length >= 2)
            {
                var bmclObjectUrl = $"{BmclApiBaseUrl}/assets/{hash[..2]}/{hash}";
                if (emitted.Add(bmclObjectUrl))
                {
                    yield return bmclObjectUrl;
                }

                var resourceObjectUrl = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
                if (emitted.Add(resourceObjectUrl))
                {
                    yield return resourceObjectUrl;
                }
            }

            yield break;
        }

        if (uri.Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
        {
            if (emitted.Add(uri.ToString()))
            {
                yield return uri.ToString();
            }

            if (uri.AbsolutePath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            {
                var officialAssetsUrl = $"https://resources.download.minecraft.net{uri.AbsolutePath}";
                if (emitted.Add(officialAssetsUrl))
                {
                    yield return officialAssetsUrl;
                }

                var hash = TryExtractMinecraftObjectHash(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(hash))
                {
                    var pistonObjectUrl = $"https://piston-data.mojang.com/v1/objects/{hash}";
                    if (emitted.Add(pistonObjectUrl))
                    {
                        yield return pistonObjectUrl;
                    }
                }
            }
            else if (uri.AbsolutePath.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = uri.AbsolutePath.Substring("/maven".Length);
                var librariesUrl = $"https://libraries.minecraft.net{suffix}";
                if (emitted.Add(librariesUrl))
                {
                    yield return librariesUrl;
                }

                if (suffix.Contains("/net/minecraftforge/", StringComparison.OrdinalIgnoreCase))
                {
                    var forgeMirrorUrl = $"{ForgeMavenBaseUrl.TrimEnd('/')}{suffix}";
                    if (emitted.Add(forgeMirrorUrl))
                    {
                        yield return forgeMirrorUrl;
                    }
                }
            }
            else if (uri.AbsolutePath.StartsWith("/fabric-meta/", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = uri.AbsolutePath.Substring("/fabric-meta".Length);
                var fabricMetaUrl = $"https://meta.fabricmc.net{suffix}";
                if (emitted.Add(fabricMetaUrl))
                {
                    yield return fabricMetaUrl;
                }
            }
            else if (uri.AbsolutePath.Equals("/mc/game/version_manifest_v2.json", StringComparison.OrdinalIgnoreCase))
            {
                const string officialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
                if (emitted.Add(officialManifestUrl))
                {
                    yield return officialManifestUrl;
                }

                const string alternateManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";
                if (emitted.Add(alternateManifestUrl))
                {
                    yield return alternateManifestUrl;
                }
            }

            yield break;
        }

        yield return uri.ToString();
    }

    private static bool IsVersionManifestUri(Uri uri)
    {
        return uri.AbsolutePath.Equals("/mc/game/version_manifest_v2.json", StringComparison.OrdinalIgnoreCase) &&
               (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTransientHttpStatus(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               (int)statusCode >= 500;
    }

    private static bool IsModrinthApiUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Host.Equals("api.modrinth.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractMinecraftObjectHash(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('/');
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "objects", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = segments[i + 1];
                if (candidate.Length >= 20)
                {
                    return candidate;
                }
            }
        }

        if (segments.Length == 0)
        {
            return null;
        }

        var last = segments[^1];
        return last.Length >= 20 ? last : null;
    }

    private static async Task<string> ComputeSha1Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha1(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildOfflineUuid(string username)
    {
        var input = Encoding.UTF8.GetBytes($"OfflinePlayer:{username}");
        var hash = MD5.HashData(input);

        // Match Java UUID.nameUUIDFromBytes formatting used by offline launchers.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // Format UUID as big-endian hex string (same as Java) instead of using
        // new Guid(hash) which reorders the first 8 bytes to little-endian on Windows.
        var h = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    private static string BuildOfflineAccessToken(string username)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"VesperAuth:{username}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? FindElyByAuthlib(
        string librariesDirectory,
        string versionId,
        string? sourceGameDirectory = null,
        string? preferredVersionPrefix = null,
        bool allowFallback = true)
    {
        var targetVersionPrefix = preferredVersionPrefix
            ?? (ShouldUseLegacyRuntimeCompatibility(versionId) ? "3." : "7.");

        var rootCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(sourceGameDirectory))
        {
            rootCandidates.Add(Path.Combine(sourceGameDirectory, "libraries"));
        }

        rootCandidates.AddRange(EnumerateLibrarySourceRoots(versionId, sourceGameDirectory));
        rootCandidates.Add(librariesDirectory);

        string? fallbackJar = null;
        foreach (var root in rootCandidates
                     .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var elyDir = Path.Combine(root, "by", "ely", "authlib");
            if (!Directory.Exists(elyDir))
            {
                continue;
            }

            var jars = Directory.EnumerateFiles(elyDir, "authlib-*.jar", SearchOption.AllDirectories).ToList();
            var preferredJar = jars.FirstOrDefault(path =>
                Path.GetFileName(path).Contains($"authlib-{targetVersionPrefix}", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(preferredJar))
            {
                return preferredJar;
            }

            var bestFallbackForRoot = jars
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (allowFallback && fallbackJar is null && !string.IsNullOrWhiteSpace(bestFallbackForRoot))
            {
                fallbackJar = bestFallbackForRoot;
            }
        }

        return allowFallback ? fallbackJar : null;
    }

    private static void PrependCompatibilityLibraries(
        List<string> classpathEntries,
        string librariesDirectory,
        string versionId,
        LaunchOptions options,
        VesperSkinBridgePlan skinBridgePlan)
    {
        if (options.MicrosoftSession != null ||
            !ShouldUseLegacyRuntimeCompatibility(versionId))
        {
            return;
        }

        if (skinBridgePlan.Enabled && !string.IsNullOrWhiteSpace(skinBridgePlan.ElyAuthlibPath))
        {
            classpathEntries.Add(skinBridgePlan.ElyAuthlibPath!);
        }

        TryAddCompatibilityLibrary(
            classpathEntries,
            versionId,
            "com/turikhay/ca-fixer/1.0/ca-fixer-1.0.jar",
            librariesDirectory,
            options.Version.SourceGameDirectory);

        TryAddCompatibilityLibrary(
            classpathEntries,
            versionId,
            "ru/tlauncher/patchy/1.0.0/patchy-1.0.0.jar",
            librariesDirectory,
            options.Version.SourceGameDirectory);

        var elyAuthlibPath = TryPrepareElyAuthlib(
            librariesDirectory,
            versionId,
            options.Version.SourceGameDirectory);

        if (!string.IsNullOrWhiteSpace(elyAuthlibPath))
        {
            classpathEntries.Add(elyAuthlibPath);
        }
    }

    private static void TryAddCompatibilityLibrary(
        List<string> classpathEntries,
        string versionId,
        string relativePath,
        string librariesDirectory,
        string? sourceGameDirectory)
    {
        var destinationPath = CombineWithRoot(librariesDirectory, relativePath);
        TryCopyLibraryFromKnownSources(versionId, relativePath, destinationPath, sourceGameDirectory);
        if (File.Exists(destinationPath))
        {
            classpathEntries.Add(destinationPath);
        }
    }

    private static string? TryPrepareElyAuthlib(
        string librariesDirectory,
        string versionId,
        string? sourceGameDirectory,
        string? preferredVersionPrefix = null,
        bool allowFallback = true)
    {
        var sourcePath = FindElyByAuthlib(librariesDirectory, versionId, sourceGameDirectory, preferredVersionPrefix, allowFallback);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var marker = $"{Path.DirectorySeparatorChar}by{Path.DirectorySeparatorChar}ely{Path.DirectorySeparatorChar}authlib{Path.DirectorySeparatorChar}";
        var markerIndex = fullSourcePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return sourcePath;
        }

        var relativePath = fullSourcePath[(markerIndex + 1)..];
        var destinationPath = CombineWithRoot(librariesDirectory, relativePath);
        if (!PathsReferToSameFile(sourcePath, destinationPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("Некорректный путь Ely authlib."));
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return File.Exists(destinationPath) ? destinationPath : sourcePath;
    }

    private async Task<VesperSkinBridgePlan> ResolveVesperSkinBridgePlanAsync(
        string librariesDirectory,
        string versionId,
        LaunchOptions options,
        JsonElement versionRoot,
        CancellationToken cancellationToken)
    {
        var authlibMajorVersion = TryGetAuthlibMajorVersion(versionRoot);
        var authlibVersion = TryGetAuthlibVersion(versionRoot);

        if (options.VesperAuthSession is null)
        {
            return new VesperSkinBridgePlan(false, authlibMajorVersion, null, "no-vesper-session");
        }

        if (options.MicrosoftSession != null)
        {
            return new VesperSkinBridgePlan(false, authlibMajorVersion, null, "microsoft-session");
        }

        if (ShouldUseLegacyRuntimeCompatibility(versionId))
        {
            var legacyElyAuthlibPath = TryPrepareElyAuthlib(
                librariesDirectory,
                versionId,
                options.Version.SourceGameDirectory,
                preferredVersionPrefix: "3.",
                allowFallback: false);

            return string.IsNullOrWhiteSpace(legacyElyAuthlibPath)
                ? new VesperSkinBridgePlan(false, authlibMajorVersion, null, "legacy-runtime-missing-ely-3")
                : new VesperSkinBridgePlan(true, authlibMajorVersion, legacyElyAuthlibPath, "legacy-runtime-via-ely-3");
        }

        if (authlibMajorVersion is null)
        {
            return new VesperSkinBridgePlan(false, null, null, "authlib-version-unknown");
        }

        string? elyAuthlibPath = null;
        string bridgeReason;

        if (authlibMajorVersion == 6)
        {
            if (!string.IsNullOrWhiteSpace(authlibVersion))
            {
                elyAuthlibPath = await TryPrepareMatchingElyAuthlibAsync(
                    librariesDirectory,
                    versionId,
                    options.Version.SourceGameDirectory,
                    authlibVersion,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(elyAuthlibPath))
            {
                elyAuthlibPath = TryPrepareElyAuthlib(
                    librariesDirectory,
                    versionId,
                    options.Version.SourceGameDirectory,
                    preferredVersionPrefix: "6.",
                    allowFallback: false);
            }

            bridgeReason = string.IsNullOrWhiteSpace(elyAuthlibPath)
                ? "authlib-6-unsupported"
                : "authlib-6-via-ely-6";
        }
        else if (authlibMajorVersion >= 7)
        {
            var preferredVersionPrefix = $"{authlibMajorVersion.Value.ToString(CultureInfo.InvariantCulture)}.";
            if (!string.IsNullOrWhiteSpace(authlibVersion))
            {
                elyAuthlibPath = await TryPrepareMatchingElyAuthlibAsync(
                    librariesDirectory,
                    versionId,
                    options.Version.SourceGameDirectory,
                    authlibVersion,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(elyAuthlibPath))
            {
                elyAuthlibPath = TryPrepareElyAuthlib(
                    librariesDirectory,
                    versionId,
                    options.Version.SourceGameDirectory,
                    preferredVersionPrefix,
                    allowFallback: false);
            }

            bridgeReason = string.IsNullOrWhiteSpace(elyAuthlibPath)
                ? $"missing-ely-authlib-{preferredVersionPrefix.TrimEnd('.')}"
                : "enabled";
        }
        else
        {
            return new VesperSkinBridgePlan(false, authlibMajorVersion, null, $"authlib-{authlibMajorVersion}-unsupported");
        }

        if (string.IsNullOrWhiteSpace(elyAuthlibPath))
        {
            return new VesperSkinBridgePlan(false, authlibMajorVersion, null, bridgeReason);
        }

        return new VesperSkinBridgePlan(true, authlibMajorVersion, elyAuthlibPath, bridgeReason);
    }

    private static string BuildVesperElyProfileLookupUrl(string apiBaseUrl)
    {
        return $"{apiBaseUrl.TrimEnd('/')}/ely/profile/";
    }

    private static string? TryGetAuthlibVersion(JsonElement versionRoot)
    {
        if (!versionRoot.TryGetProperty("libraries", out var librariesElement) ||
            librariesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var library in librariesElement.EnumerateArray())
        {
            if (!library.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var libraryName = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(libraryName) ||
                !libraryName.StartsWith("com.mojang:authlib:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = libraryName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3)
            {
                return parts[2];
            }
        }

        return null;
    }

    private static int? TryGetAuthlibMajorVersion(JsonElement versionRoot)
    {
        var authlibVersion = TryGetAuthlibVersion(versionRoot);
        if (string.IsNullOrWhiteSpace(authlibVersion))
        {
            return null;
        }

        var separatorIndex = authlibVersion.IndexOf('.');
        var majorText = separatorIndex >= 0 ? authlibVersion[..separatorIndex] : authlibVersion;
        if (int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return major;
        }

        return null;
    }

    private async Task<string?> TryPrepareMatchingElyAuthlibAsync(
        string librariesDirectory,
        string versionId,
        string? sourceGameDirectory,
        string authlibVersion,
        CancellationToken cancellationToken)
    {
        var localMatch = TryPrepareElyAuthlib(
            librariesDirectory,
            versionId,
            sourceGameDirectory,
            preferredVersionPrefix: authlibVersion,
            allowFallback: false);
        if (!string.IsNullOrWhiteSpace(localMatch))
        {
            return localMatch;
        }

        return await TryDownloadMatchingElyAuthlibAsync(librariesDirectory, authlibVersion, cancellationToken);
    }

    private async Task<string?> TryDownloadMatchingElyAuthlibAsync(
        string librariesDirectory,
        string authlibVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authlibVersion))
        {
            return null;
        }

        try
        {
            await using var stream = await Http.GetStreamAsync(ElyPrismMetadataUrl, cancellationToken);
            using var metadata = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!metadata.RootElement.TryGetProperty("overrides", out var overridesElement) ||
                !overridesElement.TryGetProperty("com.mojang:authlib", out var authlibOverrides) ||
                !authlibOverrides.TryGetProperty(authlibVersion, out var overrideElement))
            {
                return null;
            }

            var libraryName = overrideElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;
            var libraryUrl = overrideElement.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;
            var librarySha1 = overrideElement.TryGetProperty("sha1", out var sha1Element) && sha1Element.ValueKind == JsonValueKind.String
                ? sha1Element.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(libraryName) ||
                string.IsNullOrWhiteSpace(libraryUrl) ||
                !TryBuildMavenLibraryRelativePath(libraryName, out var relativePath))
            {
                return null;
            }

            var destinationPath = CombineWithRoot(librariesDirectory, relativePath);
            await DownloadFileIfNeededAsync(libraryUrl, destinationPath, librarySha1, cancellationToken);
            return File.Exists(destinationPath) ? destinationPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBuildMavenLibraryRelativePath(string libraryName, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return false;
        }

        var parts = libraryName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        var groupPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var artifactId = parts[1];
        var version = parts[2];
        var fileName = $"{artifactId}-{version}.jar";
        relativePath = Path.Combine(groupPath, artifactId, version, fileName);
        return true;
    }

    private static async Task PersistJavaLaunchOutputAsync(
        Task<string> stdOutTask,
        Task<string> stdErrTask,
        string stdOutPath,
        string stdErrPath)
    {
        try
        {
            var stdOut = await stdOutTask.ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);
            TryWriteJavaLaunchOutput(stdOutPath, stdOut);
            TryWriteJavaLaunchOutput(stdErrPath, stdErr);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private sealed record ImmediateLaunchFailureAnalysis(
        string? UserMessage,
        string? DiagnosticSnippet,
        bool ShouldAttemptAutomaticRepair,
        bool ResetVersionArtifacts,
        bool ResetDownloadedJavaRuntime,
        bool ResetCompatibilityLibraries,
        string? RepairStageText);

    private static ImmediateLaunchFailureAnalysis AnalyzeImmediateLaunchFailure(
        string resolvedVersionId,
        string latestLogPath,
        string javaStdErrPath,
        string? stdErrText,
        string? stdOutText)
    {
        var latestLogTail = ReadLogTailSafe(latestLogPath, 120);
        var javaStdErrTail = !string.IsNullOrWhiteSpace(stdErrText)
            ? stdErrText
            : ReadLogTailSafe(javaStdErrPath, 80);
        var combined = string.Join(
            Environment.NewLine,
            new[] { javaStdErrTail, stdOutText, latestLogTail }.Where(text => !string.IsNullOrWhiteSpace(text)));

        string? userMessage = null;
        var shouldRepair = false;
        var resetVersionArtifacts = false;
        var resetJavaRuntime = false;
        var resetCompatibilityLibraries = false;
        string? repairStageText = null;

        if (LaunchLogContainsAnyToken(
                combined,
                "Could not find or load main class",
                "ClassNotFoundException",
                "NoClassDefFoundError",
                "forge client jar не найден",
                "Error opening zip file",
                "Unable to access jarfile",
                "The system cannot find the file specified"))
        {
            userMessage = "Похоже, версия или её библиотеки скачались/собрались с ошибкой. Лаунчер попробует пересобрать установку.";
            shouldRepair = true;
            resetVersionArtifacts = true;
            repairStageText = "Восстанавливаю файлы версии...";
        }
        else if (LaunchLogContainsAnyToken(
                     combined,
                     "CAFixer is not available",
                     "com/turikhay/caf/util/Logger"))
        {
            userMessage = "Для старой версии не хватило библиотек совместимости. Лаунчер попробует восстановить их автоматически.";
            shouldRepair = true;
            resetVersionArtifacts = true;
            resetCompatibilityLibraries = true;
            repairStageText = "Восстанавливаю библиотеки совместимости...";
        }
        else if (LaunchLogContainsAnyToken(
                     combined,
                     "UnsupportedClassVersionError",
                     "has been compiled by a more recent version",
                     "A JNI error has occurred"))
        {
            userMessage = "Для этой версии была выбрана неподходящая Java. Лаунчер попробует заново подготовить Java runtime.";
            shouldRepair = true;
            resetJavaRuntime = true;
            repairStageText = "Переустанавливаю Java runtime...";
        }
        else if (LaunchLogContainsAnyToken(
                     combined,
                     "Could not reserve enough space",
                     "Invalid maximum heap size",
                     "Could not create the Java Virtual Machine"))
        {
            userMessage = "Запуск сорвался из-за Java или слишком агрессивных настроек памяти.";
            shouldRepair = true;
            resetJavaRuntime = true;
            repairStageText = "Чиню Java и настройки запуска...";
        }
        else if (LaunchLogContainsAnyToken(
                     combined,
                     "Scoreboard.func_96511_d",
                     "STeamsPacket"))
        {
            userMessage = $"У {resolvedVersionId} с некоторыми серверами есть известный legacy-краш по scoreboard/team packets.";
        }
        else if (LaunchLogContainsAnyToken(
                     combined,
                     "AuthenticationUnavailableException",
                     "Failed to read value Internal Server Error",
                     "Failed to fetch metadata: java.net.ConnectException"))
        {
            userMessage = "Сервис авторизации, скинов или bridge-метаданных временно не ответил.";
        }
        else if (LaunchLogContainsAnyToken(
                     combined,
                     "Could not decode textures payload"))
        {
            userMessage = "Сервер прислал битый payload текстур NPC. Это уже игровая ошибка на стороне сервера.";
        }

        return new ImmediateLaunchFailureAnalysis(
            userMessage,
            BuildLaunchFailureSnippet(combined),
            shouldRepair,
            resetVersionArtifacts,
            resetJavaRuntime,
            resetCompatibilityLibraries,
            repairStageText);
    }

    private static bool TryRepairImmediateLaunchFailure(
        string gameDirectory,
        string resolvedVersionId,
        string versionDirectory,
        string instanceDirectory,
        string? resolvedJavaExecutable,
        ImmediateLaunchFailureAnalysis analysis)
    {
        try
        {
            if (analysis.ResetVersionArtifacts)
            {
                TryDeleteDirectoryQuietly(versionDirectory);
                TryDeleteDirectoryQuietly(Path.Combine(gameDirectory, "natives", resolvedVersionId));
                TryDeleteDirectoryQuietly(Path.Combine(instanceDirectory, ".fabric", "processedMods"));
                TryDeleteFileQuietly(Path.Combine(instanceDirectory, "launch_command.txt"));
                TryDeleteFileQuietly(Path.Combine(instanceDirectory, "launch_diagnostics.txt"));
            }

            if (analysis.ResetCompatibilityLibraries)
            {
                var librariesDirectory = Path.Combine(gameDirectory, "libraries");
                TryDeleteFileQuietly(CombineWithRoot(librariesDirectory, "com/turikhay/ca-fixer/1.0/ca-fixer-1.0.jar"));
                TryDeleteFileQuietly(CombineWithRoot(librariesDirectory, "ru/tlauncher/patchy/1.0.0/patchy-1.0.0.jar"));
            }

            if (analysis.ResetDownloadedJavaRuntime)
            {
                TryDeleteDownloadedJavaRuntimeForExecutable(resolvedJavaExecutable);
            }

            TryDeleteFileQuietly(Path.Combine(AppContext.BaseDirectory, "_last_java_stdout.log"));
            TryDeleteFileQuietly(Path.Combine(AppContext.BaseDirectory, "_last_java_stderr.log"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDownloadedJavaRuntimeForExecutable(string? javaExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(javaExecutablePath))
        {
            return;
        }

        try
        {
            var runtimesRoot = Path.Combine(BaseStorageDirectory.Value, "java-runtimes");
            var fullPath = Path.GetFullPath(javaExecutablePath);
            if (!fullPath.StartsWith(runtimesRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var currentDirectory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrWhiteSpace(currentDirectory) &&
                   !string.Equals(currentDirectory, runtimesRoot, StringComparison.OrdinalIgnoreCase))
            {
                var directoryName = Path.GetFileName(currentDirectory);
                if (directoryName.StartsWith("temurin-", StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectoryQuietly(currentDirectory);
                    return;
                }

                currentDirectory = Path.GetDirectoryName(currentDirectory);
            }
        }
        catch
        {
            // ignore repair cleanup failures
        }
    }

    private static string ReadLogTailSafe(string path, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || maxLines < 1)
        {
            return string.Empty;
        }

        try
        {
            var queue = new Queue<string>(maxLines);
            foreach (var line in File.ReadLines(path))
            {
                if (queue.Count == maxLines)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(line);
            }

            return string.Join(Environment.NewLine, queue);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? BuildLaunchFailureSnippet(string combinedLogText)
    {
        if (string.IsNullOrWhiteSpace(combinedLogText))
        {
            return null;
        }

        var lines = combinedLogText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(5)
            .ToArray();

        if (lines.Length == 0)
        {
            return null;
        }

        return "Последние строки:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static bool LaunchLogContainsAnyToken(string? input, params string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        foreach (var token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(token) &&
                input.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryWriteJavaLaunchOutput(string path, string? text)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.WriteAllText(path, text ?? string.Empty);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static bool ShouldUseLegacyRuntimeCompatibility(string? versionId)
        => ShouldUseDirectLoopbackSkinHost(versionId);

    private static void WriteLaunchDiagnostics(
        string instanceDirectory,
        string gameDirectory,
        string resolvedVersionId,
        LaunchOptions options,
        string javaTemporaryDirectory,
        string? authlibInjectorPath,
        VesperSkinBridgePlan skinBridgePlan)
    {
        var lines = new[]
        {
            $"timestampUtc={DateTimeOffset.UtcNow:O}",
            $"versionId={resolvedVersionId}",
            $"username={options.Username}",
            $"profile={options.Profile}",
            $"hasSelectedSkin={!string.IsNullOrWhiteSpace(options.SelectedSkinPath)}",
            $"hasPrecomputedUserProperties={!string.IsNullOrWhiteSpace(options.PrecomputedUserPropertiesJson)}",
            $"offlineSkinSessionUuid={options.OfflineSkinSessionUuid ?? "-"}",
            $"hasVesperSession={options.VesperAuthSession != null}",
            $"vesperApiBaseUrl={options.VesperAuthSession?.ApiBaseUrl ?? "-"}",
            $"hasMicrosoftSession={options.MicrosoftSession != null}",
            $"authlibMajorVersion={skinBridgePlan.AuthlibMajorVersion?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
            $"vesperSkinBridgeEnabled={skinBridgePlan.Enabled}",
            $"vesperSkinBridgeReason={skinBridgePlan.Reason}",
            $"javaTempDirectory={javaTemporaryDirectory}",
            $"vesperElyAuthlibPath={skinBridgePlan.ElyAuthlibPath ?? "-"}",
            $"authlibInjectorPath={authlibInjectorPath ?? "-"}"
        };

        File.WriteAllLines(Path.Combine(instanceDirectory, "launch_diagnostics.txt"), lines);
        File.WriteAllLines(Path.Combine(gameDirectory, "launch_diagnostics.txt"), lines);
    }

    private static string GetJavaCompatiblePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        if (IsAsciiSafePath(fullPath))
        {
            return fullPath;
        }

        var shortPath = TryGetWindowsShortPath(fullPath);
        if (IsAsciiSafePath(shortPath))
        {
            return shortPath!;
        }

        var stagedPath = TryStageJavaCompatibleFile(fullPath);
        if (IsAsciiSafePath(stagedPath))
        {
            return stagedPath!;
        }

        return !string.IsNullOrWhiteSpace(shortPath) ? shortPath : fullPath;
    }

    private static bool IsAsciiSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var ch in path)
        {
            if (ch > sbyte.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private static string? TryStageJavaCompatibleFile(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var commonDocumentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (string.IsNullOrWhiteSpace(commonDocumentsDirectory))
            {
                return null;
            }

            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var targetDirectory = Path.Combine(commonDocumentsDirectory, "VesperLauncher", "javaagent");
            Directory.CreateDirectory(targetDirectory);

            var targetPath = Path.Combine(targetDirectory, fileName);
            if (!string.Equals(Path.GetFullPath(targetPath), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(fullPath, targetPath, true);
            }

            return targetPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWindowsShortPath(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(512);
            var written = GetShortPathName(fullPath, buffer, buffer.Capacity);
            return written > 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder shortPath,
        int shortPathBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static string ReplacePlaceholders(string text, IReadOnlyDictionary<string, string> replacements)
    {
        var resolved = text;
        foreach (var pair in replacements)
        {
            resolved = resolved.Replace($"${{{pair.Key}}}", pair.Value, StringComparison.Ordinal);
        }

        return resolved;
    }

    private static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        foreach (Match match in TokenRegex.Matches(commandLine))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static string NormalizeMinecraftBaseVersionId(string minecraftVersionId)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersionId))
        {
            throw new ArgumentException("Версия Minecraft не указана.", nameof(minecraftVersionId));
        }

        return minecraftVersionId.Trim();
    }

    private static IEnumerable<JsonElement> EnumerateArrayLike(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("value", out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in valueElement.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static bool TryParseOptiFineLoaderId(string loaderVersionId, out string optifineType, out string optifinePatch)
    {
        optifineType = string.Empty;
        optifinePatch = string.Empty;
        if (string.IsNullOrWhiteSpace(loaderVersionId))
        {
            return false;
        }

        var separatorIndex = loaderVersionId.IndexOf('|', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex >= loaderVersionId.Length - 1)
        {
            return false;
        }

        optifineType = loaderVersionId[..separatorIndex].Trim();
        optifinePatch = loaderVersionId[(separatorIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(optifineType) && !string.IsNullOrWhiteSpace(optifinePatch);
    }

    private static bool IsOptiFinePreviewLoaderId(string? loaderVersionId)
    {
        return TryParseOptiFineLoaderId(loaderVersionId ?? string.Empty, out _, out var optifinePatch) &&
               optifinePatch.StartsWith("pre", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureOptiFineTweakClass(JsonObject metadataNode)
    {
        if (metadataNode["arguments"] is not JsonObject argumentsNode)
        {
            argumentsNode = new JsonObject();
            metadataNode["arguments"] = argumentsNode;
        }

        if (argumentsNode["game"] is not JsonArray gameArguments)
        {
            gameArguments = new JsonArray();
            argumentsNode["game"] = gameArguments;
        }

        if (argumentsNode["jvm"] is not JsonArray)
        {
            argumentsNode["jvm"] = new JsonArray();
        }

        for (var index = 0; index < gameArguments.Count - 1; index++)
        {
            if (gameArguments[index] is not JsonValue keyValue ||
                gameArguments[index + 1] is not JsonValue valueValue ||
                !keyValue.TryGetValue<string>(out var keyString) ||
                !valueValue.TryGetValue<string>(out var valueString))
            {
                continue;
            }

            if (keyString.Equals("--tweakClass", StringComparison.Ordinal) &&
                valueString.Equals("optifine.OptiFineTweaker", StringComparison.Ordinal))
            {
                return;
            }
        }

        gameArguments.Add("--tweakClass");
        gameArguments.Add("optifine.OptiFineTweaker");
    }

    private static string ReadZipEntryText(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool TryGetStringNodeValue(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var stringValue))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(stringValue))
        {
            return false;
        }

        value = stringValue.Trim();
        return true;
    }

    private static string GetLibraryIdentity(JsonNode? libraryNode, int index)
    {
        if (libraryNode is JsonObject libraryObject &&
            TryGetStringNodeValue(libraryObject["name"], out var libraryName))
        {
            return libraryName;
        }

        return $"__library_{index}_{libraryNode?.GetType().Name ?? "null"}";
    }

    private sealed class ModLoaderVersionComparer : IComparer<string>
    {
        public static readonly ModLoaderVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var leftTokens = SplitVersionTokens(x);
            var rightTokens = SplitVersionTokens(y);
            var commonCount = Math.Min(leftTokens.Count, rightTokens.Count);
            for (var index = 0; index < commonCount; index++)
            {
                var leftToken = leftTokens[index];
                var rightToken = rightTokens[index];
                var tokenCompare = CompareVersionToken(leftToken, rightToken);
                if (tokenCompare != 0)
                {
                    return tokenCompare;
                }
            }

            return leftTokens.Count.CompareTo(rightTokens.Count);
        }

        private static List<string> SplitVersionTokens(string version)
        {
            return version
                .Replace('-', '.')
                .Replace('_', '.')
                .Replace('+', '.')
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        private static int CompareVersionToken(string leftToken, string rightToken)
        {
            if (int.TryParse(leftToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftNumber) &&
                int.TryParse(rightToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }

            return string.Compare(leftToken, rightToken, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string CombineWithRoot(string rootPath, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(rootPath, normalized);
    }

    private static string ResolveLibraryRelativePath(JsonElement library, JsonElement artifact)
    {
        var relativePathByName = ResolveLibraryPathFromName(library);
        if (!string.IsNullOrWhiteSpace(relativePathByName))
        {
            return NormalizeLibraryRelativePath(relativePathByName);
        }

        var relativePath = ResolveRelativeDownloadPath(artifact);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            return NormalizeLibraryRelativePath(relativePath);
        }

        return ResolveLibraryPathFromName(library);
    }

    private static string ResolveLibraryPathFromName(JsonElement library)
    {
        if (!library.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return BuildLibraryRelativePathFromName(nameElement.GetString() ?? string.Empty);
    }

    private static string BuildLibraryRelativePathFromName(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return string.Empty;
        }

        var ext = "jar";
        var normalizedName = libraryName;
        var extSeparatorIndex = normalizedName.IndexOf('@', StringComparison.Ordinal);
        if (extSeparatorIndex >= 0)
        {
            ext = normalizedName[(extSeparatorIndex + 1)..].Trim();
            normalizedName = normalizedName[..extSeparatorIndex];
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = "jar";
            }
        }

        var parts = normalizedName.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return string.Empty;
        }

        var groupPath = parts[0].Replace('.', '/');
        var artifactId = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? parts[3] : string.Empty;

        var fileName = string.IsNullOrWhiteSpace(classifier)
            ? $"{artifactId}-{version}.{ext}"
            : $"{artifactId}-{version}-{classifier}.{ext}";

        return $"{groupPath}/{artifactId}/{version}/{fileName}";
    }

    private static string NormalizeLibraryRelativePath(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["libraries/".Length..];
        }

        return normalized;
    }

    private static string? ResolveLibraryRepositoryUrl(JsonElement library)
    {
        if (!library.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var rawUrl = urlElement.GetString();
        return string.IsNullOrWhiteSpace(rawUrl) ? null : rawUrl.Trim();
    }

    private static string? ResolveLibraryRepositoryUrlFallback(string? repositoryUrl, string? libraryName)
    {
        if (!string.IsNullOrWhiteSpace(repositoryUrl) || string.IsNullOrWhiteSpace(libraryName))
        {
            return repositoryUrl;
        }

        if (libraryName.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase))
        {
            return ForgeMavenBaseUrl;
        }

        return repositoryUrl;
    }

    private static string? BuildRepositoryDownloadUrl(string? repositoryUrl, string relativePath)
    {
        var normalizedRelativePath = NormalizeLibraryRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(repositoryUrl))
        {
            var normalizedRepositoryUrl = repositoryUrl.Trim();
            if (Uri.TryCreate(normalizedRepositoryUrl, UriKind.Absolute, out var repositoryUri))
            {
                if (!normalizedRepositoryUrl.EndsWith("/", StringComparison.Ordinal))
                {
                    normalizedRepositoryUrl += "/";
                }

                return new Uri(new Uri(normalizedRepositoryUrl), normalizedRelativePath).ToString();
            }
        }

        return $"https://libraries.minecraft.net/{normalizedRelativePath}";
    }

    private static string NormalizeDownloadUrl(string rawUrl, string? repositoryUrl, string? fallbackRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl) &&
            Uri.TryCreate(rawUrl, UriKind.Absolute, out var absoluteUrl))
        {
            return absoluteUrl.ToString();
        }

        var relativePathCandidate = !string.IsNullOrWhiteSpace(rawUrl)
            ? rawUrl
            : fallbackRelativePath ?? string.Empty;

        var resolved = BuildRepositoryDownloadUrl(repositoryUrl, relativePathCandidate);
        return resolved ?? string.Empty;
    }

    private static bool TryCopyLibraryFromKnownSources(
        string versionId,
        string relativePath,
        string destinationPath,
        string? sourceGameDirectory = null)
    {
        if (File.Exists(destinationPath))
        {
            return true;
        }

        var normalizedRelativePath = NormalizeLibraryRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            return false;
        }

        foreach (var sourceRoot in EnumerateLibrarySourceRoots(versionId, sourceGameDirectory))
        {
            var sourcePath = CombineWithRoot(sourceRoot, normalizedRelativePath);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? throw new InvalidOperationException("РќРµРІР°Р»РёРґРЅС‹Р№ РїСѓС‚СЊ Р±РёР±Р»РёРѕС‚РµРєРё."));
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return true;
        }

        return File.Exists(destinationPath);
    }

    private static IEnumerable<string> EnumerateLibrarySourceRoots(string versionId, string? sourceGameDirectory = null)
    {
        var result = new List<string>();

        // 1. Libraries from selected external game directory
        if (!string.IsNullOrWhiteSpace(sourceGameDirectory))
        {
            result.Add(Path.Combine(sourceGameDirectory, "libraries"));
        }

        // 2. Libraries from other known game directories
        foreach (var gameDirectory in EnumerateKnownGameDirectories())
        {
            result.Add(Path.Combine(gameDirectory, "libraries"));
        }

        // 3. Explicit env override
        var envLibrariesPath = Environment.GetEnvironmentVariable("VESPER_MC_LIBRARIES");
        if (!string.IsNullOrWhiteSpace(envLibrariesPath))
        {
            result.Add(Environment.ExpandEnvironmentVariables(envLibrariesPath));
        }

        // 4. Local game data libraries
        result.Add(Path.Combine(BaseStorageDirectory.Value, "libraries"));

        // 5. Version-specific bundled libraries
        var bundledVersionDirectory = ResolveBundledVersionDirectory(versionId);
        if (!string.IsNullOrWhiteSpace(bundledVersionDirectory))
        {
            result.Add(Path.Combine(bundledVersionDirectory, "libraries"));
        }

        // 6. Global bundled libraries
        result.Add(Path.Combine(AppContext.BaseDirectory, "BundledLibraries"));

        return result
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void TryCopyBundledNatives(string versionId, string destinationDirectory, MinecraftVersionEntry sourceVersion)
    {
        var sourceDirectories = new List<string>();

        if (!string.IsNullOrWhiteSpace(sourceVersion.LocalMetadataPath))
        {
            var localVersionDirectory = Path.GetDirectoryName(sourceVersion.LocalMetadataPath);
            if (!string.IsNullOrWhiteSpace(localVersionDirectory))
            {
                sourceDirectories.Add(Path.Combine(localVersionDirectory, "natives"));
            }
        }

        if (!string.IsNullOrWhiteSpace(sourceVersion.SourceGameDirectory))
        {
            sourceDirectories.Add(Path.Combine(sourceVersion.SourceGameDirectory, "versions", versionId, "natives"));
        }

        foreach (var gameDirectory in EnumerateKnownGameDirectories())
        {
            sourceDirectories.Add(Path.Combine(gameDirectory, "versions", versionId, "natives"));
        }

        var bundledVersionDirectory = ResolveBundledVersionDirectory(versionId);
        if (!string.IsNullOrWhiteSpace(bundledVersionDirectory))
        {
            sourceDirectories.Add(Path.Combine(bundledVersionDirectory, "natives"));
        }

        foreach (var nativesSourceDirectory in sourceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(nativesSourceDirectory))
            {
                continue;
            }

            foreach (var sourcePath in Directory.EnumerateFiles(nativesSourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(sourcePath);
                if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".so", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }
    }

    private static string ResolveRelativeDownloadPath(JsonElement downloadElement)
    {
        if (downloadElement.TryGetProperty("path", out var pathElement) &&
            pathElement.ValueKind == JsonValueKind.String)
        {
            var path = pathElement.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }

        if (downloadElement.TryGetProperty("url", out var urlElement) &&
            urlElement.ValueKind == JsonValueKind.String)
        {
            var url = urlElement.GetString() ?? string.Empty;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var pathFromUrl = uri.AbsolutePath.TrimStart('/');
                if (!string.IsNullOrWhiteSpace(pathFromUrl))
                {
                    return pathFromUrl;
                }
            }
        }

        return string.Empty;
    }

    private static string ResolveBaseStorageDirectory()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "GameData"));
        }

        candidates.Add(Path.Combine(LauncherDataPaths.GetPreferredDataDirectory(), "GameData"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "GameData"));

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            candidates.Add(Path.Combine(localApplicationData, "CheatCraftLauncherData"));
        }

        candidates.Add(Path.Combine(Path.GetTempPath(), "CheatCraftLauncherData"));

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(comparer))
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // Fallback when the launcher folder is read-only.
            }
        }

        throw new IOException("Не удалось подготовить папку данных лаунчера.");
    }

    private static string SanitizePathSegment(string value)
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

    private static void InitializeVersionInstanceDirectory(string sharedGameDirectory, string instanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sharedGameDirectory) || string.IsNullOrWhiteSpace(instanceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(instanceDirectory);

        foreach (var fileName in new[] { "servers.dat", "servers.dat_old" })
        {
            var sourcePath = Path.Combine(sharedGameDirectory, fileName);
            var destinationPath = Path.Combine(instanceDirectory, fileName);
            if (File.Exists(sourcePath) && !File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }

        foreach (var sharedDirectoryName in new[] { "shaderpacks", "resourcepacks" })
        {
            var sourceDirectory = Path.Combine(sharedGameDirectory, sharedDirectoryName);
            var destinationDirectory = Path.Combine(instanceDirectory, sharedDirectoryName);
            if (!Directory.Exists(sourceDirectory))
            {
                continue;
            }

            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
                if (!File.Exists(destinationPath))
                {
                    File.Copy(sourceFile, destinationPath, overwrite: false);
                }
            }
        }

        EnsureSharedDirectoryLink(sharedGameDirectory, instanceDirectory, "saves");
        EnsureMinecraftPerformanceOptions(sharedGameDirectory);
        EnsureMinecraftPerformanceOptions(instanceDirectory);
    }

    private static void EnsureSharedDirectoryLink(string sharedGameDirectory, string instanceDirectory, string directoryName)
    {
        var sharedDirectory = Path.Combine(sharedGameDirectory, directoryName);
        var instanceSubdirectory = Path.Combine(instanceDirectory, directoryName);

        Directory.CreateDirectory(sharedDirectory);

        try
        {
            if (Directory.Exists(instanceSubdirectory))
            {
                var attributes = File.GetAttributes(instanceSubdirectory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                MergeDirectoryContents(instanceSubdirectory, sharedDirectory);
                Directory.Delete(instanceSubdirectory, recursive: true);
            }
            else if (File.Exists(instanceSubdirectory))
            {
                File.Delete(instanceSubdirectory);
            }
        }
        catch
        {
            return;
        }

        if (TryCreateDirectoryJunction(instanceSubdirectory, sharedDirectory))
        {
            return;
        }

        Directory.CreateDirectory(instanceSubdirectory);
        MergeDirectoryContents(sharedDirectory, instanceSubdirectory);
    }

    private static void MergeDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        Directory.CreateDirectory(destinationDirectory);

        foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory));
            MergeDirectoryContents(sourceSubdirectory, destinationSubdirectory);
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            if (!File.Exists(destinationFile))
            {
                File.Copy(sourceFile, destinationFile, overwrite: false);
            }
        }
    }

    private sealed class OptiFineVersionComparer : IComparer<string>
    {
        public static readonly OptiFineVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var left = Parse(x);
            var right = Parse(y);

            var generationCompare = ModLoaderVersionComparer.Instance.Compare(left.GenerationId, right.GenerationId);
            if (generationCompare != 0)
            {
                return generationCompare;
            }

            if (left.IsPreview != right.IsPreview)
            {
                return left.IsPreview ? -1 : 1;
            }

            if (left.IsPreview)
            {
                var previewCompare = left.PreviewNumber.CompareTo(right.PreviewNumber);
                if (previewCompare != 0)
                {
                    return previewCompare;
                }
            }

            return string.Compare(left.RawId, right.RawId, StringComparison.OrdinalIgnoreCase);
        }

        private static ParsedOptiFineVersion Parse(string loaderVersionId)
        {
            if (!TryParseOptiFineLoaderId(loaderVersionId, out var optifineType, out var optifinePatch))
            {
                return new ParsedOptiFineVersion(loaderVersionId, loaderVersionId, false, 0);
            }

            var isPreview = optifinePatch.StartsWith("pre", StringComparison.OrdinalIgnoreCase);
            var previewNumber = 0;
            if (isPreview)
            {
                var digits = new string(optifinePatch.Where(char.IsDigit).ToArray());
                _ = int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out previewNumber);
            }

            var generationId = isPreview
                ? optifineType
                : $"{optifineType}_{optifinePatch}";

            return new ParsedOptiFineVersion(loaderVersionId, generationId, isPreview, previewNumber);
        }

        private readonly record struct ParsedOptiFineVersion(
            string RawId,
            string GenerationId,
            bool IsPreview,
            int PreviewNumber);
    }

    private static bool TryCreateDirectoryJunction(string junctionPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(junctionPath) ?? throw new InvalidOperationException("Некорректный путь ссылки на saves."));

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0 && Directory.Exists(junctionPath);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureProfileDirectories(
        string gameDirectory,
        LauncherProfile profile,
        string? preferredLanguageCode = null)
    {
        var commonDirectories = new[]
        {
            "mods",
            "resourcepacks",
            "shaderpacks",
            "saves",
            "config",
            "logs",
            "screenshots"
        };

        foreach (var directory in commonDirectories)
        {
            Directory.CreateDirectory(Path.Combine(gameDirectory, directory));
        }

        if (profile == LauncherProfile.CheatClient)
        {
            Directory.CreateDirectory(Path.Combine(gameDirectory, "cheat-libs"));
        }

        EnsureMinecraftLanguageOptions(gameDirectory, preferredLanguageCode);
        EnsureMinecraftPerformanceOptions(gameDirectory);
    }

    private sealed record ModrinthProjectMetadata(
        string ProjectId,
        string Slug,
        string Title,
        string? Description,
        string? IconUrl);

    private sealed record ModrinthFileInfo(
        string FileName,
        string DownloadUrl,
        string? FileSha1);

    private sealed record ModrinthVersionFileInfo(
        string FileName,
        string DownloadUrl,
        string? FileSha1,
        IReadOnlyList<string> RequiredDependencyProjectIds);

    private sealed record ModrinthResolvedMod(
        string ProjectId,
        string DisplayName,
        string FileName,
        string DownloadUrl,
        string? FileSha1,
        IReadOnlyList<string> RequiredDependencyProjectIds,
        IReadOnlyList<string> ProjectAliases);

    private sealed class ModrinthCatalogCacheState
    {
        public Dictionary<string, ModrinthCatalogCacheEntry> Entries { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ModrinthCatalogCacheEntry(
        DateTimeOffset CachedAtUtc,
        List<RecommendedModCatalogItem> Items);

    private sealed record ModrinthVersionResolutionCacheEntry(
        DateTimeOffset CachedAtUtc,
        ModrinthVersionFileInfo? Version);

    private sealed class MineSkinCacheState
    {
        public Dictionary<string, MineSkinCacheEntry> Entries { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class MineSkinCacheEntry
    {
        public string TextureValue { get; init; } = string.Empty;
        public string? TextureSignature { get; init; }
        public string? TextureUrl { get; init; }
        public string? ProfileId { get; init; }
        public DateTimeOffset CachedAtUtc { get; init; }
    }

    private sealed record AuthlibInjectorRelease(
        string FileName,
        string DownloadUrl,
        string? Sha256);

    private readonly record struct VesperSkinBridgePlan(
        bool Enabled,
        int? AuthlibMajorVersion,
        string? ElyAuthlibPath,
        string Reason);

    private sealed record OptiFineLaunchwrapperLibrary(
        string LibraryName,
        string RelativePath,
        string AbsolutePath);

    private sealed record DownloadEntry(string Url, string DestinationPath, string? Sha1, string Label)
    {
        public static DownloadEntry FromJson(
            JsonElement element,
            string destinationPath,
            string label,
            string? repositoryUrl = null,
            string? fallbackRelativePath = null)
        {
            var rawUrl = element.GetProperty("url").GetString() ?? string.Empty;
            var url = NormalizeDownloadUrl(rawUrl, repositoryUrl, fallbackRelativePath);
            var sha1 = element.TryGetProperty("sha1", out var sha1Element)
                ? sha1Element.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidDataException($"Р’ metadata РѕС‚СЃСѓС‚СЃС‚РІСѓРµС‚ URL РґР»СЏ С„Р°Р№Р»Р°: {destinationPath}");
            }

            return new DownloadEntry(url, destinationPath, sha1, label);
        }
    }
}



