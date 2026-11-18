; UPS Hub installer (Inno Setup 6.4 or newer).
; Build:  ISCC installer\UpsHub.iss /DAppVersion=0.4.0 /DSourceExe=..\artifacts\win-x64\UpsHub.exe
; Output: artifacts\installer\UpsHub-Setup-<version>.exe
;
; Per-user install, no admin prompt: %LOCALAPPDATA%\Programs\UPS Hub.
; Silent update (what the in-app updater runs; /VERYSILENT for automatic updates):
;   UpsHub-Setup-x.y.z.exe /SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /RELAUNCH=1|tray
; /RELAUNCH (updater mode): Setup waits for UPS Hub to close itself (never force-closes it), keeps a copy of
; UpsHub.exe, puts it back if Setup doesn't finish, and starts UPS Hub again either way ("tray" = --minimized).
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
#define AppRepo "https://github.com/AslanCore/UPS-Hub"
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
#ifdef TestFailInstall
; Verification builds only (ISCC /DTestFailInstall): a file that can't be copied right after the exe was
; replaced, so Setup aborts mid-install like it would on a locked or unwritable file.
Source: "{src}\upshub-missing-on-purpose.bin"; DestDir: "{app}"; Flags: external
#endif
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
; Interactive install: offer to open the app. Silent update: DeinitializeSetup reopens it (see /RELAUNCH).
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.log"
Type: filesandordirs; Name: "{app}\.update-backup"
Type: dirifempty; Name: "{app}"

[Code]
var
  RelaunchExe: String;
  InstallDone: Boolean;

function AppMutex: String;
begin
  Result := 'AslanCore.UpsHub.' + GetUserNameString + '.main';
end;

function RelaunchMode: String;
begin
  Result := Lowercase(ExpandConstant('{param:RELAUNCH|0}'));
end;

{ True only when the in-app updater started this Setup (it always passes /RELAUNCH=1 or /RELAUNCH=tray
  together with /SILENT or /VERYSILENT). Interactive installs and plain silent installs are unaffected. }
function UpdaterMode: Boolean;
begin
  Result := WizardSilent and ((RelaunchMode = '1') or (RelaunchMode = 'tray'));
end;

{ Where UPS Hub lives now: the previous install of this AppId (per-user). Known before anything is replaced. }
function PreviousExe: String;
var
  Dir: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1C2B7E-4E53-4C8B-9A3F-2D5B7A1E0C44}_is1', 'Inno Setup: App Path', Dir) then
    if FileExists(AddBackslash(Dir) + '{#AppExe}') then
      Result := AddBackslash(Dir) + '{#AppExe}';
end;

function BackupDir: String;
begin
  Result := ExpandConstant('{app}\.update-backup');
end;

function BackupExe: String;
begin
  Result := BackupDir + '\{#AppExe}';
end;

function InitializeSetup: Boolean;
begin
  RelaunchExe := PreviousExe;
  InstallDone := False;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallDone := True;
    RelaunchExe := ExpandConstant('{app}\{#AppExe}');
    if UpdaterMode and FileExists(BackupExe) then
    begin
      DeleteFile(BackupExe);
      RemoveDir(BackupDir);
      Log('Update installed; pre-update copy removed.');
    end;
  end;
end;

{ Updater mode: if Setup did not finish (error, abort, file in use), put the pre-update UpsHub.exe back so
  the existing installation keeps working, then start it again unless it is already running. Settings,
  saved passwords and shutdown rules live in %APPDATA%\UPS Hub and are never touched by Setup. }
procedure DeinitializeSetup;
var
  Code: Integer;
  Params, Exe: String;
begin
  if not UpdaterMode then Exit;
  Exe := RelaunchExe;
  if (not InstallDone) and (Exe <> '') and FileExists(ExtractFilePath(Exe) + '.update-backup\{#AppExe}') then
  begin
    if FileCopy(ExtractFilePath(Exe) + '.update-backup\{#AppExe}', Exe, False) then
    begin
      Log('Setup did not finish: restored the pre-update ' + Exe);
      DeleteFile(ExtractFilePath(Exe) + '.update-backup\{#AppExe}');
      RemoveDir(ExtractFilePath(Exe) + '.update-backup');
    end
    else
      Log('Setup did not finish and restoring ' + Exe + ' failed; the pre-update copy stays in .update-backup.');
  end;
  if (Exe = '') or not FileExists(Exe) then Exit;
  if CheckForMutexes(AppMutex) then
  begin
    Log('UPS Hub is running; not starting another copy.');
    Exit;
  end;
  Params := '';
  if RelaunchMode = 'tray' then Params := '--minimized';
  Log('Relaunching ' + Exe + ' ' + Params);
  if not ExecAsOriginalUser(Exe, Params, '', SW_SHOWNORMAL, ewNoWait, Code) then
    Log('Relaunch failed: ' + SysErrorMessage(Code));
end;

{ Updater mode: UPS Hub closes itself (monitors stopped in order) once it sees Setup running, unless a UPS
  went on battery or a shutdown started meanwhile. Wait up to 60 s; Setup never force-closes it here. }
function WaitForAppExit: Boolean;
var
  I: Integer;
begin
  I := 0;
  while CheckForMutexes(AppMutex) and (I < 240) do
  begin
    Sleep(250);
    I := I + 1;
  end;
  Result := not CheckForMutexes(AppMutex);
  Log('Waited ' + IntToStr(I * 250) + ' ms for UPS Hub to close; closed: ' + IntToStr(Ord(Result)));
  if Result then Sleep(1000);   { let Windows release the exe file }
end;

{ Interactive / plain installs: the app lives in the tray, so Restart Manager often finds no window to
  close. End this user's UpsHub.exe so its file can be replaced. }
procedure KillRunningApp;
var
  Code: Integer;
begin
  Log('Mutex present: ' + IntToStr(Ord(CheckForMutexes(AppMutex))));
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
var
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(Exe) then Exit;
  if not UpdaterMode then
  begin
    KillRunningApp;
    Exit;
  end;
  if not WaitForAppExit then
  begin
    Result := 'UPS Hub is still running, so the update was postponed.';
    Exit;
  end;
  { Keep the working copy until the new one is in place. A backup left by an interrupted earlier update
    is refreshed from the current exe, which Setup only ever swaps in whole. }
  ForceDirectories(BackupDir);
  if not FileCopy(Exe, BackupExe, False) then
  begin
    Result := 'Could not keep a copy of the current UPS Hub, so the update was not installed.';
    Exit;
  end;
  Log('Pre-update copy saved to ' + BackupExe);
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
