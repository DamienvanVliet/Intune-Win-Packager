# Intune Win Packager

Intune Win Packager is a local Windows desktop app that converts `.msi` and `.exe` installers into `.intunewin` packages by calling Microsoft's official `IntuneWinAppUtil.exe`.

## Why This App

This tool is built for Intune administrators who want a fast, local, no-cloud workflow to package Win32 apps.

- Uses the official Microsoft packaging engine (`IntuneWinAppUtil.exe`)
- Works fully offline after setup
- Gives clear validation, logs, progress, and results
- Saves reusable profiles and packaging history

## Functionality Overview

- Drag-and-drop or browse for `.msi` and `.exe` installers
- Configure Source folder, Setup file, and Output folder
- Auto-detect installer type (MSI/EXE)
- MSI metadata inspection with command suggestions
- EXE install/uninstall command editing
- Immediate input validation before packaging starts
- Real-time process log streaming during packaging
- Step-based progress bar with percentage + active step detail
- Local settings/profile/history persistence (`JSON`)
- Low Impact Packaging Mode to reduce CPU impact while packing
- Tool auto-locate plus one-click tool install
- In-app update check with changelog and one-click installer launch
- Open output folder after successful packaging

## Screenshots

Screenshots are intentionally not included yet.

When you share images, we will add them here in this section so new users can immediately see:
- Main dashboard
- Packaging in-progress state
- Success result state
- Validation/error state

## Quick Start (Beginner Friendly)

### 1) Prerequisites

- Windows 10 or 11
- .NET 8 SDK
- Git (only needed if you want to clone)

Install missing tools with `winget`:

```powershell
winget install --id Microsoft.DotNet.SDK.8 --source winget
winget install --id Git.Git --source winget
```

### 2) Clone the repository

Open PowerShell and run:

```powershell
git clone https://github.com/DamienvanVliet/Intune-Win-Packager.git
cd Intune-Win-Packager
```

If you do not use Git, download the ZIP from GitHub and extract it, then open PowerShell in that extracted folder.

### 3) Build the solution

```powershell
dotnet restore
dotnet build IntuneWinPackager.sln
```

### 4) Run the desktop app

```powershell
dotnet run --project IntuneWinPackager.App
```

## How To Package (In App)

1. Open the app.
2. Configure `IntuneWinAppUtil.exe` via `Auto Locate`, `Install Tool`, or `Browse Tool`.
3. Drag/drop or browse to your installer (`.msi` or `.exe`).
4. Confirm Source, Setup, and Output paths.
5. Review install/uninstall commands.
6. Click `Start Packaging`.
7. Watch live logs and status until completed.
8. Click `Open Output` to open the generated package folder.

## App Updates (In App)

1. Open the `Settings & Profiles` card.
2. In `App Updates`, click `Check for Updates`.
3. Review:
- Current version
- Latest version
- Changelog
4. If an update is available, click `Install Update`.
5. The installer is downloaded and launched automatically.

Note: update checks require internet access to read GitHub releases.

## Release + Changelog Workflow

From now on, every release should include changelog notes and be published so the in-app update button can detect it.

1. Make your code changes.
2. Update notes in `CHANGELOG.md` (new version section).
3. Publish release (builds installer + creates GitHub Release with installer asset):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-update.ps1 -Version 1.1.0 -ReleaseNotes "Your release notes here"
```

Requirements:
- GitHub CLI (`gh`) installed and authenticated.
- Repo remote correctly configured.

## Build A Real Windows Installer (Setup.exe)

Use this when you want to install and run the app like a normal desktop application from Start Menu.

1. Open PowerShell in the repo folder.
2. Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
```

What this script does:
- Publishes a self-contained `win-x64` build (no local .NET runtime required for users).
- Auto-installs Inno Setup via `winget` if missing.
- Builds a Windows installer executable (`Setup.exe`).

Installer output:
- `artifacts\installer\IntuneWinPackager-Setup-<version>.exe`

After installing:
- Launch from Start Menu: `Intune Win Packager`
- Optional Desktop shortcut can be created during setup.

## Common Problems And Fixes

- `dotnet` command not found: install .NET 8 SDK, then reopen PowerShell.
- `git` command not found: install Git with `winget`, then reopen PowerShell.
- Packaging tool missing: use `Auto Locate` or `Install Tool` in the app settings section.
- Output folder permission error: choose a writable folder (for example under `C:\Users\<you>\Documents`).

## Suggested Improvements

- Add code-signing for installer and app executable (improves trust and SmartScreen behavior).
- Add a release script for version bump + changelog + installer artifact naming.
- Add richer MSI metadata extraction (Publisher, ProductName, ProductVersion fallbacks).
- Add a "Command Templates" library for popular EXE vendors.
- Add optional export of logs to `.txt` for ticketing/support.
- Add integration tests for end-to-end packaging workflow with mocked process output.

## Project Structure

- `IntuneWinPackager.App` - WPF UI (MVVM), views/viewmodels, DI composition
- `IntuneWinPackager.Core` - business rules, validation, workflow services
- `IntuneWinPackager.Infrastructure` - process runner, persistence, tool locate/install, MSI inspection
- `IntuneWinPackager.Models` - entities, DTOs, enums
- `IntuneWinPackager.Tests` - unit tests for core logic

## Run Tests

```powershell
dotnet test IntuneWinPackager.sln
```

## Local App Data

Saved in:

`%LOCALAPPDATA%\IntuneWinPackager`

- `settings.json`
- `profiles.json`
- `history.json`
