@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT_FILE=%SCRIPT_DIR%IntuneWinPackager.App\IntuneWinPackager.App.csproj"

REM Always start from this script's folder
cd /d "%SCRIPT_DIR%"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [FOUT] .NET SDK is niet gevonden.
    echo Installeer .NET 8 SDK en probeer opnieuw.
    echo Tip: winget install --id Microsoft.DotNet.SDK.8 --source winget
    pause
    exit /b 1
)

if not exist "%PROJECT_FILE%" (
    REM Fallback: search under script dir if the project was moved.
    for /f "delims=" %%I in ('where /r "%SCRIPT_DIR%" IntuneWinPackager.App.csproj 2^>nul') do (
        set "PROJECT_FILE=%%~fI"
        goto :project_found
    )
)

:project_found
if not exist "%PROJECT_FILE%" (
    echo [FOUT] Projectbestand niet gevonden: IntuneWinPackager.App.csproj
    echo Plaats dit script in de root van de repo, naast IntuneWinPackager.sln.
    echo Huidige scriptmap: "%SCRIPT_DIR%"
    pause
    exit /b 1
)

echo Intune Win Packager wordt gestart...
dotnet run --project "%PROJECT_FILE%" --configuration Release
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo [FOUT] De applicatie is gestopt met code %EXIT_CODE%.
    echo Controleer de output hierboven voor details.
    pause
)

exit /b %EXIT_CODE%
