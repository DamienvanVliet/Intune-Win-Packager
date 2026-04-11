# Intune Win Packager

Intune Win Packager is a local Windows desktop utility that converts `.msi` and `.exe` installers into `.intunewin` packages using Microsoft's official `IntuneWinAppUtil.exe`.

## Super Easy Run Guide

1. Open PowerShell.
2. Go to the project folder:

```powershell
cd "C:\path\to\Intune-Win-Packager"
```

Use the folder where `IntuneWinPackager.sln` exists.

3. Build:

```powershell
dotnet build IntuneWinPackager.sln
```

4. Start the app:

```powershell
dotnet run --project IntuneWinPackager.App
```

5. In the app:
- Click `Install Tool` (or `Auto Locate`) to configure `IntuneWinAppUtil.exe`.
- Drop/select your installer (`.msi` or `.exe`).
- Check source/output folders.
- Click `Start Packaging`.

## Projects

- `IntuneWinPackager.App` - WPF UI (MVVM)
- `IntuneWinPackager.Core` - validation + workflow logic
- `IntuneWinPackager.Infrastructure` - process execution, persistence, inspection
- `IntuneWinPackager.Models` - models/DTOs/enums
- `IntuneWinPackager.Tests` - unit tests

## Tests

```powershell
dotnet test IntuneWinPackager.sln
```

## Local Data

Saved under:

`%LOCALAPPDATA%\\IntuneWinPackager`

- `settings.json`
- `profiles.json`
- `history.json`
