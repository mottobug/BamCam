; Inno Setup script for BamCam. Built by build/package.ps1, which passes:
;   /DAppVersion=1.2.3 /DSourceDir=<publish folder> /DOutputDir=<folder> /DOutputName=<file> /DArch=x64compatible|arm64 /DIconFile=<ico>

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64compatible"
#endif

[Setup]
AppId={{6B1F2C7E-4E1B-4C38-9A57-3F2D0C9B8A41}
AppName=BamCam
AppVersion={#AppVersion}
AppPublisher=BamCam
DefaultDirName={autopf}\BamCam
DefaultGroupName=BamCam
UninstallDisplayIcon={app}\BamCam.exe
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
SetupIconFile={#IconFile}
LicenseFile={#SourcePath}\..\..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
; Allow installing for the current user only (no admin rights needed).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Off by default: CamBam may already own the .cb extension.
Name: "associate"; Description: "Open .cb drawings with BamCam"; GroupDescription: "File associations:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\BamCam"; Filename: "{app}\BamCam.exe"
Name: "{group}\{cm:UninstallProgram,BamCam}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\BamCam"; Filename: "{app}\BamCam.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.cb\OpenWithProgids"; ValueType: string; ValueName: "BamCam.Drawing"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\BamCam.Drawing"; ValueType: string; ValueName: ""; ValueData: "BamCam drawing"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\BamCam.Drawing\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\BamCam.exe,0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\BamCam.Drawing\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\BamCam.exe"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\BamCam.exe"; Description: "{cm:LaunchProgram,BamCam}"; Flags: nowait postinstall skipifsilent
