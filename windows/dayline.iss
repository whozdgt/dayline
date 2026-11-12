; Builds dayline-setup.exe from the folder the workflow assembles.
; Run:  iscc /DBuild=7 /DAppDir=C:\path\to\app /DOutDir=C:\path\out dayline.iss
#ifndef Build
  #define Build "0"
#endif
#ifndef AppDir
  #define AppDir "app"
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{3F6C2B1E-94D7-4E0A-8B52-6A1D9C7E4F20}
AppName=Dayline
AppVersion=1.0.{#Build}
AppPublisher=whozdgt
AppPublisherURL=https://github.com/whozdgt/dayline
; Installs for the current person only, so no administrator prompt is needed.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Dayline
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir={#OutDir}
OutputBaseFilename=dayline-setup
SetupIconFile=dayline.ico
UninstallDisplayIcon={app}\Dayline.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Put a Dayline shortcut on the desktop"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Dayline"; Filename: "{app}\Dayline.exe"
Name: "{autodesktop}\Dayline"; Filename: "{app}\Dayline.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Dayline.exe"; Description: "Open Dayline"; Flags: nowait postinstall skipifsilent
