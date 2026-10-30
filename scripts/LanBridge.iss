#define MyAppName "LanBridge"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "LanBridge contributors"
#define MyAppExeName "LanBridge.App.exe"
#ifndef SourceDir
  #define SourceDir "..\artifacts\LanBridge-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{A7B41D39-5D4E-4D7C-9E39-5D0B8E6D8A41}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\LanBridge
DefaultGroupName={#MyAppName}
OutputDir={#OutputDir}
OutputBaseFilename=LanBridge-Setup
SetupIconFile={#SourceDir}\Assets\LanBridge.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile={#SourceDir}\LICENSE

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LanBridge"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\LanBridge.ico"
Name: "{autodesktop}\LanBridge"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\LanBridge.ico"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 LanBridge"; Flags: nowait postinstall skipifsilent
