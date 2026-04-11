# Intune Win Packager

Intune Win Packager is a local Windows desktop app that converts `.msi` and `.exe` installers into `.intunewin` packages by calling Microsoft's official `IntuneWinAppUtil.exe`.

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

## First-Time Setup In The App

1. Click `Auto Locate` to find `IntuneWinAppUtil.exe`.
2. If not found, click `Install Tool` for one-click setup.
3. If needed, click `Browse Tool` and select `IntuneWinAppUtil.exe` manually.
4. Select or drag/drop your installer (`.msi` or `.exe`).
5. Confirm the Source folder, Setup file, and Output folder.
6. Review install/uninstall commands.
7. Click `Start Packaging`.
8. After success, click `Open Output Folder`.

## Common Problems And Fixes

- `dotnet` command not found: install .NET 8 SDK, then reopen PowerShell.
- `git` command not found: install Git with `winget`, then reopen PowerShell.
- Packaging tool missing: use `Auto Locate` or `Install Tool` in the app settings section.
- Output folder permission error: choose a writable folder (for example under `C:\Users\<you>\Documents`).

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
