; UPS Hub installer (Inno Setup 6.4 or newer).
; Build:  ISCC installer\UpsHub.iss /DAppVersion=0.4.0 /DSourceExe=..\artifacts\win-x64\UpsHub.exe
; Output: artifacts\installer\UpsHub-Setup-<version>.exe
;
; Per-user install, no admin prompt: %LOCALAPPDATA%\Programs\UPS Hub.
; Silent update (what the in-app updater runs):
;   UpsHub-Setup-x.y.z.exe /SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /RELAUNCH=1
; Silent uninstall keeps settings unless /REMOVESETTINGS is given.

#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\win-x64\UpsHub.exe"
#endif
#define AppName "UPS Hub"
#define AppPublisher "AslanCore"
#define AppExe "UpsHub.exe"
#define AppRepo "https://github.com/cyberwill01/UPS-Hub"
#define AppUserModelId "AslanCore.UpsHub"

[Setup]
AppId={{6F1C2B7E-4E53-4C8B-9A3F-2D5B7A1E0C44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppRepo}
AppSupportURL={#AppRepo}/issues
AppUpdatesURL={#AppRepo}/releases
AppCopyright=Copyright (C) AslanCore. GPL-3.0-or-later.
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=no
DisableWelcomePage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=UpsHub-Setup-{#AppVersion}
SetupIconFile=..\src\UpsHub.App\Assets\upshub.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern dark
WizardSizePercent=100
WizardImageFile=wizard-large-100.png,wizard-large-200.png
WizardSmallImageFile=wizard-small-100.png,wizard-small-200.png
WizardImageStretch=no
WizardImageBackColor=$08090a
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LicenseFile=..\LICENSE
; Close a running UPS Hub before files are replaced (Restart Manager), and reopen it afterwards.
CloseApplications=force
CloseApplicationsFilter=*.exe
RestartApplications=no
SetupMutex=AslanCore.UpsHub.Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.StartWithWindows=Start UPS Hub when I sign in to Windows (recommended, so shutdown rules protect this PC)
english.LaunchApp=Open UPS Hub
english.RemoveSettings=Also remove your UPS Hub settings (servers, saved passwords and shutdown rules)?%n%nChoose No to keep them for a later install.

[Tasks]
Name: "startwithwindows"; Description: "{cm:StartWithWindows}"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExe}"; Flags: ignoreversion
Source: "..\src\UpsHub.App\Assets\upshub.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\NOTICE.md"; DestDir: "{app}"; DestName: "NOTICE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\upshub.ico"; AppUserModelID: "{#AppUserModelId}"; Comment: "Monitor your UPS and shut down safely"

[Registry]
; Same value the app writes from Settings, so either side can turn it off.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startwithwindows; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppName}"; ValueType: none; Tasks: not startwithwindows; Flags: deletevalue

[Run]
; Interactive install: offer to open the app. Silent update: reopen it when /RELAUNCH=1 was passed.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[UninstallDelete]
Type: files; Name: "{app}\*.log"
Type: dirifempty; Name: "{app}"

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

{ The app lives in the tray, so Restart Manager often finds no window to close. End this user's
  UpsHub.exe so its file can be replaced (the silent update relaunches it afterwards). }
procedure KillRunningApp;
var
  Code: Integer;
begin
  Log('Mutex present: ' + IntToStr(Ord(CheckForMutexes('AslanCore.UpsHub.' + GetUserNameString + '.main'))));
  Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/F /IM {#AppExe} /FI "USERNAME eq ' + GetUserNameString + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('taskkill exit code: ' + IntToStr(Code));
  if (Code <> 0) and (Code <> 128) then
  begin
    { Older or minimal taskkill builds without /FI support. }
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Log('taskkill (no filter) exit code: ' + IntToStr(Code));
  end;
  { 0 = something was ended. Give Windows a moment to release the file; Setup also retries a locked file. }
  if Code = 0 then Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
    KillRunningApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  KillRunningApp;
  Result := True;
end;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Remove: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\{#AppUserModelId}');
    if UninstallSilent then
      Remove := HasParam('/REMOVESETTINGS')
    else
      Remove := MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if Remove then
    begin
      DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
    end
    else
      { Downloaded updates are only a cache; settings stay. }
      DelTree(ExpandConstant('{localappdata}\{#AppName}\updates'), True, True, True);
  end;
end;
