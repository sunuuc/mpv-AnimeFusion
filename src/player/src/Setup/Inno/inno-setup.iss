
#define MyAppName "AnimeVE"
#define MyAppExeName "AnimeVE.exe"
#define MyAppSourceDir "..\..\MpvNet.Windows\bin\Debug"
#define MyAppVersion GetFileVersion("..\..\MpvNet.Windows\bin\Debug\AnimeVE.exe")

[Setup]
AppId={{5507DC66-6430-49B6-88BD-AD4791CF73C2}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=sunuuc
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2
DefaultDirName={autopf}\{#MyAppName}
OutputBaseFilename=mpv-AnimeFusion-v{#MyAppVersion}-setup-x64
OutputDir=output
DefaultGroupName={#MyAppName}
SetupIconFile=..\..\MpvNet.Windows\mpv-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Files]
Source: "{#MyAppSourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Excludes: "win-x64,win-arm64"; Flags: ignoreversion recursesubdirs createallsubdirs;
