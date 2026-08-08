; Chorus Host — Inno Setup script (built by GitHub Actions)
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Chorus Host"
#define MyAppPublisher "Chorus"
#define MyAppURL "https://github.com/Ryancheese/ChorusForWindows"
#define MyAppExeName "Chorus.Host.exe"

[Setup]
AppId={{A7C3E8F1-4B2D-4F9A-9C1E-8D6B5A2F0E31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\Chorus Host
DefaultGroupName=Chorus Host
DisableProgramGroupPage=yes
LicenseFile=
OutputDir=..\artifacts\installer
OutputBaseFilename=ChorusHost-Setup-{#MyAppVersion}
SetupIconFile=..\src\Chorus.Host\Assets\chorus-host.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
