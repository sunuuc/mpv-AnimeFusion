
#define MyAppName "mpv-AnimeFusion"
#define MyAppExeName "mpv-AnimeFusion.exe"
#ifndef MyAppSourceDir
  #error MyAppSourceDir must point to the assembled release payload
#endif
#ifndef MyAppVersion
  #error MyAppVersion must come from release.json
#endif
#ifndef MyAppOutputDir
  #error MyAppOutputDir must point to the release output directory
#endif

[Setup]
AppId={{F5D22654-9F97-480D-973C-586627E6E509}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=sunuuc
AppPublisherURL=https://github.com/sunuuc/mpv-AnimeFusion
AppSupportURL=https://github.com/sunuuc/mpv-AnimeFusion/issues
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2/fast
SolidCompression=yes
DefaultDirName={localappdata}\Programs\{#MyAppName}
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
OutputBaseFilename=mpv-AnimeFusion-{#MyAppVersion}-setup-x64
OutputDir={#MyAppOutputDir}
DefaultGroupName={#MyAppName}
SetupIconFile=..\..\MpvNet.Windows\mpv-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{#MyAppName} Manager"; Filename: "{app}\mpv-AnimeFusionManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Files]
; Program files are replaced on upgrade. User-created files are never swept.
Source: "{#MyAppSourceDir}\mpv-AnimeFusion.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourceDir}\mpv-AnimeFusionManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourceDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\animejanai\*"; DestDir: "{app}\animejanai"; Excludes: "animejanai.conf"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\portable_config\scripts\*"; DestDir: "{app}\portable_config\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\portable_config\script-modules\*"; DestDir: "{app}\portable_config\script-modules"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\portable_config\shaders\*"; DestDir: "{app}\portable_config\shaders"; Flags: ignoreversion recursesubdirs createallsubdirs
; Defaults are seeded once and survive both upgrades and uninstall.
Source: "{#MyAppSourceDir}\portable_config\*.conf"; DestDir: "{app}\portable_config"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#MyAppSourceDir}\portable_config\script-opts\*"; DestDir: "{app}\portable_config\script-opts"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\animejanai\animejanai.conf"; DestDir: "{app}\animejanai"; Flags: onlyifdoesntexist uninsneveruninstall

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent unchecked
