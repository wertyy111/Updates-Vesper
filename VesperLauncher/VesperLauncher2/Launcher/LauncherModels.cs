using System;
using System.Collections.Generic;

namespace VesperLauncher.Launcher;

public enum LauncherProfile
{
    Vanilla,
    CheatClient
}

public sealed record MinecraftVersionEntry(
    string Id,
    string Type,
    DateTimeOffset ReleaseTime,
    string MetadataUrl,
    string MetadataSha1,
    string? LocalMetadataPath = null,
    string? SourceGameDirectory = null,
    string? BaseVersionId = null)
{
    public string DisplayName => $"{Id} ({Type})";
}

public enum ModLoaderKind
{
    Forge,
    Fabric,
    OptiFine
}

public sealed record ModLoaderVersionEntry(
    string Id,
    string DisplayName,
    DateTimeOffset ReleaseTime);

public sealed record ModLoaderInstallResult(
    string InstalledVersionId,
    string VersionJsonPath);

public sealed record RecommendedModProject(
    string ProjectId,
    string DisplayName,
    string Description,
    string? ResolvedFileName = null,
    string? ResolvedDownloadUrl = null,
    string? ResolvedFileSha1 = null,
    IReadOnlyList<string>? RequiredDependencyProjectIds = null)
{
    public string Summary => $"{DisplayName} - {Description}";
}

public enum RecommendedCatalogContentKind
{
    Mod,
    Shader,
    ResourcePack,
    Modpack
}

public sealed record RecommendedModCatalogItem(
    string ProjectId,
    string DisplayName,
    string Description,
    string? IconUrl,
    string PackSummary,
    RecommendedCatalogContentKind ContentKind = RecommendedCatalogContentKind.Mod,
    string ActionText = "Установить",
    string? SourceIconUrl = null,
    string BadgeText = "",
    string BadgeBackgroundHex = "#24324A",
    string BadgeForegroundHex = "#F3FAFF",
    bool IsFavorite = false,
    bool IsInstalled = false,
    string? InstalledFilePath = null,
    string? ResolvedFileName = null,
    string? ResolvedDownloadUrl = null,
    string? ResolvedFileSha1 = null,
    IReadOnlyList<string>? RequiredDependencyProjectIds = null);

public sealed record InstalledModProjectInfo(
    string ProjectId,
    string DisplayName,
    string FileName,
    string FilePath,
    bool Downloaded,
    IReadOnlyList<string>? ProjectAliases = null);

public sealed record CatalogAssetInstallResult(
    string DestinationDirectory,
    string FileName,
    bool Downloaded);

public sealed record ModPackInstallResult(
    string ModsDirectory,
    int DownloadedCount,
    int SkippedCount,
    int MissingCount,
    IReadOnlyList<string> DownloadedFiles,
    IReadOnlyList<string> SkippedProjects,
    IReadOnlyList<string> MissingProjects,
    IReadOnlyList<InstalledModProjectInfo> InstalledProjects);

public sealed class LaunchOptions
{
    public required string Username { get; init; }
    public required string JavaExecutable { get; init; }
    public required MinecraftVersionEntry Version { get; init; }
    public required LauncherProfile Profile { get; init; }
    public int MemoryMb { get; init; }
    public string ExtraJvmArgs { get; init; } = string.Empty;
    public string? MinecraftLanguageCode { get; init; }
    public string? SelectedSkinPath { get; init; }
    public bool SelectedSkinIsSlim { get; init; }
    public string? PrecomputedUserPropertiesJson { get; init; }
    public string? OfflineSkinSessionUuid { get; init; }
    public VesperAuthSession? VesperAuthSession { get; init; }
    public string? DirectConnectServerAddress { get; init; }
    public int? DirectConnectServerPort { get; init; }
    public MicrosoftSession? MicrosoftSession { get; init; }
}

public sealed record LauncherProgress(string Stage, double Current, double Total);

public sealed record LaunchResult(
    string GameDirectory,
    int? ProcessId,
    bool RequiresLauncherRestart = false,
    string? RestartMessage = null);

public sealed record OfflineSkinLaunchData(
    string UserPropertiesJson,
    string? SessionUuid = null,
    VesperAuthSession? VesperAuthSession = null);

public sealed record VesperAuthPreparedProfile(
    VesperAuthSession Session,
    string UserPropertiesJson);

public sealed record VesperAuthSession(
    string Uuid,
    string Username,
    string AccessToken,
    string ApiBaseUrl);

