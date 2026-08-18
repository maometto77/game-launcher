; Inno Setup script for the desktop client.
;
; Normally driven by build-release.ps1, which publishes first and passes the
; version off the built binary. To compile it by hand:
;
;   dotnet publish GameLauncher.Desktop -c Release -p:PublishProfile=win-x64
;   iscc deploy\installer\Don.iss
;
; Produces deploy\installer\output\Don-Setup-<version>.exe.
;
; The published output is a single self-contained executable plus whatever is in
; tools\, so this installs a handful of files rather than a runtime.
;
; This is the optional half of the distribution story. The zip that
; build-release.ps1 produces installs through Install-Don.ps1 and needs neither
; Inno Setup nor a build machine that has it, which is why the zip is what gets
; published and this is a convenience for people who expect a setup.exe.

#define AppName        "Don"

; Supplied by build-release.ps1 as /DAppVersion=x.y.z. The fallback only applies
; to a hand-run compile, so a release can never be stamped with a stale number.
#ifndef AppVersion
  #define AppVersion   "1.0.0"
#endif

#define AppPublisher   "Don"
#define AppExeName     "Don.exe"
#define PublishDir     "..\..\GameLauncher.Desktop\bin\publish\win-x64"

[Setup]
AppId={{8F3B6A21-9C4D-4E7A-B2F1-6D5C0E93A748}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

; Per-machine when run elevated, per-user when not. A launcher is a personal
; application and should not demand an administrator to install.
PrivilegesRequiredOverridesAllowed=dialog
PrivilegesRequired=lowest

OutputDir=output
OutputBaseFilename=Don-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

; The published executable is already compressed by the single-file bundler, so
; the installer's own pass gains little on it — but the tools folder and any
; loose files still benefit.
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=
SetupIconFile=..\..\GameLauncher.Desktop\Resources\Branding\don.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; The whole publish folder. Recursing rather than naming files means a tool
; dropped into tools\ is installed without editing this script.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The extraction cache the single-file bundler creates on first run. Left
; behind, it is a few hundred megabytes of nothing after an uninstall.
Type: filesandordirs; Name: "{localappdata}\Temp\.net\{#AppName}"

[Code]
{
  The library is deliberately not removed. It holds the user's games, playtime,
  collections, achievements and — in settings.json — the only copy of their
  relay token, which the relay stores as a hash and cannot reissue. An uninstall
  that silently deleted all of that would be unforgivable, so it is offered and
  defaults to no.
}
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\{#AppName}');

    if DirExists(DataDir) then
    begin
      if MsgBox('Remove your library as well?' + #13#10#13#10 +
                'This deletes games, playtime, collections, achievements and your relay ' +
                'credentials from:' + #13#10 + DataDir + #13#10#13#10 +
                'Your relay token cannot be recovered afterwards. Choose No to keep everything.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
