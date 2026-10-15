; Exo's PC installer (PLAN.md, Release). Built by release.ps1, which
; stages the files and passes AppVersion, Stage, Out and CertThumb; not
; meant to be compiled by hand.
;
; Per machine, elevated once. Install does, and uninstall undoes:
;   1. the hub (self-contained, adb loose beside it), the camera DLL,
;      registered in HKLM so the Frame Server loads it, and shortcuts
;   2. the DLL's log folder, with a DACL LOCAL SERVICE can write to
;   3. the release certificate in LocalMachine Root and TrustedPublisher,
;      then the display driver and its root device, by exodevice.exe
;   4. an inbound firewall rule for the hub, every profile, for Wi-Fi
; Nothing goes to a phone: its app is a separate download.
; The hub writes its own HKCU Run value on first run; uninstall removes
; it and the hub's settings, for the account that elevated.

#ifndef AppVersion
  #error Build with release.ps1
#endif

[Setup]
AppId={{E7A1C2F4-3B8D-4C6E-9A51-0D2F7B8E4C13}
AppName=Exo
AppVersion={#AppVersion}
AppPublisher=hiidz
DefaultDirName={autopf}\Exo
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; MFCreateVirtualCamera is Windows 11 only.
MinVersion=10.0.22000
OutputDir={#Out}
OutputBaseFilename=Exo-{#AppVersion}-setup
SetupIconFile=..\ExoHub\Exo.ico
UninstallDisplayIcon={app}\ExoHub.exe
UninstallDisplayName=Exo
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; PrepareToInstall ends the hub itself.
CloseApplications=no
SetupLogging=yes

[Tasks]
Name: desktopicon; Description: "Put an Exo shortcut on the desktop"

[Files]
Source: "{#Stage}\ExoHub.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\adb\*"; DestDir: "{app}\adb"; Flags: ignoreversion
Source: "{#Stage}\exocam.dll"; DestDir: "{app}"; Flags: ignoreversion regserver
Source: "{#Stage}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "{#Stage}\exodevice.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\exo-release.cer"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Exo"; Filename: "{app}\ExoHub.exe"
Name: "{autodesktop}\Exo"; Filename: "{app}\ExoHub.exe"; Tasks: desktopicon

[Run]
; As the user who ran setup, not elevated: the hub never runs as admin, and
; its Run value must land in that user's HKCU.
Filename: "{app}\ExoHub.exe"; Description: "Start Exo"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallDelete]
Type: filesandordirs; Name: "{win}\Temp\Exo"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function Run(const Exe, Params: String): Integer;
var
  Code: Integer;
begin
  if not ExecAndLogOutput(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    Code := -1;
  Log(Format('%s %s: exit %d', [Exe, Params, Code]));
  Result := Code;
end;

function Sys(const Exe: String): String;
begin
  Result := ExpandConstant('{sys}\') + Exe;
end;

// The hub, in any session (it may be in the tray from Start with Windows),
// and the Frame Server, which keeps the DLL loaded after the last camera
// goes. The Frame Server starts again by itself when an app wants a camera.
procedure StopExo;
begin
  Run(Sys('taskkill.exe'), '/F /IM ExoHub.exe');
  Run(Sys('net.exe'), 'stop FrameServer /y');
end;

// Every rule naming the hub, Exo's own and any Windows made from its
// prompt: cancelling that prompt leaves a block rule, and block beats allow.
procedure RemoveFirewallRules;
begin
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name=all program="' + ExpandConstant('{app}\ExoHub.exe') + '"');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopExo;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Logs: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // Protected, inherited by files: Administrators, SYSTEM, LOCAL SERVICE
  // (the Frame Server's account) and Users, full control.
  Logs := ExpandConstant('{win}\Temp\Exo\logs');
  ForceDirectories(Logs);
  Run(Sys('icacls.exe'), '"' + Logs + '" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-19:(OI)(CI)F *S-1-5-32-545:(OI)(CI)F');

  Run(Sys('certutil.exe'), '-f -addstore Root "' + ExpandConstant('{app}\exo-release.cer') + '"');
  Run(Sys('certutil.exe'), '-f -addstore TrustedPublisher "' + ExpandConstant('{app}\exo-release.cer') + '"');
  if Run(ExpandConstant('{app}\exodevice.exe'), 'install "' + ExpandConstant('{app}\driver\ExoDisplay.inf') + '"') <> 0 then
    SuppressibleMsgBox('The Exo display driver did not install, so the phone cannot be a display. The webcam still works. Details are in the setup log, in %TEMP%.', mbError, MB_OK, IDOK);

  RemoveFirewallRules;
  Run(Sys('netsh.exe'), 'advfirewall firewall add rule name="Exo" dir=in action=allow profile=any program="' + ExpandConstant('{app}\ExoHub.exe') + '"');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  StopExo;
  Run(ExpandConstant('{app}\exodevice.exe'), 'remove');
  Run(Sys('certutil.exe'), '-delstore Root {#CertThumb}');
  Run(Sys('certutil.exe'), '-delstore TrustedPublisher {#CertThumb}');
  RemoveFirewallRules;
  // This hive is the elevating account's; on another account the hub's
  // own toggle removes its Run value.
  RegDeleteValue(HKCU, RunKey, 'Exo');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Exo');
end;
