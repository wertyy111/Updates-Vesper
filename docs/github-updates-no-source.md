# GitHub updates without publishing source code

This launcher can use a separate public GitHub repository only for update files.

## Safe structure

- Keep your real launcher source code local or in a private repository.
- Create a second public repository such as `vesper-launcher-updates`.
- Publish only these files to the public updates repository:
  - the built `setup.exe` in GitHub Releases
  - `appcast.xml`
  - `appcast.xml.signature`
  - markdown changelog files for each version

Your source code does not need to be pushed to the public updates repository.

## One-time setup

1. Create an empty public repository for updates only.
2. Add a `README` when creating it so the default branch already exists.
3. In repository settings, enable GitHub Pages from the `main` branch and `/docs` folder.
4. Create a fine-grained GitHub token with access only to the updates repository and `Contents: Read and write`.
5. Generate NetSparkle keys locally:

```powershell
.\scripts\New-NetSparkleKeys.ps1
```

6. Copy the printed public key into [update-config.json](/d:/ЛАУНЧЕР%20ДЛЯ%20МАЙНКРАФТА/VesperLauncher/update-config.json).
7. Set the app cast URL in [update-config.json](/d:/ЛАУНЧЕР%20ДЛЯ%20МАЙНКРАФТА/VesperLauncher/update-config.json) to:

```text
https://YOUR_GITHUB_USERNAME.github.io/YOUR_UPDATES_REPO/appcast.xml
```

## SmartScreen

If `setup.exe` is unsigned, Windows SmartScreen can show the "Windows protected your PC" warning.

To reduce that warning for real users:

1. Sign `setup.exe` with a trusted code-signing certificate or Microsoft Trusted Signing.
2. Timestamp the signature.
3. Publish the signed file consistently from the same publisher.
4. Let SmartScreen reputation build over time.

This repository now includes a local signing helper:

```powershell
.\scripts\Sign-Setup.ps1 `
  -FilePath 'D:\builds\setup.exe' `
  -CertificatePath 'D:\certs\vesper-launcher.pfx' `
  -CertificatePassword 'YOUR_PASSWORD'
```

Or sign with a certificate already installed in the Windows certificate store:

```powershell
.\scripts\Sign-Setup.ps1 `
  -FilePath 'D:\builds\setup.exe' `
  -CertificateThumbprint 'YOUR_CERT_THUMBPRINT'
```

Or sign with Microsoft Trusted Signing / Artifact Signing:

```powershell
.\scripts\Install-ArtifactSigningTools.ps1

.\scripts\New-ArtifactSigningMetadata.ps1 `
  -Endpoint 'https://YOUR_REGION.codesigning.azure.net' `
  -CodeSigningAccountName 'YOUR_ACCOUNT_NAME' `
  -CertificateProfileName 'YOUR_CERT_PROFILE'

.\scripts\Sign-Setup.ps1 `
  -FilePath 'D:\builds\setup.exe' `
  -ArtifactSigningMetadataPath '%LOCALAPPDATA%\VesperLauncher\ArtifactSigning\metadata.json'
```

## Publishing a new launcher version

1. Build a new installer with the dedicated single-file build script:

```powershell
.\scripts\Build-Installer.ps1 -UpdateWindowsSetup
```

This produces:

- `windows/setup.exe`
- `_temp_build/release_<VERSION>/VesperLauncherSetup-<VERSION>.exe`
- a fresh `_windows_build/payload_latest.zip` made from a `win-x64` self-contained launcher publish

The setup executable is self-contained, and the embedded launcher payload includes the .NET runtime. Users should not need to install .NET/C# separately.

Do not publish the tiny framework-dependent apphost from a plain `dotnet build`.
2. Sign `setup.exe` before publishing if you want to reduce SmartScreen warnings.
3. Bump the launcher version in [VesperLauncher.csproj](/d:/ЛАУНЧЕР%20ДЛЯ%20МАЙНКРАФТА/VesperLauncher/VesperLauncher.csproj).
4. Prepare a markdown changelog file for the exact app version, for example `1.0.1.md`.
5. Run:

```powershell
.\scripts\Publish-GitHubUpdate.ps1 `
  -Version 1.0.1 `
  -SetupPath 'D:\ЛАУНЧЕР ДЛЯ МАЙНКРАФТА\_temp_build\release_1.0.1\VesperLauncherSetup-1.0.1.exe' `
  -ReleaseNotesPath 'D:\builds\1.0.1.md' `
  -GitHubOwner 'YOUR_GITHUB_USERNAME' `
  -GitHubRepo 'YOUR_UPDATES_REPO' `
  -GitHubToken 'YOUR_FINE_GRAINED_TOKEN'
```

The script will:

- generate a signed `appcast.xml`
- upload the installer to GitHub Releases
- update `docs/appcast.xml` and `docs/appcast.xml.signature`
- publish changelog files to GitHub Pages

If you already have a trusted code-signing certificate, the same publish command can sign the staged installer before upload:

```powershell
.\scripts\Publish-GitHubUpdate.ps1 `
  -Version 1.0.1 `
  -SetupPath 'D:\ЛАУНЧЕР ДЛЯ МАЙНКРАФТА\_temp_build\release_1.0.1\VesperLauncherSetup-1.0.1.exe' `
  -ReleaseNotesPath 'D:\builds\1.0.1.md' `
  -GitHubOwner 'YOUR_GITHUB_USERNAME' `
  -GitHubRepo 'YOUR_UPDATES_REPO' `
  -GitHubToken 'YOUR_FINE_GRAINED_TOKEN' `
  -CertificatePath 'D:\certs\vesper-launcher.pfx' `
  -CertificatePassword 'YOUR_PASSWORD'
```

Or publish using Microsoft Trusted Signing / Artifact Signing:

```powershell
.\scripts\Publish-GitHubUpdate.ps1 `
  -Version 1.0.1 `
  -SetupPath 'D:\ЛАУНЧЕР ДЛЯ МАЙНКРАФТА\_temp_build\release_1.0.1\VesperLauncherSetup-1.0.1.exe' `
  -ReleaseNotesPath 'D:\builds\1.0.1.md' `
  -GitHubOwner 'YOUR_GITHUB_USERNAME' `
  -GitHubRepo 'YOUR_UPDATES_REPO' `
  -GitHubToken 'YOUR_FINE_GRAINED_TOKEN' `
  -ArtifactSigningMetadataPath '%LOCALAPPDATA%\VesperLauncher\ArtifactSigning\metadata.json'
```

After that, users who already have the updater-enabled launcher will download the new installer on the next app start.

## Keep private

- Never publish `NetSparkle_Ed25519.priv`.
- Do not put your source code into the public updates repository.
- Do not publish `.pdb` files with release builds.
- If a token is shared in chat, rotate it and create a new one.

