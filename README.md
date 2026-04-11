# Intune Win Packager

Intune Win Packager is a local Windows 11 desktop app that converts `.msi` and `.exe` installers to `.intunewin` by using Microsoft's official `IntuneWinAppUtil.exe`.

## Why This Tool

Packaging Win32 apps for Intune should be fast, clear, and repeatable.
This app gives admins one polished screen to:

- select installer + source + output
- auto-generate command suggestions
- validate everything before packaging
- run the official Microsoft packager with live logs
- save reusable profiles and package history

## Core Features

- Drag and drop for `.msi` / `.exe`
- Source, setup, and output pickers
- Installer type detection (MSI/EXE)
- MSI metadata inspection and command prefills
- EXE silent-install templates
- Inline validation with clear error messages
- Step-based packaging progress + percent
- Live process output log panel
- Result panel with output path and quick open
- Local JSON settings/profiles/history
- Auto-locate tool path + one-click tool install
- Low Impact Packaging Mode (less CPU pressure)
- In-app updates with changelog + installer launch

## UI Walkthrough (Current Build)

Based on the current UI screens:

1. Header + status strip
2. Package Configuration card (drag/drop + browse)
3. Install Commands card (install/uninstall)
4. Guided Start card (readiness + quick actions)
5. Settings & Profiles card (tool path, profiles, low-impact mode)
6. App Updates card (check, install, changelog)
7. Validation card (instant required-field checks)
8. Result card (status + progress)
9. Packaging Logs panel (timestamped output)
10. Recent Packages card (local run history)

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
2. Drop/select installer file
3. Confirm source/setup/output
4. Check install/uninstall command fields
5. Click `Start Packaging`
6. Follow progress and logs
7. Open output folder after success

## Update Check Notes

If you see `HTTP 404` in App Updates, the release feed is not publicly reachable.
Common causes:

- repository/releases are private
- no published release yet

The app now avoids overriding packaging progress status when update checks fail or return no update.

## Build Installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

Output:

- `artifacts\installer\IntuneWinPackager-Setup-<version>.exe`

## Publish Release + Changelog

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-update.ps1 -Version 1.1.1 -ReleaseNotes "Your release notes here"
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
