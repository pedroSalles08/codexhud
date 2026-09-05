#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{B8CCBA22-F8B1-4A0F-95A5-D060572307EB}
AppName=CodexHUD
AppVersion={#AppVersion}
AppPublisher=Pedro Henrique Encarnação Salles
AppPublisherURL=https://github.com/pedroSalles08/codexhud
AppSupportURL=https://github.com/pedroSalles08/codexhud/issues
AppUpdatesURL=https://github.com/pedroSalles08/codexhud/releases
DefaultDirName={localappdata}\Programs\CodexHUD
DefaultGroupName=CodexHUD
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=CodexHUD-Setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CodexHUD.exe
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CodexHUD
VersionInfoDescription=CodexHUD installer
VersionInfoCompany=Pedro Henrique Encarnação Salles

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CodexHUD"; Filename: "{app}\CodexHUD.exe"; WorkingDir: "{app}"; Comment: "Codex Desktop usage HUD"

[Run]
Filename: "{app}\CodexHUD.exe"; Description: "Open CodexHUD"; Flags: nowait postinstall skipifsilent
