# Cross-Platform Audit

Date: 2026-06-22

## Current Stack

- `VesperLauncher/` is currently a Windows launcher application with a mixed UI stack.
- Legacy UI/backend host: WPF (`UseWPF=true`, `App.xaml`, `MainWindow.xaml`, `StartupUpdateWindow`).
- Newer shell/frontend: Photino + React/Vite (`Program.cs`, `PhotinoHost/`, `UserInterface/src`).
- Backend logic is still mixed into WPF-facing classes, especially `MainWindow.xaml.cs` and `Launcher/MinecraftLauncherService.cs`.
- Current project target is `net10.0-windows`, not a cross-platform target.

## Largest Monoliths

| File | Approx. lines | Current role | Status |
| --- | ---: | --- | --- |
| `VesperLauncher/MainWindow.xaml.cs` | 13,594 | WPF window, account, friends, versions, skins, mods, launch orchestration | Must be split; WPF-bound |
| `VesperLauncher/Launcher/MinecraftLauncherService.cs` | 12,936 | Minecraft versions, downloads, Java, launch args, skin bridge, mod catalog | Must be split; partly WPF-bound |
| `VesperLauncher/MainWindow.xaml` | 6,286 | Legacy WPF visual tree | Remove after React shell owns UI |
| `backend/vesper-account-api/src/index.js` | 2,466 | Cloudflare Worker API | Separate backend; not part of WPF removal |
| `windows/VesperNet.Service/VesperNetControlService.cs` | 1,275 | Windows overlay service | Windows-only platform feature |

## WPF-Specific Areas

These block cross-platform targeting until isolated or removed:

- `VesperLauncher/App.xaml`
- `VesperLauncher/App.xaml.cs`
- `VesperLauncher/MainWindow.xaml`
- `VesperLauncher/MainWindow.xaml.cs`
- `VesperLauncher/MainWindow.Appearance.cs`
- `VesperLauncher/MainWindow.PhotinoBridge.cs`
- `VesperLauncher/MainWindow.WindowChrome.cs`
- `VesperLauncher/StartupUpdateWindow.xaml`
- `VesperLauncher/StartupUpdateWindow.cs`
- `VesperLauncher/PhotinoHost/LauncherWpfBackendHost.cs`
- `VesperLauncher/LauncherAutoUpdateService.cs`
- `VesperLauncher/Launcher/MinecraftLauncherService.cs`

Important detail: `MinecraftLauncherService.cs` imports `System.Windows.Media` and `System.Windows.Media.Imaging` for skin bitmap work. That means the Minecraft backend is not fully cross-platform yet.

## Windows-Specific Areas

These should move behind platform interfaces or remain Windows-only packages:

- `VesperLauncher/WindowsUninstallRegistration.cs`
- `VesperLauncher/Program.cs` native window shaping and DWM/user32/gdi32 calls
- `VesperLauncher/MainWindow.WindowChrome.cs`
- `VesperLauncher/MainWindow.xaml.cs` service registry checks and Explorer launch helpers
- `windows/VesperNet.Service/`
- `windows/installer-src/`
- `scripts/Install-VesperNetService.ps1`
- `scripts/Uninstall-VesperNetService.ps1`
- `scripts/Build-Installer.ps1`
- `scripts/Sign-Setup.ps1`
- `scripts/Build-VesperMultiIcon.ps1`
- `scripts/Crop-PngToAlpha.ps1`

## Already Cross-Platform Or Close

- `VesperLauncher/Core/Logger.cs`
- `VesperLauncher/Core/EventManager.cs`
- `VesperLauncher/Core/DependencyContainer.cs`
- `VesperLauncher/Core/AppDiagnostics.cs` with guarded OS-specific branches
- `VesperLauncher/Utils/PathHelper.cs`
- `VesperLauncher/Launcher/ProcessMonitor.cs`
- `VesperLauncher/UserInterface/src/**`
- `backend/vesper-account-api/**`

## Can Be Extracted First

These are good first targets because they can be made independent from WPF:

- Path and file helpers
- Hash/checksum helpers
- Config loading/saving
- Generic cache manager
- HTTP/download layer
- JVM argument builder
- Process monitor
- Version state machine
- Java detector
- Mod catalog cache models

## Depends On WPF

These need UI isolation or replacement:

- Main window navigation and panels
- Message boxes and dialogs
- WPF timers/Dispatcher usage
- WPF bitmap processing for avatars and skin previews
- WPF 3D/software skin preview rendering
- Startup update window
- WPF backend host used by Photino

## Depends On Windows

These need platform interfaces or Windows-only capability flags:

- Windows uninstall registration
- VesperNet Windows service and Wintun
- Installer/uninstaller
- Registry access
- Win32/DWM window chrome
- Explorer folder opening
- Windows short path handling in launch service

## Migration Notes

- Do not remove WPF until the React/Photino command bridge can replace every launcher workflow.
- Keep Windows-only features available on Windows, but expose disabled/unsupported states on Linux/macOS.
- Move skin image processing out of WPF `BitmapSource` before `MinecraftLauncherService` can become a true backend service.
- Keep `Updates-Vesper` separate from source code; `Vesper-Launcher` is now the private source repository.

