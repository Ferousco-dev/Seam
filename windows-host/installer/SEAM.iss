; ---------------------------------------------------------------------------------------
;  SEAM - Windows installer (Inno Setup 6).
;
;  Build (from the repo root, after `dotnet publish` has produced the app folder):
;      iscc /DAppVersion=0.9.0 /DSourceDir=<abs path to published folder> windows-host\installer\SEAM.iss
;
;  Layout contract: the installer packages exactly the same folder the portable ZIP is
;  built from (seam-host.exe + seam-agent.jar + platform-tools\ + docs). There is no
;  second, installer-only copy of anything - see .github/workflows/windows-build.yml.
;
;  Why not single-file / self-extracting: the app resolves adb.exe and seam-agent.jar
;  relative to the folder that contains seam-host.exe. A single-file build runs from
;  %TEMP%\.net\... , which is exactly the bug the first real-device test hit.
; ---------------------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

; Folder produced by `dotnet publish` plus the bundled resources. Overridden by CI.
#ifndef SourceDir
  #define SourceDir "..\..\build\publish"
#endif

; Stable AppId: never change it, or upgrades stop being upgrades.
#define AppId "{{6F1C2B34-9D5E-4A7B-8C21-3E4D5A6B7C8D}"
#define AppName "SEAM"
#define AppExe "seam-host.exe"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SEAM
AppPublisherURL=https://github.com/CalebPiusC/Seam
AppSupportURL=https://github.com/CalebPiusC/Seam/issues
AppUpdatesURL=https://github.com/CalebPiusC/Seam
VersionInfoVersion={#AppVersion}

; Per-machine install under Program Files. The app itself needs no admin rights at
; runtime, but writing to Program Files and registering in "Installed apps" does.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Upgrades: same AppId + remembered directory. User data lives outside {app}
; (%USERPROFILE%\.seam\auth_key) so it survives upgrades automatically.
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes

OutputDir=..\..\build\installer
OutputBaseFilename=SEAM-Setup-x64
SetupIconFile=..\packaging\seam.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LicenseFile=..\..\LICENSE
InfoAfterFile=INSTALL-NOTES.txt

; Let the installer close a running seam-host.exe (and its console) instead of
; failing on locked files. Restarting it automatically would be surprising.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Everything the app needs, including platform-tools\adb.exe and seam-agent.jar.
; recursesubdirs keeps the published runtime folders intact.
; restartreplace: if a running adb server (or any other process) still holds one of these
; files, Inno renames it aside and finishes the install, scheduling the old copy for
; deletion at the next restart - instead of aborting with "DeleteFile failed; code 5".
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
; The exe is a GUI-subsystem binary, so these launch the desktop window with no console.
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Comment: "Share one mouse and keyboard with an Android phone"
; Diagnostics prints to a console on purpose, so it runs inside the command processor,
; which also keeps the window open long enough to read the report.
Name: "{group}\{#AppName} diagnostics"; Filename: "{cmd}"; Parameters: "/k ""{app}\{#AppExe}"" --diagnose"; IconFilename: "{app}\{#AppExe}"; Comment: "Check adb, agent and device detection without connecting"
Name: "{group}\{#AppName} quick start"; Filename: "{app}\README-WINDOWS.txt"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start SEAM"; Flags: postinstall nowait skipifsilent unchecked
Filename: "{app}\README-WINDOWS.txt"; Description: "Open the SEAM quick start guide"; Flags: postinstall shellexec skipifsilent unchecked

; No [UninstallDelete] entry on purpose: the uninstaller already removes exactly the
; files it installed, and nothing outside {app} is ever touched except the pairing key,
; which is only deleted when the user explicitly says yes (see [Code] below).

[Code]
{ Windows refuses to delete or replace a running image: "DeleteFile failed; code 5"
  on platform-tools\adb.exe means the adb server started from the installed copy is still
  alive (it outlives the SEAM window). Stop it before any file is copied. restartreplace
  on [Files] is the backstop for anything else that still holds a file. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AdbExe: String;
  ResultCode: Integer;
begin
  Result := '';
  AdbExe := ExpandConstant('{app}\platform-tools\adb.exe');
  if FileExists(AdbExe) then
    Exec(AdbExe, 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Keep the pairing key on upgrade and on normal uninstall; offer to remove it explicitly. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SeamDir, KeyFile: String;
  Answer: Integer;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  SeamDir := ExpandConstant('{userprofile}\.seam');
  KeyFile := SeamDir + '\auth_key';
  if not FileExists(KeyFile) then
    Exit;

  { Never destroy data without an answer: silent uninstall keeps everything. }
  if UninstallSilent then
    Exit;

  Answer := MsgBox('SEAM also stored a pairing key at:' + #13#10 + KeyFile + #13#10 + #13#10 +
                   'Keep it if you may reinstall SEAM and reuse the same pairing. ' +
                   'Delete it now?',
                   mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
  if Answer = IDYES then
  begin
    DeleteFile(KeyFile);
    RemoveDir(SeamDir);
  end;
end;
