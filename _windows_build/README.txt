Setup contents:
- setup.exe (installer)
- payload.zip (self-contained launcher files with bundled .NET runtime, no user data)

Usage:
- Double-click setup.exe
- Optional custom install dir: setup.exe --dir "C:\Path\To\Install"
- Silent update install: setup.exe /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS
- Build fresh installer/payload: .\scripts\Build-Installer.ps1 -UpdateWindowsSetup

Privacy/first run:
- On first run the launcher starts clean (no saved nicknames, friends, or UI state).
- Local cached auth token (msa-token.json) and vesper-auth.log are cleared on first run.
