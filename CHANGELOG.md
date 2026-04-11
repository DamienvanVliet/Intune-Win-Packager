# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog and this project follows Semantic Versioning.

## [1.1.0] - 2026-04-11

### Added
- Step-based packaging progress with percentage and current stage detail.
- In-app update section with:
  - Check for updates
  - Install update
  - Current/latest version display
  - Changelog view from release notes
- App update service that checks GitHub latest release and downloads/launches installer updates.
- Low Impact Packaging Mode toggle (default on) to reduce PC slowdown during packaging.

### Changed
- Packaging workflow now reports phase updates (validation, compression, finalize, completion).
- Process runner now lowers packager process priority when low impact mode is enabled.

### Notes
- To make in-app updates available, publish a GitHub Release with the installer `.exe` attached and release notes.

## [1.0.0] - 2026-04-11

### Added
- Initial production-ready local WPF app for packaging `.msi`/`.exe` into `.intunewin`.
- MVVM architecture with separate App/Core/Infrastructure/Models/Test projects.
- Tool auto-locate and one-click install for `IntuneWinAppUtil.exe`.
- Settings/profiles/history persistence and live packaging logs.
- Installer build pipeline using Inno Setup.
