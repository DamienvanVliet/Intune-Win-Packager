# Changelog

## [1.1.10] - 2026-04-14

### Added
- Performance: packaging now reuses fresh preflight results for unchanged configuration (up to 10 minutes), avoiding repeated full preflight on consecutive runs.
- Performance: smart staging copy now uses adaptive parallel file copy for faster source staging on large app sources.
- Performance: avoids unnecessary recursive package-artifact scan when smart staging is off or output is already inside source.
- Adds timing logs for source preparation and packaging process duration to make bottlenecks visible.

## [1.1.9] - 2026-04-14

### Added
- Fixes too-small text sizing in Comfortable density mode.
- Increases default Comfortable scale, control font sizes, and log font for better readability without switching to Compact.

## [1.1.8] - 2026-04-14

### Added
- Fixes dark-mode visibility for dropdown controls in Settings and form-heavy sections.
- Adds a resizable splitter in Packaging so logs and result panels can be widened interactively.
- Improves Packaging Logs readability for long commands with horizontal scrolling and denser typography.
- Reduces overall UI footprint with tighter comfortable/compact density values and smaller control minimum heights.

## [1.1.7] - 2026-04-14

### Added
- Fix dark-mode dropdown contrast/readability across settings and rules panels.\n- Improve packaging logs readability (wrapping + larger visible area).\n- Make UI density more compact and reduce oversized layout feel.

## [1.1.6] - 2026-04-14

### Added
- Fix updater reliability and release selection logic.\n- Add core localization key mapping for validation/preflight messages.\n- Add tab icons and compact density mode.\n- Add UI regression smoke tests and updater regression tests.

## [1.1.5] - 2026-04-13

### Added
- Universal Intune preparation flow for MSI, EXE, APPX/MSIX and script installers.
- Removed app-specific behavior and hardcoded defaults; command/detection handling is type-aware.
- Added structured Intune install rules, requirement rules, and detection rules (MSI/File/Registry/Script).
- Added Intune preparation outputs: .intune.json metadata and .intune-checklist.md portal checklist.
- Added requirement validation and preflight checks for production-safe packaging.

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog and this project follows Semantic Versioning.

## [1.1.4] - 2026-04-12

### Added
- Added a full in-app preflight system (Option 7) with one-click run:
  - tool health probe
  - source/setup/output checks
  - output write-permission test
  - output drive free-space check
  - install/uninstall command checks
- Added a dedicated **Preflight Checks** panel and a **Run Preflight** action in Guided Start.
- Added automatic preflight execution before packaging begins, with clear blocking/warning messages in logs and UI.
- Added preflight unit tests for blocking and passing scenarios.

### Changed
- One-click tool install now verifies that IntuneWinAppUtil.exe is executable after locate/install (health check), not only present on disk.
- If a preflight was already run and configuration changes, preflight is invalidated and asks for a rerun to avoid stale readiness state.

## [1.1.3] - 2026-04-12

### Added
- Added silent update install policy toggle in settings for managed/internal environments.

### Fixed
- Added SHA-256 verification for downloaded update installers before launch.
- Added retry with backoff for update network requests (check and download endpoints).
- Improved updater errors with clearer technical error codes for troubleshooting.

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
