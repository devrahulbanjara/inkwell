; Inkwell for Windows — per-user installer (no admin prompt).
; Built by windows/scripts/build.ps1:  ISCC /DAppVersion=0.1.2 installer\Inkwell.iss
; The in-app updater runs it with /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6E3B7F0A-8C1D-4B5E-9F2A-1D4C7B8E9A01}
AppName=Inkwell
AppVersion={#AppVersion}
AppVerName=Inkwell {#AppVersion}
AppPublisher=Rahul Dev Banjara
AppPublisherURL=https://inkwell.rahuldevbanjara.com.np
AppSupportURL=https://github.com/devrahulbanjara/inkwell/issues
AppUpdatesURL=https://github.com/devrahulbanjara/inkwell/releases
DefaultDirName={localappdata}\Programs\Inkwell
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=Inkwell-Setup
SetupIconFile=..\Inkwell\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Inkwell.exe
UninstallDisplayName=Inkwell
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=Inkwell.exe
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Inkwell
VersionInfoDescription=Inkwell Setup
LicenseFile=..\..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Inkwell when I sign in to Windows"; GroupDescription: "Options:"

[Files]
Source: "..\publish\win-x64\Inkwell.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Inkwell"; Filename: "{app}\Inkwell.exe"; Comment: "AI writing help in every app"

[Registry]
; Same value the app's "Launch at login" switch manages. Only set on interactive installs, so silent
; updates never turn it back on after someone switched it off in Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Inkwell"; \
  ValueData: """{app}\Inkwell.exe"" --background"; Tasks: startup; Check: not WizardSilent; Flags: uninsdeletevalue

[Run]
; Interactive install: offer to open Inkwell (it shows its dashboard so people can find the tray icon).
Filename: "{app}\Inkwell.exe"; Description: "Open Inkwell"; Flags: nowait postinstall skipifsilent
; Silent update: bring Inkwell straight back, quietly.
Filename: "{app}\Inkwell.exe"; Parameters: "--background --updated"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im Inkwell.exe /f"; Flags: runhidden; RunOnceId: "StopInkwell"

[UninstallDelete]
; Settings, keys and stats in %LOCALAPPDATA%\Inkwell are kept, so reinstalling picks up where you left off.
Type: filesandordirs; Name: "{app}"
