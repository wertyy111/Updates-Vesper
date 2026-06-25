# Vesper Project Structure

This repository is split into four source layers:

- `VesperLauncher/` - WPF launcher application.
- `windows/VesperNet.Service/` - local Windows service used by VesperNet.
- `windows/installer-src/` - WinForms installer source.
- `backend/vesper-account-api/` - Cloudflare Worker account API and D1 migrations.

Generated artifacts should stay out of source:

- `_temp_build/`, `_build_verify/`, `_tmp_probe/`, `_tmp_catalog_probe/`
- `bin/`, `obj/`
- `*.log`
- `windows/setup.exe`
- `_windows_build/payload*.zip`

The only intentionally kept file under `_windows_build/` is the current payload zip used by the installer build script. It is treated as a local build input, not as source code.

## Internal UI Framework Direction

The launcher UI should continue moving toward a small internal WPF style layer:

- shared glass brushes and panel chrome live in theme resources;
- repeated panels use the same glass material rules;
- feature logic stays outside visual resources;
- large workflows should move out of `MainWindow.xaml.cs` into focused services or view models when touched for real behavior changes.

Do not rewrite the whole window in one pass. Split by stable ownership:

- account/session UI;
- version and launch workflow;
- mods/catalog workflow;
- friends and relay workflow;
- skin/background customization;
- installer/update workflow.

This keeps visual experiments safe while the current launcher logic remains intact.

## Local Verification

Use these checks after structural changes:

```powershell
$env:DOTNET_CLI_HOME=(Join-Path $PWD '.dotnet_home')
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:NUGET_PACKAGES=(Join-Path $PWD '.nuget')
dotnet build 'VesperLauncher\VesperLauncher.csproj' -c Debug -nologo
dotnet build 'windows\VesperNet.Service\VesperNet.Service.csproj' -c Debug -nologo
dotnet build 'windows\installer-src\Installer.csproj' -c Debug -nologo
```

