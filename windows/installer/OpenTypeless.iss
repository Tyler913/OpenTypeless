; The Windows installer: OpenTypeless-<version>-windows-<arch>-setup.exe, built by scripts\build.ps1 -Package with
; Inno Setup 6 (preinstalled on GitHub's Windows runners; locally: winget install JRSoftware.InnoSetup).
;
;   ISCC /DAppVersion=1.3.8 /DArch=x64 /DSource=<published app folder> installer\OpenTypeless.iss
;
; It installs the same files as the zip, for the current user only (no administrator prompt), into
; %LOCALAPPDATA%\Programs\OpenTypeless: the folder the in-app updater can replace without elevation. It adds a Start
; menu entry, a desktop shortcut (a checkbox, on by default) and an entry in Settings → Apps, whose version the app
; keeps current after in-app updates. Uninstalling quits the app and removes it, its shortcuts and its login entry;
; settings, history and API keys stay (%LOCALAPPDATA%\OpenTypeless and Credential Manager), as the zip would leave them.

#ifndef AppVersion
  #error Pass the version: /DAppVersion=1.2.3
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Source
  #define Source "..\.build\publish\OpenTypeless"
#endif

[Setup]
; Also the registry key name (…\Uninstall\OpenTypeless_is1) the app updates DisplayVersion under: keep in sync with
; Services\InstallerRegistration.cs.
AppId=OpenTypeless
AppName=OpenTypeless
AppVersion={#AppVersion}
AppVerName=OpenTypeless {#AppVersion}
AppPublisher=TylerHong
AppPublisherURL=https://github.com/Tyler913/OpenTypeless
AppSupportURL=https://github.com/Tyler913/OpenTypeless/issues
AppUpdatesURL=https://github.com/Tyler913/OpenTypeless/releases
AppCopyright=Copyright (c) 2026 TylerHong
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\OpenTypeless
DisableProgramGroupPage=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
SetupIconFile=..\src\OpenTypeless\Assets\AppIcon.ico
UninstallDisplayIcon={app}\OpenTypeless.exe
UninstallDisplayName=OpenTypeless
WizardStyle=modern
; The Windows display language picks the installer's; the language dialog shows only when none matches.
ShowLanguageDialog=auto
Compression=lzma2/max
SolidCompression=yes
; A running copy (it starts at login) is closed for the upgrade, and not restarted by Restart Manager: the last page
; offers to open the new version instead.
CloseApplications=force
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=OpenTypeless-{#AppVersion}-windows-{#Arch}-setup

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
; Not shipped with Inno Setup 6 (it is from 7): the project's own translation, from its source repository.
Name: "zh"; MessagesFile: "Languages\ChineseSimplified.isl"
#endif
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OpenTypeless"; Filename: "{app}\OpenTypeless.exe"
Name: "{autodesktop}\OpenTypeless"; Filename: "{app}\OpenTypeless.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenTypeless.exe"; Description: "{cm:LaunchProgram,OpenTypeless}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; After an in-app update the folder holds files this installer never recorded; it's the app's own folder.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.old"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  // Confirmed: quit a running copy (it starts at login), and give Windows a moment to release its files.
  if CurUninstallStep = usUninstall then
  begin
    if Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM OpenTypeless.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       and (ResultCode = 0) then
      Sleep(1500);
  end;
  // The app adds its login entry itself (Settings → General → Open at login).
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'OpenTypeless');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'OpenTypeless');
  end;
end;
