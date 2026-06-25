# Changelog

## 2026-06-23

- Replaced the legacy NetSparkle updater integration with Velopack.
- Added early `VelopackApp.Build().Run()` startup hook in the main launcher executable.
- Added a Velopack-based startup update service with GitHub Releases source support.
- Added a safe local/draft/publish release script: `scripts/Publish-VelopackUpdate.ps1`.
- Moved update publishing tokens out of config and into environment variables only.
- Added Velopack update safety documentation.
