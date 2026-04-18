# Changelog

## [1.1.40] - 2026-04-18

### Added
- Hotfix updater launch reliability.
- Added launch handshake so app only closes when deferred updater startup is confirmed.
- Added PowerShell fallback scheduler when batch launcher does not initialize.
- Kept lock-safe behavior to avoid installer code 32 race conditions while preserving no-forced-app-close policy.

## [1.1.39] - 2026-04-18

### Added
- Updater hotfix (launcher reliability).\n\n- Fixed deferred updater launcher hang where the app closed but installer never started.\n- Removed global image-name wait and now wait on the exact process PID + executable lock only.\n- Added bounded retry timeouts so updater always proceeds instead of waiting indefinitely.\n- Added regression checks for deferred launcher script behavior.

## [1.1.38] - 2026-04-18

### Added
- Store UX update.\n\n- Added a dedicated Store tab so package discovery is separated from Updates & Changes.\n- Kept the Microsoft Store-like flow: select app, review details, then one-click import into Packaging.\n- Updates & Changes tab is now focused on app update status, changelog, and recent packages.\n- Updated localization keys and UI smoke tests for the new tab structure.

## [1.1.37] - 2026-04-18

### Fixed
- Prevented the app from silently closing when startup initialization steps fail.
- Added startup resilience: settings/profile/history/catalog load failures are now isolated and logged instead of crashing startup.
- Added an explicit single-instance warning message instead of immediate silent shutdown when another instance/mutex is detected.

## [1.1.36] - 2026-04-18

### Fixed
- Removed forced reboot path for normal interactive self-updates by restoring split installer behavior (`restartreplace` is now silent-only again for the app executable).
- Hardened deferred updater launch with an explicit file-unlock gate: setup now waits until `IntuneWinPackager.App.exe` can be opened exclusively before starting, reducing recurring `DeleteFile failed; code 32` races.
- Added updater regression coverage for the deferred launcher script to ensure unlock waiting behavior remains in place.

## [1.1.35] - 2026-04-18

### Fixed
- Restored lock-safe self-update behavior for `IntuneWinPackager.App.exe` by using `restartreplace` in the installer file map (prevents recurring `DeleteFile failed; code 32` during in-place upgrades).
- In-app non-silent updater now also passes `NORESTART` plus no-close-app flags for consistent update behavior.

## [1.1.34] - 2026-04-18

### Fixed
- Update check now prefers the highest installable release when a newer release exists without installer assets, so users are no longer blocked unnecessarily.
- Release notes in the updater are normalized so literal escaped sequences like `\n` render as real line breaks.
- Added updater regression tests for installable-release fallback and release-note newline normalization.

## [1.1.33] - 2026-04-18

### Added
- Product Store Phase 3 baseline: added Scoop and NuGet v3 sources in the catalog pipeline.
- Added configured source discovery for Chocolatey (`choco source list`) and NuGet (`dotnet nuget list source`) with safe fallback behavior.
- Added SQLite-backed store search cache with stale policy and background refresh support.
- Added provider diagnostics telemetry (requests, failures, consecutive failures, timeout count, last error, last success/failure).
- Added new store source toggles in UI and localization for Scoop/NuGet.
- Added tests for NuGet search normalization, cache hit behavior, and provider diagnostics recording.

### Changed
- Catalog download now resolves Chocolatey package URLs from the selected configured Chocolatey source when explicit installer URL is missing.
- Store source selection validation now includes Scoop and NuGet.

## [1.1.32] - 2026-04-18

### Added
- Product Store Phase 2: canonical package identity (`publisher + product + release channel`) for normalized cross-source matching.
- Product Store Phase 2: structured installer variants per package (source/channel/type/arch/scope/url/hash/signing metadata).
- Product Store Phase 2: deterministic detection strategy mapping per installer variant:
  - MSI: ProductCode-first.
  - EXE: strict exact registry equality script (DisplayName, Publisher, DisplayVersion).
  - APPX/MSIX: exact identity + version script.
- Added canonical/variant-aware profile matching and promotion so prepared profiles survive source differences.
- Added tests for canonical source merge, deterministic EXE detection mapping, and multi-asset GitHub variant mapping.

### Changed
- Store search now merges duplicate apps across providers into one logical package while preserving source variants.
- Store download flow now includes fallback to alternate source variants when the primary source fails.

## [1.1.31] - 2026-04-18

### Added
- Product Store: added optional GitHub Releases source.
- Product Store: WinGet now discovers configured non-explicit sources (for example winget + msstore).
- Product Store: added clear readiness states (Ready, Needs review, Blocked).
- Store reliability: removed automatic EXE silent-switch auto-verification without evidence.
- Added phase backlog document for Store scaling work.

## [1.1.30] - 2026-04-18

### Added
- Detection rules rebuilt to deterministic Intune-first behavior.
- EXE detection now requires exact registry DisplayVersion equality (no fuzzy matching).
- MSI detection remains ProductCode-first and authoritative.
- Script detection is blocked by validation/preflight unless truly required (APPX/MSIX or explicit script type).
- Added stricter preflight and validation checks for generic file/registry detection to prevent false positives.

## [1.1.29] - 2026-04-17

### Added
- Fixed WinGet package download failures (e.g., Spotify) when WinGet download exits with hash mismatch/no installer metadata.
- Added direct URL fallback from WinGet output with clear unverified-hash messaging.
- Improved process output capture reliability to prevent intermittent 'Collection was modified' failures.
- Added automated tests for WinGet hash-mismatch and no-installer-url scenarios.

## [1.1.28] - 2026-04-17

### Added
- Hardened catalog script detection with weighted matching (name/key/token/publisher/version).
- Reduced false positives from display-name-only matching.
- Added regression tests for catalog detection script generation.

## [1.1.27] - 2026-04-17

### Added
- Added a local Package Store profile cache (package id + version + installer hash) so prepared package commands and detection can be reused with confidence markers (`Verified`, `Likely`, `Manual review`).
- Added one-click Store flow improvements: `Prepare In Packaging` now first tries exact local prepared profiles, then falls back to download + auto-prepare.
- Added Store trust signals in UI: hash verification, vendor signature, silent switch probe evidence, and detection readiness badges.
- Added upgrade-aware Store indicators to highlight when a newer catalog version is available compared to a locally prepared version.
- Added Store advanced-details toggle to keep default view simpler and reduce technical clutter.

### Changed
- Package catalog download metadata now tracks installer SHA256, source hash-verification signal, and vendor signing details.
- Store icons are now cached locally to improve scroll stability and reduce repeated remote icon fetches.
- Catalog downloads now persist profile snapshots used to feed trust badges and reuse logic.

### Fixed
- Improved command suggestion metadata flow by exposing parameter-probe detection state to downstream UI/profile trust scoring.
- Strengthened UI smoke tests for new Store advanced toggle and trust/upgrade indicators.

## [1.1.26] - 2026-04-17

### Fixed
- Catalog download flow now auto-completes packaging defaults more aggressively for EXE installers: placeholder install/uninstall commands are replaced automatically.
- Added automatic fallback uninstall command generation (registry-based lookup) when EXE uninstall placeholders remain.
- Added automatic script detection rule generation from catalog metadata when no detection rule is present.
- `Fix For Me` now also treats placeholder commands as missing and refreshes them from installer suggestions.

## [1.1.25] - 2026-04-17

### Fixed
- Improved Start Packaging behavior when configuration is incomplete: button click now shows a clear blocking reason in status/log instead of appearing to do nothing.
- Start Packaging is now disabled only while busy, so users can always click once to get actionable feedback on missing fields.

## [1.1.24] - 2026-04-17

### Fixed
- Prevented update-time shortcut write conflicts (`IPersistFile::Save failed`, `0x80070020`) by skipping Start Menu/Desktop shortcut creation when the `.lnk` already exists.
- Keeps first-install shortcut creation behavior unchanged while avoiding unnecessary shortcut rewrites during in-place updates.
- Removed forced reboot path for normal interactive updates by limiting `restartreplace` fallback to silent policy-driven updates only.

## [1.1.23] - 2026-04-17

### Fixed
- Removed installer-driven close-apps prompting again to prevent unrelated process prompts (like `vgc`) during update preparation.
- Added targeted locked-file fallback for `IntuneWinPackager.App.exe` using `restartreplace`, so update can proceed without hard `code 32` failure when that executable is temporarily locked.
- Silent in-app updater now again enforces `/NOCLOSEAPPLICATIONS` to match no-forced-close update policy.

## [1.1.22] - 2026-04-17

### Fixed
- Added an installer-level update rescue path for locked `IntuneWinPackager.App.exe` scenarios: setup now enables controlled application closing and only targets `IntuneWinPackager.App.exe` for close detection.
- Added installer mutex coordination (`AppMutex=IntuneWinPackager.AppMutex`) and app mutex registration to improve update reliability and avoid concurrent app/update conflicts.
- Updated silent in-app updater arguments to allow controlled close behavior (`/CLOSEAPPLICATIONS`) while still preventing automatic app restart after setup.
- Enabled setup logging to improve troubleshooting when update/replace issues are reported from production machines.

## [1.1.21] - 2026-04-17

### Fixed
- Replaced PowerShell-based deferred updater launch with a dedicated `cmd` launcher script that waits for the current app process to fully exit before starting setup.
- Removed unsafe immediate-launch fallback path that could still trigger installer file lock errors (`DeleteFile failed; code 32`) while the app executable was in use.
- Added safer scheduling failure handling so update flow surfaces a clear error instead of starting setup too early.

## [1.1.20] - 2026-04-17

### Fixed
- Updater now schedules installer launch only after the current app process fully exits, preventing file lock errors (`code 32`) on `IntuneWinPackager.App.exe`.
- Kept no-force-close behavior while removing update race condition between running app and installer file replacement.

## [1.1.19] - 2026-04-17

### Fixed
- Disabled Inno Setup automatic application closing/restarting in the installer (`CloseApplications=no`, `RestartApplications=no`) to prevent unrelated services/apps like `vgc` from being targeted.
- Silent in-app updater now explicitly passes `/NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS`.

## [1.1.18] - 2026-04-17

### Added
- Added a dedicated `Download` action in Package Store details. It downloads the selected package artifact and automatically loads it into the Packaging workflow.
- Added Package Store download support for WinGet and Chocolatey sources (including archive extraction and installer discovery where possible).

### Changed
- Package Store now auto-prepares Packaging fields from the real downloaded installer, so install/uninstall commands are generated from actual installer metadata instead of template placeholders.
- Improved Package Store icon fallback behavior for more packages using homepage/id-derived favicon resolution.

### Fixed
- Silent app update no longer uses forced close flags (`/CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS`), avoiding unnecessary forced process/service shutdown side effects.

## [1.1.17] - 2026-04-17

### Added
- Added a full in-app Package Store (catalog) with WinGet + Chocolatey search. - Added package cards showing app icon (with fallback tile), app name, package id, source, and build version. - Added package detail panel with install/uninstall command templates, detection guidance, metadata notes, and quick actions.

### Changed
- Added a new package catalog service layer (Models/Core/Infrastructure) to keep store logic modular and extensible.
- Added a `Use In Packager` action that applies selected catalog hints into the main packaging workflow for faster setup.

## [1.1.16] - 2026-04-17

### Added
- Added resilient in-app update notifications with periodic background checks. - Cached latest known version in app settings so update banner can still show after restart. - Improved startup/manual update flow via shared update info application logic.

## [1.1.15] - 2026-04-16

### Added
- Sharpened Intune detection UX with plain-language recommendations per installer type. - Added operator guidance (Exists vs comparison) directly in detection forms. - Hid irrelevant comparison value fields when Exists is selected to reduce confusion.

## [1.1.14] - 2026-04-16

### Added
- Added Delete Profile button in Settings > Tooling & Profiles. - Added profile deletion backend support and status feedback. - Detection Rule dropdowns now show plain-language labels instead of technical enum names. - Added in-context guidance text explaining when to choose MSI/File/Registry/Script detection and operator usage.

## [1.1.13] - 2026-04-16

### Added
- Simplified EXE preset dropdown labels to plain language (less technical). - Added small helper text under EXE preset selection with clear guidance for non-technical users. - Kept behavior unchanged while reducing cognitive load in advanced settings.

## [1.1.12] - 2026-04-16

### Added
- Simplified Packaging UI: essential fields stay visible, advanced Intune settings moved behind one optional section. - Reduced technical wording in command areas and added clearer helper text. - Added clear EXE silent-switch verification status (local history match vs manual verification pending). - Removed Compact density mode; Comfortable is now the only density.

## [1.1.11] - 2026-04-16

### Added
- Added static installer fingerprinting (MSI, MSIX/APPX, Inno, NSIS, InstallShield, Burn, Squirrel and more) with confidence scoring. - Added non-installing parameter probe (/?, -?, /help, --help) and command suggestion extraction. - Added verified knowledge cache by SHA256 and product version for install/uninstall/detection reuse. - Added startup update check and clear in-app update-available notification banner.

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
