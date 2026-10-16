
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

[CustomMessages]
chinesesimplified.UninstallDataTitle=卸载 mpv-AnimeFusion
chinesesimplified.UninstallDataDescription=选择要同时删除的数据，未勾选的项目将保留。
chinesesimplified.UninstallDataHint=程序文件和临时缓存会自动移除。
chinesesimplified.UninstallPersonalData=个人数据
chinesesimplified.UninstallSubtitlesAndDanmaku=字幕与弹幕
chinesesimplified.UninstallModels=模型与推理组件
chinesesimplified.UninstallContinue=卸载
chinesesimplified.UninstallDeleteError=无法删除已勾选的数据，卸载已停止。
english.UninstallDataTitle=Uninstall mpv-AnimeFusion
english.UninstallDataDescription=Select data to delete. Unchecked items will be kept.
english.UninstallDataHint=Program files and temporary caches will be removed automatically.
english.UninstallPersonalData=Personal data
english.UninstallSubtitlesAndDanmaku=Subtitles and danmaku
english.UninstallModels=Models and inference components
english.UninstallContinue=Uninstall
english.UninstallDeleteError=Unable to delete selected data. Uninstall has stopped.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{#MyAppName} Manager"; Filename: "{app}\mpv-AnimeFusionManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{app}\portable_config\scripts\thumbfast.lua"

[UninstallDelete]
; Runtime updates and optional components can create files outside the install log.
; Personal data is removed only for the categories selected in the uninstall form.
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\docs"
Type: filesandordirs; Name: "{app}\build-info"
Type: filesandordirs; Name: "{app}\portable_config\cache"
Type: files; Name: "{app}\animejanai\currentanimejanai.log"
Type: files; Name: "{app}\portable_config\startup-diagnostic.json"
Type: files; Name: "{app}\portable_config\startup-diagnostic.json.tmp"
Type: files; Name: "{app}\portable_config\playback-diagnostic.json"
Type: files; Name: "{app}\portable_config\playback-error.json"
Type: files; Name: "{app}\portable_config\playback-error.json.tmp"
Type: dirifempty; Name: "{app}\animejanai"
Type: dirifempty; Name: "{app}\portable_config"
Type: dirifempty; Name: "{localappdata}\mpv-AnimeFusion"

[Files]
; Program files are replaced on upgrade; personal configurations and models survive.
Source: "{#MyAppSourceDir}\mpv-AnimeFusion.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourceDir}\mpv-AnimeFusionManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSourceDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\animejanai\*"; DestDir: "{app}\animejanai"; Excludes: "animejanai.conf,\inference\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; Inference files are removed only when the corresponding uninstall option is selected.
Source: "{#MyAppSourceDir}\animejanai\inference\*"; DestDir: "{app}\animejanai\inference"; Flags: ignoreversion uninsneveruninstall recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\portable_config\scripts\*"; DestDir: "{app}\portable_config\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\portable_config\script-modules\*"; DestDir: "{app}\portable_config\script-modules"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\portable_config\shaders\*"; DestDir: "{app}\portable_config\shaders"; Flags: ignoreversion recursesubdirs createallsubdirs
; Defaults are seeded once and survive both upgrades and uninstall.
Source: "{#MyAppSourceDir}\portable_config\*.conf"; DestDir: "{app}\portable_config"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#MyAppSourceDir}\portable_config\script-opts\*"; DestDir: "{app}\portable_config\script-opts"; Flags: onlyifdoesntexist uninsneveruninstall recursesubdirs createallsubdirs
Source: "{#MyAppSourceDir}\animejanai\animejanai.conf"; DestDir: "{app}\animejanai"; Flags: onlyifdoesntexist uninsneveruninstall

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  DeleteUserData: array[0..2] of Boolean;

procedure DeleteDataPath(Path: String; IsDirectory: Boolean);
var
  ExpandedPath: String;
  Found: TFindRec;
  Deleted: Boolean;
begin
  ExpandedPath := ExpandConstant(Path);
  if IsDirectory then begin
    if not DirExists(ExpandedPath) then Exit;
    Deleted := DelTree(ExpandedPath, True, True, True);
  end else if (Pos('*', ExpandedPath) > 0) or (Pos('?', ExpandedPath) > 0) then begin
    if not FindFirst(ExpandedPath, Found) then Exit;
    FindClose(Found);
    Deleted := DelTree(ExpandedPath, False, True, False);
  end else begin
    if not FileExists(ExpandedPath) then Exit;
    Deleted := DeleteFile(ExpandedPath);
  end;
  if not Deleted then begin
    Log('Unable to delete selected uninstall data: ' + ExpandedPath);
    RaiseException(CustomMessage('UninstallDeleteError') + #13#10 + ExpandedPath);
  end;
end;

procedure DeleteSelectedData;
begin
  if DeleteUserData[0] then begin
    DeleteDataPath('{app}\portable_config\settings.xml', False);
    DeleteDataPath('{app}\portable_config\*.conf', False);
    DeleteDataPath('{app}\portable_config\interface-language.json', False);
    DeleteDataPath('{app}\portable_config\script-opts', True);
    DeleteDataPath('{app}\portable_config\scripts', True);
    DeleteDataPath('{app}\portable_config\script-modules', True);
    DeleteDataPath('{app}\portable_config\shaders', True);
    DeleteDataPath('{app}\animejanai\animejanai.conf', False);
    DeleteDataPath('{app}\animejanai\backups', True);
    DeleteDataPath('{app}\portable_config\bangumi.json', False);
    DeleteDataPath('{app}\portable_config\bangumi-app.json', False);
    DeleteDataPath('{app}\portable_config\trakt.json', False);
    DeleteDataPath('{app}\portable_config\mal.json', False);
    DeleteDataPath('{app}\portable_config\sync.json', False);
    DeleteDataPath('{localappdata}\mpv-AnimeFusion\Bangumi', True);
    DeleteDataPath('{localappdata}\mpv-AnimeFusion\Trakt', True);
    DeleteDataPath('{localappdata}\mpv-AnimeFusion\MyAnimeList', True);
    DeleteDataPath('{app}\portable_config\watch_later', True);
    DeleteDataPath('{app}\portable_config\saved-props.json', False);
  end;
  if DeleteUserData[1] then begin
    DeleteDataPath('{localappdata}\mpv-AnimeFusion\Subtitles', True);
    DeleteDataPath('{localappdata}\mpv-AnimeFusion\Danmaku', True);
  end;
  if DeleteUserData[2] then begin
    DeleteDataPath('{app}\animejanai\onnx', True);
    DeleteDataPath('{app}\animejanai\rife', True);
    DeleteDataPath('{app}\animejanai\inference', True);
  end;
end;

function SelectUninstallData: Boolean;
var
  Form: TSetupForm;
  Header: TPanel;
  HeaderLine, FooterLine: TBevel;
  Title, Description, Hint: TNewStaticText;
  Options: TNewCheckListBox;
  ContinueButton, CancelButton: TNewButton;
  I, ButtonWidth: Integer;
begin
  { CreateCustomForm initializes and scales the form itself. Use the native
    wizard's design dimensions here; scale only the controls added afterward. }
  Form := CreateCustomForm(497, 276, False, False);
  try
    Form.Caption := CustomMessage('UninstallDataTitle');
    Form.BorderIcons := [biSystemMenu];

    Header := TPanel.Create(Form);
    Header.Parent := Form;
    Header.SetBounds(0, 0, Form.ClientWidth, ScaleY(58));
    Header.BevelOuter := bvNone;
    Header.Color := clWindow;
    Header.ParentBackground := False;

    Title := TNewStaticText.Create(Form);
    Title.Parent := Header;
    Title.SetBounds(ScaleX(24), ScaleY(8), Header.Width - ScaleX(48), ScaleY(16));
    Title.AutoSize := False;
    Title.Font.Style := [fsBold];
    Title.Caption := CustomMessage('UninstallDataTitle');
    Title.AdjustHeight;

    Description := TNewStaticText.Create(Form);
    Description.Parent := Header;
    Description.SetBounds(ScaleX(40), Title.Top + Title.Height + ScaleY(4),
      Header.Width - ScaleX(64), ScaleY(28));
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.Caption := CustomMessage('UninstallDataDescription');
    Description.AdjustHeight;

    HeaderLine := TBevel.Create(Form);
    HeaderLine.Parent := Form;
    HeaderLine.Shape := bsTopLine;
    HeaderLine.SetBounds(0, Header.Height, Form.ClientWidth, ScaleY(2));

    FooterLine := TBevel.Create(Form);
    FooterLine.Parent := Form;
    FooterLine.Shape := bsTopLine;
    FooterLine.SetBounds(0, Form.ClientHeight - ScaleY(47),
      Form.ClientWidth, ScaleY(2));

    Hint := TNewStaticText.Create(Form);
    Hint.Parent := Form;
    Hint.SetBounds(ScaleX(40), Header.Height + ScaleY(20),
      Form.ClientWidth - ScaleX(80), ScaleY(28));
    Hint.AutoSize := False;
    Hint.WordWrap := True;
    Hint.Caption := CustomMessage('UninstallDataHint');
    Hint.AdjustHeight;

    Options := TNewCheckListBox.Create(Form);
    Options.Parent := Form;
    Options.SetBounds(Hint.Left, Hint.Top + Hint.Height + ScaleY(12),
      Hint.Width, FooterLine.Top - Hint.Top - Hint.Height - ScaleY(32));
    Options.BorderStyle := bsNone;
    Options.ParentColor := True;
    Options.Flat := True;
    Options.MinItemHeight := ScaleY(28);
    Options.WantTabs := True;
    Options.AddCheckBox(CustomMessage('UninstallPersonalData'), '', 0, False, True, False, False, nil);
    Options.AddCheckBox(CustomMessage('UninstallSubtitlesAndDanmaku'), '', 0, False, True, False, False, nil);
    Options.AddCheckBox(CustomMessage('UninstallModels'), '', 0, False, True, False, False, nil);

    ContinueButton := TNewButton.Create(Form);
    ContinueButton.Parent := Form;
    ContinueButton.Caption := CustomMessage('UninstallContinue');
    ContinueButton.ModalResult := mrOk;
    ContinueButton.Default := True;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Caption := SetupMessage(msgButtonCancel);
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    ButtonWidth := Form.CalculateButtonWidth([ContinueButton.Caption, CancelButton.Caption]);
    ContinueButton.SetBounds(Form.ClientWidth - 2 * ButtonWidth - ScaleX(20),
      Form.ClientHeight - ScaleY(33), ButtonWidth, ScaleY(23));
    CancelButton.SetBounds(Form.ClientWidth - ButtonWidth - ScaleX(12),
      ContinueButton.Top, ButtonWidth, ContinueButton.Height);
    Form.ActiveControl := Options;
    Result := Form.ShowModal() = mrOk;
    if Result then
      for I := 0 to 2 do
        DeleteUserData[I] := Options.Checked[I];
  finally
    Form.Free;
  end;
end;

function InitializeUninstall: Boolean;
begin
  if UninstallSilent() then
    Result := True
  else begin
    Log('Opening uninstall data selection.');
    Result := SelectUninstallData();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    DeleteSelectedData;
end;
