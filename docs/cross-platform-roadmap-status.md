# Cross-Platform Roadmap Status

Date: 2026-06-23

## Summary

The Windows launcher is still WPF-backed, but a separate cross-platform Photino shell project now exists and builds for Linux/macOS. The migration path is to keep moving backend logic into cross-platform services, then retire the hidden WPF backend on Windows.

## Stage 0: Audit

Status: completed as a repo document.

Output:

- `docs/cross-platform-audit.md`

Findings:

- WPF is still central to the launcher.
- `MainWindow.xaml.cs` and `MinecraftLauncherService.cs` are the main monoliths.
- `MinecraftLauncherService.cs` is not clean backend because it still uses WPF bitmap types.
- Windows-only features are spread across launcher, installer, VesperNet service, scripts, and native window code.

## Stage 1: Target Architecture

Status: partially started.

Implemented:

- `Core/Logger.cs`
- `Core/AppDiagnostics.cs`
- `Core/EventManager.cs`
- `Core/DependencyContainer.cs`
- `Utils/PathHelper.cs`
- `Utils/HashHelper.cs`
- `Storage/FileManager.cs`
- `Storage/CacheManager.cs`

Not implemented yet:

- `AppHost`
- `ConfigManager`
- `PhotinoLauncherShell`
- `FrontendBridge`
- `CommandRouter`
- `LauncherStateProvider`
- `LauncherCommandHandler`
- `MinecraftLauncher`
- `VersionManager`
- `ModManager`
- `PerformanceOptimizer`
- `VesperHttpClient`
- `DownloadManager`
- `UpdateChecker`
- `ApiService`
- `SkinManager`
- `SkinCache`
- `SkinRenderer`
- `SkinUploader`
- `DatabaseManager`

## Stage 2: Stabilization Without Deepening WPF

Status: partially started.

### TASK-03: Close Game Button

Status: implemented first pass.

Implemented:

- `Launcher/ProcessMonitor.cs`
- `Process.Exited`
- polling fallback every 2 seconds
- graceful close with `CloseMainWindow()`
- forced fallback with `Kill(entireProcessTree: true)`
- `MainWindow` now uses `ProcessMonitor.IsRunning` for game-running state

Needs verification:

- Build check
- Manual launch/close test
- Manual close-from-Minecraft test

### TASK-04: Log Paths

Status: implemented first pass.

Implemented:

- cross-platform path helper
- log session directories under user data
- username/path segment sanitization
- logger rotation
- `Program.cs`, `App.xaml.cs`, and `MainWindow.xaml.cs` now write through `Logger`

Needs verification:

- Confirm actual runtime path on Windows
- Confirm path behavior on Linux/macOS later

### TASK-02: Weak PC Startup

Status: partially implemented.

Implemented:

- startup diagnostics for OS, runtime, .NET, architecture, RAM, working set, elevation, base path
- existing global exception handlers remain in `App.xaml.cs`
- Photino shell logs fatal unhandled exceptions

Not implemented yet:

- user-friendly crash report window/page in React shell
- AppHost-level startup wrapper
- low-memory mode flags
- removal of WPF-only startup behavior

Update window audit:

- `StartupUpdateWindow.xaml` currently looks like a newer visual design, but it is excluded from WPF compilation by `VesperLauncher.csproj`.
- The runtime startup update window is built in `StartupUpdateWindow.cs`.
- This is acceptable short term because the migration goal is to remove WPF startup UI rather than deepen it, but do not rely on edits to `StartupUpdateWindow.xaml` until the project file changes.

### TASK-01: RAM

Status: not started.

Needed:

- timer ownership audit
- event subscription cleanup audit
- cache TTL implementation
- bitmap/image disposal review
- RAM cache cleanup after game exit

## Stage 3: Minecraft Launch

Status: started.

Implemented:

- `JvmArgBuilder`
- `JavaDetector`
- `ProcessMonitor`
- `LauncherModels`

Next classes:

- `MinecraftLauncher`
- extraction from `MinecraftLauncherService.cs`

Progress:

- `MinecraftLauncherService.cs` still mixes launch, downloads, versions, skins, and mods.
- WPF bitmap dependency was removed from launch skin preparation. Prepared skin PNGs now use `SixLabors.ImageSharp 3.1.12` and byte-based `SkinTexture` processing instead of `BitmapSource`/`PngBitmapEncoder`.
- `JavaDetector.cs` now exists as a cross-platform service. It checks `JAVA_HOME`, `PATH`, Windows Java install roots, Linux JVM roots, and macOS JVM roots.
- `JvmArgBuilder.cs`, `ProcessMonitor.cs`, and launcher models are included in the cross-platform shell build.

Risks:

- The service is still too large and should be split after the WPF dependency cuts are complete.

## Stage 4: Versions

Status: started.

Needed:

- `VersionManager`
- install/play/update states
- checksum integrity
- cache-first loading
- background refresh

Implemented:

- `VersionStateMachine.cs`
- installed/not-installed/downloading/update-available state shape
- `.minecraft/versions/{version}/{version}.json` and `.jar` checks
- optional SHA-1 integrity check from version JSON

## Stage 5: Skins

Status: not started.

Needed:

- `SkinCache`
- `SkinRenderer`
- `SkinManager`
- `SkinUploader`

Important correction:

- Do not promise Hypixel support for Vesper-hosted skins. Online-mode public servers use Mojang-signed textures.

## Stage 6: Mods

Status: not started.

Needed:

- `ModManager`
- Modrinth/Forge/Fabric catalog extraction
- catalog cache
- optimization mod recommendations

## Stage 7: Network/API/Download

Status: not started.

Needed:

- `VesperHttpClient`
- `DownloadManager`
- `ApiService`
- `UpdateChecker`

## Stage 8: Platform Layer

Status: started.

Implemented:

- `Platform/PlatformKind.cs`
- `Platform/PlatformFeatureSet.cs`
- `Platform/IPlatformService.cs`
- `Platform/IPlatformProcessService.cs`
- `Platform/IPlatformPathService.cs`
- `Platform/PlatformService.cs`
- `Platform/PlatformServiceFactory.cs`
- `Platform/PlatformProcessService.cs`
- `Platform/PlatformPathService.cs`
- `Platform/LauncherDataPaths.cs`
- `Platform/PlatformDependencyRegistration.cs`
- Windows/Linux/macOS detection
- platform data/cache/logs/Minecraft directory resolution
- cross-platform folder/URL opening via Windows shell, macOS `open`, Linux `xdg-open`
- feature flags for Windows-only installer/VesperNet/window shaping
- launcher data compatibility paths:
  - new platform user-data directory
  - old install-local `.launcher-data`
  - old Windows `%AppData%/VesperLauncher`
- Microsoft auth cache now reads/writes through platform data paths
- Vesper skin registry and auth HTTP diagnostics now read/write through platform data paths
- bundled `skin-sync.json` and `account-sync.json` are copied on all platforms, not Windows only
- `MinecraftLauncherService` now uses platform data/cache paths for:
  - version manifest cache
  - MineSkin cache
  - temporary launcher downloads
  - Forge installer workspace/cache
  - authlib-injector storage
  - Linux/macOS `.minecraft` directory discovery
  - cross-platform writable `GameData` fallback while keeping Windows install-local `GameData` compatibility
- `App.xaml.cs` registers Windows uninstall metadata only when the platform supports Windows installer features.
- `Program.cs` and `LauncherAutoUpdateService.cs` skip Velopack startup/update flow when the platform feature set marks auto-update unsupported.
- settings "open game directory" now uses the platform folder opener instead of direct `Process.Start(... UseShellExecute = true)`.
- VesperNet service diagnostics/timer/overlay availability now return a disabled state on platforms without VesperNet support instead of touching Windows service registry or polling the local Windows service.
- `PhotinoHost/ILauncherBackendHost.cs` and `PhotinoHost/LauncherBackendHostFactory.cs` were added.
- `Program.cs` now depends on `ILauncherBackendHost` instead of directly constructing `LauncherWpfBackendHost`, so the WPF backend can be replaced by a pure cross-platform backend later.
- `Program.cs` now guards Win32/DWM window shaping, DPI registry probing, window handle lookup, and native drag calls behind `SupportsNativeWindowShaping`.
- `MainWindow.PhotinoCommandAdapter.cs` was added. WPF event-handler calls from the React/Photino command bridge are now centralized in one adapter file.
- `MainWindow.PhotinoBridge.cs` no longer creates `RoutedEventArgs` or temporary WPF `Button` instances while routing React commands.
- `PhotinoState/LauncherStateProvider.cs`, `LauncherSnapshot.cs`, and `LauncherSnapshotParts.cs` were added.
- The root Photino snapshot is now a typed WPF-free state model assembled through `LauncherStateProvider`; section snapshots are still produced by `MainWindow` and need to be moved next.
- `PhotinoHost/LauncherFallbackBackendHost.cs` was added. On non-Windows/cross-platform builds it provides a WPF-free backend snapshot and safe command handling so the React/Photino shell can start without `MainWindow`.
- `VesperLauncher.CrossPlatform.csproj` was added. It targets `net10.0`, has no `UseWPF`, and explicitly includes only cross-platform-safe source files.
- `scripts/Publish-CrossPlatformShell.ps1` was added for Linux/macOS shell publishing.

Next:

- replace direct `Process.Start(... UseShellExecute = true)` calls with `IPlatformProcessService`
- continue replacing direct `%AppData%`/`ApplicationData` path usage with `IPlatformPathService`
- expose unsupported VesperNet/install features as disabled states on Linux/macOS
- move Win32/DWM window shaping behind a Windows-only shell adapter
- move remaining Win32/DWM declarations from `Program.cs` into a Windows-specific shell adapter
- move Java agent staging and remaining Windows-only launch helpers behind platform-specific services
- extract `MainWindow.PhotinoBridge.cs` command handlers into a WPF-free `CommandRouter`
- split snapshot creation away from WPF controls so React state can be produced by backend services
- move `BuildPhotinoMainSnapshot`, account/settings/skin/mods/friends snapshots from `MainWindow` into typed WPF-free providers one section at a time
- replace `LauncherWpfBackendHost` with a pure backend host after state/command extraction

## Stage 9: Remove WPF

Status: in progress.

Blocked by:

- Windows runtime still uses `LauncherWpfBackendHost`.
- React/Photino bridge does not own all workflows yet.
- `MainWindow` still owns most launcher behavior.
- The cross-platform fallback backend can open the shell, but launch/version download/mod/skin/account workflows still need real service implementations.

## Stage 10: Verification

Status: partially available locally.

Latest local check:

- Windows/WPF build succeeds with custom `obj/bin` output path.
- Cross-platform shell build succeeds with `VesperLauncher.CrossPlatform.csproj`.
- Cross-platform shell publish succeeds for:
  - `linux-x64`
  - `osx-x64`
  - `osx-arm64`
- Command used through temporary `V:` subst path to avoid MSBuild quoting issues with the workspace path:
  `dotnet build V:/VesperLauncher/VesperLauncher.csproj -c Debug -p:SkipFrontendBuild=true`
- Only current warning: `Open.NAT 2.1.0` restores via .NET Framework assets and may not be fully compatible with `net10.0-windows7.0`.
- `%LOCALAPPDATA%/VesperCodexBuild/cross-platform-stage*` temporary outputs were removed after verification because the C: drive had reached 0 bytes free.
- Latest verification also used a temporary restore cache at `_build_verify/nuget-packages` because the repo-local `.nuget` directory has broken ACL inheritance for newly restored packages.

Required later:

- normal build without custom `obj/bin` workaround
- launch launcher
- launch Minecraft
- close Minecraft manually
- load versions
- test skins
- RAM idle/active measurement
- Linux/macOS compatibility checks

