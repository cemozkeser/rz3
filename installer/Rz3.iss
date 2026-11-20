; ==========================================================================================
;  Rz3 installer (Inno Setup 6/7)
;
;  Compile with scripts\build-release.ps1 -Installer, which passes AppVersion, AppSource
;  and AppOutput and guarantees that the version here is the one in src\Version.cs.
;
;  DESIGN NOTES - why this installer is shaped the way it is. Read this before changing it.
;
;  1. PER-USER, NO ELEVATION.
;     Rz3 needs nothing an administrator has. The mouse talks to a HID interface that a normal
;     user may write to, the settings live in HKCU, and host actions run inside Rz3's own
;     process. Requesting admin would only add a UAC prompt and a second user's registry, so
;     PrivilegesRequired is set to lowest on purpose. Consequence: the install needs no
;     certificate for elevation and can be removed by the same user who added it.
;
;  2. WHAT ACTUALLY GOES INTO THE REGISTRY.
;     The mouse settings do NOT live in the registry and cannot: they live in the mouse's own
;     firmware. The registry entries written here are only the Windows-side integration:
;         HKCU\Software\Microsoft\Windows\CurrentVersion\Run   -> autostart (opt-in)
;         HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Rz3_is1 -> Add/Remove row
;     Both are HKCU, so "install for me only" is literal.
;
;  3. NO WINDOWS SERVICE - DELIBERATE.
;     A service runs in session 0 with no desktop. Host actions work by installing a
;     WH_KEYBOARD_LL hook and by injecting keys/programs into the *interactive* desktop, and
;     session-0 processes cannot reach that desktop. A service would install, start, and then
;     silently never fire a single button. The correct equivalent is a per-user autostart
;     entry that runs Rz3.exe --tray in the user's own session, which is what the Run value
;     does. There is an optional task (off by default) that runs the mouse-cleanup helper at
;     user logon instead - still session-local, still no elevation.
;
;  4. SIDEBYSIDE VERSIONS.
;     Each release installs into its own folder, %LOCALAPPDATA%\Programs\Rz3\<version>. A new
;     version never overwrites the files of the old one, so "which build am I running" stays
;     answerable and a bad upgrade can be undone by starting the other one. Shared, versionless
;     user state (action tables, names, UI prefs) deliberately stays in ONE place,
;     %LOCALAPPDATA%\Rz3, so upgrading does not lose configuration. The uninstaller only
;     removes that state when the user asks for it.
;
;     A version that is already installed is upgraded in place. The optional entry "Remove old
;     versions after installing" deletes older version folders once the new one is in place.
;
;  5. SYNAPSE.
;     If Razer Synapse is running, both programs take turns on the same device and reads start
;     timing out. The installer offers to close Synapse; it never silently kills it.
; ==========================================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppSource
  #define AppSource "..\dist"
#endif
#ifndef AppOutput
  #define AppOutput "..\dist"
#endif

#define AppName        "Rz3"
#define AppPublisher   "Rz3 contributors"
#define AppUrl         "https://github.com/cemozkeser/rz3"
#define AppExeName     "Rz3.exe"
#define AppId          "{{9F3C2D41-7B16-4E52-9A0B-3D7C6E18F204}"

[Setup]
; A fixed AppId keeps upgrades and uninstall entries pointing at the same product.
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user, no UAC. See note 1.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\Rz3\{#AppVersion}
; {autopf} with "lowest" resolves to the user's Programs folder, so the uninstaller lands in
; Add/Remove Programs for this user only.
DefaultGroupName=Rz3
DisableProgramGroupPage=yes
UsePreviousAppDir=no
; Hidden runtime state lives in %LOCALAPPDATA%\Rz3 and must survive upgrades.
UsePreviousTasks=yes
AllowNoIcons=yes
OutputDir={#AppOutput}
OutputBaseFilename=Rz3-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile={#AppSource}\Rz3.ico
; Nothing here is signed yet; the wizard says so plainly on startup rather than pretending.
SignedUninstaller=no
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription={#AppName} setup {#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon";    Description: "{cm:CreateDesktopIcon}";        GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart";      Description: "Start Rz3 with Windows (tray mode) - required for host actions"; GroupDescription: "Windows startup:"
Name: "removeold";      Description: "Remove older installed versions after this one"; GroupDescription: "Upgrades:"

[Files]
Source: "{#AppSource}\Rz3.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSource}\Rz3.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppSource}\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme skipifsourcedoesntexist
Source: "{#AppSource}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#AppSource}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName} {#AppVersion}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; --- autostart (opt-in). HKCU because Rz3 runs as the logged-on user, never as a service. ---
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "Rz3"; ValueData: """{app}\{#AppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart
; --- remember where the newest build lives so a later uninstall can find shared state. ---
Root: HKCU; Subkey: "Software\Rz3"; ValueType: string; ValueName: "InstallDir"; \
    ValueData: "{app}"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\Rz3"; ValueType: string; ValueName: "InstallVersion"; \
    ValueData: "{#AppVersion}"; Flags: uninsdeletekeyifempty

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start Rz3 now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Rz3 writes its runtime files next to the executable (that is how a portable copy stays
; self-contained), so a version folder is never empty at uninstall time. These are the files the
; program itself creates; they are deleted so the folder can go away. Note that the *shared*
; configuration in %LOCALAPPDATA%\Rz3 is a different thing and is handled in [Code] - it is kept
; unless the user asks for it to be removed.
Type: files; Name: "{app}\rz3_host.json"
Type: files; Name: "{app}\rz3_host.log"
Type: files; Name: "{app}\rz3_names.json"
Type: files; Name: "{app}\rz3_dpi.json"
Type: files; Name: "{app}\rz3_ui.json"
Type: files; Name: "{app}\rz3_power.json"
Type: files; Name: "{app}\rz3_rate_cal.json"
Type: files; Name: "{app}\rz3_crash.log"
Type: files; Name: "{app}\rz3_logictest.txt"
Type: files; Name: "{app}\rz3_uicheck.txt"
Type: files; Name: "{app}\rz3_traytest.txt"
Type: files; Name: "{app}\rz3_probe.txt"
Type: files; Name: "{app}\rz3_fire.txt"
Type: files; Name: "{app}\rz3_runtest.txt"
Type: files; Name: "{app}\rz3_ratetest.txt"
Type: files; Name: "{app}\rz3_selftest.txt"
Type: files; Name: "{app}\rz3_switchtest.txt"
Type: files; Name: "{app}\rz3_nametest.txt"
Type: files; Name: "{app}\rz3_droptest.txt"
Type: files; Name: "{app}\rz3_dpitest.txt"
Type: files; Name: "{app}\rz3_hosttest.txt"
Type: files; Name: "{app}\rz3_tabtest.txt"
Type: files; Name: "{app}\rz3_startuptest.txt"
Type: files; Name: "{app}\rz3_hidetotray.txt"
Type: files; Name: "{app}\rz3_trayhook.txt"
Type: files; Name: "{app}\rz3_triggercheck.txt"
Type: files; Name: "{app}\rz3_watchkeys.txt"
Type: files; Name: "{app}\rz3_linktest.txt"
Type: files; Name: "{app}\rz3_linkmodetest.txt"
Type: files; Name: "{app}\rz3_powertest.txt"
Type: files; Name: "{app}\rz3_rangetest.txt"
Type: files; Name: "{app}\rz3_wincmd.txt"
Type: files; Name: "{app}\rz3_uiprobe.txt"
Type: files; Name: "{app}\rz3_icontest.txt"
Type: files; Name: "{app}\_tvtest"
Type: dirifempty; Name: "{app}\rz3_icontest"
Type: dirifempty; Name: "{app}\docs"
Type: dirifempty; Name: "{app}\rz3_test_tmp"
Type: dirifempty; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
  RazerSynapseExe = 'RazerAppEngine.exe';

var
  SynapsePage: TInputOptionWizardPage;
  StatePage: TInputOptionWizardPage;

// ---------------------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------------------

// True when a process with this image name is alive.
//
// Implemented with tasklist rather than WMI or toolhelp: Inno's Pascal Script does not expose
// the toolhelp structures, WMI can be disabled on hardened systems, and "cannot tell" must
// never block an install. tasklist writes its answer to a temp file that is parsed here.
function ProcessIsRunning(const ExeName: string): Boolean;
var
  Tmp, Args: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
  I: Integer;
  Lower: String;
begin
  Result := False;
  Tmp := ExpandConstant('{tmp}\rz3_proc.txt');
  DeleteFile(Tmp);
  Args := '/FO CSV /NH /FI "IMAGENAME eq ' + ExeName + '"';
  if not Exec(ExpandConstant('{cmd}'), '/C tasklist ' + Args + ' > "' + Tmp + '"', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if not FileExists(Tmp) then Exit;
  try
    if not LoadStringsFromFile(Tmp, Lines) then Exit;
    Lower := Lowercase(ExeName);
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Pos(Lower, Lowercase(Lines[I])) > 0 then
      begin
        Result := True;
        Exit;
      end;
  finally
    DeleteFile(Tmp);
  end;
end;

// True when at least one Razer engine process is alive. Synapse keeps the HID interface busy,
// which is what makes Rz3 read "n/a" for everything until it is closed.
function AnyRazerEngineRunning: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Names := ['RazerAppEngine.exe', 'RzDeviceManager.exe', 'RzAppManager.exe',
            'RzDeviceManagerEx.exe', 'RazerCentralService.exe'];
  for I := 0 to GetArrayLength(Names) - 1 do
    if ProcessIsRunning(Names[I]) then
    begin
      Result := True;
      Exit;
    end;
end;

// Politely ask the running instances to close (taskkill without /F sends WM_CLOSE, so Synapse
// writes its own settings on the way out). Never forced, never automatic.
procedure CloseRazerSynapse;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/IM ' + RazerSynapseExe, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);   // give the device a moment to be released
end;

// Runs Rz3.exe --tray so the tray engine takes over immediately after install.
procedure LaunchRz3Tray;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{app}\{#AppExeName}'), '--tray', ExpandConstant('{app}'),
       SW_HIDE, ewNoWait, ResultCode);
end;

// The uninstaller needs one flag that is not a normal setup parameter, so the user's choice is
// carried in the uninstall command line - the least surprising place for it.
procedure SetUninstallStringSuffix(const Suffix: string);
var
  Cmd: String;
begin
  if not RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Cmd) then Exit;
  if Pos(Suffix, Cmd) > 0 then Exit;
  RegWriteStringValue(HKCU, UninstallKey, 'UninstallString', Cmd + ' ' + Suffix);
end;

// The user's choice about the shared configuration is stored in the registry by the *installer*
// and read by the uninstaller.
//
// Why not the command line: Inno's uninstaller re-launches itself for its second phase with a
// rewritten command line (/SECONDPHASE=..., /FIRSTPHASEWND=..., plus the caller's switches), and
// when it is started from Settings > Apps there is no user-controlled command line at all. A
// parameter scan therefore cannot be relied on. A stored preference can.
function DeleteDataPrefIsSet: Boolean;
var
  Flag: String;
begin
  Result := RegQueryStringValue(HKCU, 'Software\Rz3', 'DeleteDataOnUninstall', Flag) and (Flag = '1');
end;

// Is another Rz3 build still installed next to this one?
function AnotherRz3Installed: Boolean;
var
  Base, Cur, Name, Exe: String;
  FindRec: TFindRec;
begin
  Result := False;
  Base := ExpandConstant('{localappdata}\Programs\Rz3');
  Cur := ExpandConstant('{app}');
  if not FindFirst(Base + '\*', FindRec) then Exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Name := Base + '\' + FindRec.Name;
        Exe := Name + '\{#AppExeName}';
        if (CompareText(Name, Cur) <> 0) and FileExists(Exe) then
        begin
          Result := True;
          Exit;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// Removes folders of older versions when the user asked for it on the State page.
procedure RemoveOlderVersionFolders;
var
  Base, Cur, Name: String;
  FindRec: TFindRec;
  Versions: TStringList;
  I: Integer;
begin
  Base := ExpandConstant('{localappdata}\Programs\Rz3');
  Cur := ExpandConstant('{app}');
  Versions := TStringList.Create;
  try
    if FindFirst(Base + '\*', FindRec) then
    begin
      try
        repeat
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
             (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          begin
            Name := Base + '\' + FindRec.Name;
            if CompareText(Name, Cur) <> 0 then
              Versions.Add(Name);
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
    for I := 0 to Versions.Count - 1 do
      // Only folders that really look like an Rz3 build, never anything else.
      if FileExists(Versions[I] + '\{#AppExeName}') then
        DelTree(Versions[I], True, True, True);
  finally
    Versions.Free;
  end;
end;

// ---------------------------------------------------------------------------------------
// Welcome page: say exactly what will be changed
// ---------------------------------------------------------------------------------------

procedure InitializeWizard;
begin
  SynapsePage := CreateInputOptionPage(wpWelcome,
    'Razer Synapse', 'Rz3 and Synapse cannot use the mouse at the same time.',
    'Both programs talk to the same interface inside the mouse. When Synapse is running, ' +
    'Rz3''s reads and writes can time out, and the status bar reports the device as busy or ' +
    'asleep.' + #13#10 + #13#10 +
    'Rz3 does not need Synapse and does not remove it. Choose one:', False, False);
  SynapsePage.Add('Close Synapse for me now (recommended)');
  SynapsePage.Add('I will close it myself before starting Rz3');
  SynapsePage.Values[1] := True;

  StatePage := CreateInputOptionPage(SynapsePage.ID,
    'Your settings', 'Where Rz3 keeps your configuration.',
    'Profiles, button mappings, DPI, polling rate and lighting are stored in the mouse''s own ' +
    'memory by Rz3 - they are not in Windows and they survive an uninstall.' + #13#10 + #13#10 +
    'Rz3''s own configuration (host actions, profile names it had to remember locally, tray and ' +
    'window preferences) is kept in one shared folder so that upgrading never loses it:' + #13#10 +
    ExpandConstant('{localappdata}\Rz3') + #13#10 + #13#10 +
    'A later uninstall keeps that folder unless you tell it otherwise.', False, False);
  StatePage.Add('Keep my Rz3 settings if I uninstall later (recommended)');
  StatePage.Add('Also delete my Rz3 settings when I uninstall');
  StatePage.Values[0] := True;
end;

function AnyRazerRunningCheck: Boolean;
begin
  Result := AnyRazerEngineRunning;
end;

// ---------------------------------------------------------------------------------------
// Install flow
// ---------------------------------------------------------------------------------------

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if (SynapsePage <> nil) and SynapsePage.Values[0] and AnyRazerRunningCheck then
    CloseRazerSynapse;
end;

// Runs after files are copied: make sure the tray engine is actually alive, so a user who
// never opens the window still gets host actions.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Remember the user's choice about the shared configuration and record it for the
    // uninstaller, which cannot reliably see this session's command line later on.
    if (StatePage <> nil) and StatePage.Values[1] then
      RegWriteStringValue(HKCU, 'Software\Rz3', 'DeleteDataOnUninstall', '1')
    else
      RegDeleteValue(HKCU, 'Software\Rz3', 'DeleteDataOnUninstall');
    if WizardIsTaskSelected('autostart') then
      LaunchRz3Tray;
  end;
end;

// ---------------------------------------------------------------------------------------
// Uninstall flow
// ---------------------------------------------------------------------------------------

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue, StateDir: String;
  RemoveState, Silent, OtherBuild: Boolean;
begin
  // usUninstall runs before Inno removes the program folder, which is what we want: the checks
  // below still see the tree as it really was when the user started the uninstall.
  if CurUninstallStep = usUninstall then
  begin
    // 1) Autostart. Only clear the Run value when it actually points at the build being removed;
    //    otherwise a newer version is installed and must keep starting with Windows.
    if RegQueryStringValue(HKCU, RunKey, 'Rz3', RunValue) then
      if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(RunValue)) > 0 then
        RegDeleteValue(HKCU, RunKey, 'Rz3');

    // 2) Shared configuration. It is shared by every installed version, so:
    //      * never delete it while another build is still installed;
    //      * follow the preference stored at install time (this also covers a silent uninstall
    //        and being launched from Settings > Apps);
    //      * in an interactive uninstall ask, defaulting to KEEP.
    StateDir := ExpandConstant('{localappdata}\Rz3');
    OtherBuild := AnotherRz3Installed;
    // UninstallSilent is the uninstaller's own silent-mode flag. (WizardSilent must NOT be used
    // here: Inno raises "Cannot call WizardSilent during Uninstall".)
    Silent := UninstallSilent;
    RemoveState := False;
    if (not OtherBuild) and DirExists(StateDir) then
    begin
      if Silent then
        RemoveState := DeleteDataPrefIsSet
      else
        RemoveState := MsgBox(
          'Also delete Rz3''s configuration folder?' + #13#10 + #13#10 +
          StateDir + #13#10 + #13#10 +
          'This holds host actions, locally remembered profile names and your tray/window ' +
          'preferences.' + #13#10 + #13#10 +
          'Profiles and button mappings stay in the mouse either way and are NOT affected.' + #13#10 +
          'Choose No to keep your configuration.',
          mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    end;

    if RemoveState then
      DelTree(StateDir, True, True, True);
  end;
end;

// Pascal Script has no CmdLine global but does expose ParamCount/ParamStr.
function InitializeSetup: Boolean;
begin
  Result := True;
end;
