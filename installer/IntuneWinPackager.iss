#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

#ifndef SourceDir
#define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts\installer"
#endif

#define MyAppName "Intune Win Packager"
#define MyAppPublisher "Intune Win Packager"
#define MyAppExeName "IntuneWinPackager.App.exe"

[Setup]
AppId={{E8E631D4-7D83-4C53-A6C8-4DFA7557043D}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppMutex=IntuneWinPackager.AppMutex
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename=IntuneWinPackager-Setup-{#AppVersion}
SetupIconFile=..\IntuneWinPackager.App\Assets\IntuneWinPackager.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\IntuneWinPackager.App.exe"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "IntuneWinPackager.App.exe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Check: ShouldCreateProgramShortcut
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Check: ShouldCreateDesktopShortcut

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function ShouldCreateProgramShortcut: Boolean;
begin
  Result := not FileExists(ExpandConstant('{autoprograms}\{#MyAppName}.lnk'));
end;

function ShouldCreateDesktopShortcut: Boolean;
begin
  Result := not FileExists(ExpandConstant('{autodesktop}\{#MyAppName}.lnk'));
end;
