# Intune Win Packager

Intune Win Packager is a local Windows 11 desktop app that converts setup packages such as `.msi`, `.exe`, `.appx/.msix`, and deployment scripts to `.intunewin` by using Microsoft's official `IntuneWinAppUtil.exe`.

## Why This Tool

Packaging Win32 apps for Intune should be fast, clear, and repeatable.
This app gives admins one polished screen to:

- select installer + source + output
- auto-generate command suggestions
- validate everything before packaging
- run the official Microsoft packager with live logs
- save reusable profiles and package history

## Core Features

- Drag and drop for `.msi`, `.exe`, `.appx/.msix`, and script-based setup files
- Source, setup, and output pickers
- Installer type detection (MSI/EXE/APPX-MSIX/script)
- MSI metadata inspection, command prefills, and MSI product-code detection suggestions
- EXE installer framework engine (Inno/NSIS/InstallShield/WiX/unknown) with explicit switch verification gates
- APPX/MSIX command + detection-script suggestions (package identity driven when available)
- Structured Intune rule editor: install context, restart behavior, max runtime, detection rule type (MSI/File/Registry/Script)
- Structured Intune requirements editor: architecture, minimum OS, device requirements, optional requirement script
- Smart source staging to avoid recursive packaging when output artifacts are inside source
- Intune sidecar metadata export (`.intune.json`) next to every successful `.intunewin`
- Intune portal checklist export (`.intune-checklist.md`) with exact post-package portal settings to fill in
- Inline validation with clear error messages
- Step-based packaging progress + percent
- Live process output log panel
- Result panel with output path and quick open
- Local JSON settings/profiles/history
- Auto-locate tool path + one-click tool install
- Tool health verification after auto-locate/install (ensures executable is usable)
- Preflight checks panel (tool/paths/access/disk/commands) with clear pass/warn/error output
- Requirement preflight validation (architecture/OS/numeric constraints + requirement script placeholder checks)
- Automatic preflight gate before packaging starts
- Low Impact Packaging Mode (less CPU pressure)
- In-app updates with changelog + installer launch
- SHA-256 integrity verification before update install
- Retry/backoff for transient update network errors
- Optional silent update install mode for managed environments

## UI Walkthrough (Current Build)

Based on the current UI screens:

1. Header + status strip
2. Package Configuration card (drag/drop + browse)
3. Install Commands card (install/uninstall)
4. Guided Start card (readiness + quick actions)
5. Settings & Profiles card (tool path, profiles, low-impact mode)
6. App Updates card (check, install, changelog)
7. Preflight Checks card (readiness + blocking issues)
8. Validation card (instant required-field checks)
9. Result card (status + progress)
10. Packaging Logs panel (timestamped output)
11. Recent Packages card (local run history)

## Install (End Users)

Download the latest installer:

- [Latest Release](https://github.com/DamienvanVliet/Intune-Win-Packager/releases/latest)

Then install and launch from Start Menu:

- `Intune Win Packager`

## Run From Source (Developers)

```powershell
git clone https://github.com/DamienvanVliet/Intune-Win-Packager.git
cd Intune-Win-Packager
dotnet restore
dotnet build IntuneWinPackager.sln
dotnet run --project IntuneWinPackager.App
```

## Typical Packaging Flow

1. Set `IntuneWinAppUtil.exe` path (`Auto Locate`, `Install Tool`, or `Browse Tool`)
2. Drop/select setup package
3. Confirm source/setup/output
4. Configure Intune install + detection rules (MSI/File/Registry/Script)
5. Run `Preflight` (or let it run automatically when packaging starts)
6. Verify EXE silent switches if required by the selected profile
7. Click `Start Packaging`
8. Follow progress and logs
9. Use all outputs: `.intunewin`, generated `.intune.json` metadata, and `.intune-checklist.md` portal checklist

## Update Check Notes

If you see `HTTP 404` in App Updates, the release feed is not publicly reachable.
Common causes:

- repository/releases are private
- no published release yet

The app now avoids overriding packaging progress status when update checks fail or return no update.
Update installs are hash-verified (SHA-256) before launch, and transient network failures use retry with backoff.

## Build Installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

Output:

- `artifacts\installer\IntuneWinPackager-Setup-<version>.exe`

## Publish Release + Changelog

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-update.ps1 -Version 1.1.4 -ReleaseNotes "Your release notes here"
```

This workflow updates app versioning, keeps changelog aligned, and publishes installer assets used by in-app updates.

## Repo Cleanup Policy

Short answer: for users, only the installer `.exe` is needed.
For GitHub, keep source + scripts + docs so the app can be maintained.

Do keep:

- `IntuneWinPackager.*` projects
- `scripts/`
- `installer/IntuneWinPackager.iss`
- `README.md`, `CHANGELOG.md`

Do not keep in git:

- `bin/`, `obj/`, `artifacts/`, temporary output

## Project Layout

- `IntuneWinPackager.App` - WPF UI + MVVM composition
- `IntuneWinPackager.Core` - business rules and workflow interfaces
- `IntuneWinPackager.Infrastructure` - process, persistence, installer/update services
- `IntuneWinPackager.Models` - strongly-typed entities and DTOs
- `IntuneWinPackager.Tests` - unit tests

## Local Data Location

`%LOCALAPPDATA%\IntuneWinPackager`

- `settings.json`
- `profiles.json`
- `history.json`

## Test

```powershell
dotnet test IntuneWinPackager.sln
```

## License

This project is licensed under the MIT License.
See [LICENSE](LICENSE).
