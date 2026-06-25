# Cross-platform release CI

Workflow: `.github/workflows/cross-platform-release.yml`.

It builds:

- `VesperLauncher-<version>-linux-x64.AppImage` on `ubuntu-latest`.
- `VesperLauncher-<version>-osx-x64.dmg` on `macos-latest`.
- `VesperLauncher-<version>-osx-arm64.dmg` on `macos-latest`.

Manual run:

1. Open GitHub Actions.
2. Run `Cross-platform release`.
3. Set `tag`, for example `beta-1.0`.
4. Set `version`, for example `beta-1.0`.
5. Keep `upload_release=true` to upload assets to the GitHub Release.

Tag run:

- Push a tag matching `beta-*` or `v*`.
- The workflow uploads assets to the release with the same tag.

macOS signing secrets:

- `MACOS_CERTIFICATE_P12_BASE64`: base64-encoded Developer ID Application `.p12`.
- `MACOS_CERTIFICATE_PASSWORD`: password for the `.p12`.
- `MACOS_CODESIGN_IDENTITY`: exact Developer ID Application identity name.
- `MACOS_KEYCHAIN_PASSWORD`: optional temporary CI keychain password.

macOS notarization secrets:

- `MACOS_NOTARY_KEY_BASE64`: base64-encoded App Store Connect API `.p8` key.
- `MACOS_NOTARY_KEY_ID`: App Store Connect API key id.
- `MACOS_NOTARY_ISSUER_ID`: App Store Connect issuer id.

If signing secrets are missing, CI still builds an unsigned DMG. If notarization secrets are missing, CI skips notarization.
