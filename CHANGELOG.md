# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog and this project follows Semantic Versioning.

## [1.1.2] - 2026-04-12

### Added
- Added an MIT `LICENSE` file for clear open-source usage terms.

### Fixed
- Update checks now fall back to the public releases list endpoint when `releases/latest` is unavailable.
- Improved release payload parsing so update detection works consistently across REST and GitHub CLI payload shapes.
- Update logs now show both current and latest versions when no newer update is found.
- Fixed updater installer launch flow by fully closing the downloaded file before launch (prevents stall at `Launching update installer...`).

## [1.1.1] - 2026-04-12

### Fixed
- Update checks no longer override packaging progress/status in the Result panel.
- Result panel no longer appears as stuck on `Ready (0%)` before a packaging run starts.
- Improved update-check resilience for private/non-public release feeds with GitHub CLI fallback.

### Changed
- Refined update messaging and logging behavior so non-packaging actions keep the previous workflow state.
- Improved README clarity for end users and developers, including run/install flow and repository cleanup guidance.

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
