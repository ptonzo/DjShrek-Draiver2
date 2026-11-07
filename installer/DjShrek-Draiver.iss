#define MyAppName "DjShrek-Draiver Cyber Club"
#define MyAppVersion "1.6.0"
#define MyAppPublisher "DjShrek-Draiver"

[Setup]
AppId={{C29F8C2B-817B-49F5-9897-9E72A8E43119}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/ptonzo/DjShrek-Draiver2
AppSupportURL=https://github.com/ptonzo/DjShrek-Draiver2/issues
AppUpdatesURL=https://github.com/ptonzo/DjShrek-Draiver2
AppCopyright=Copyright (C) 2026 DjShrek-Draiver
DefaultDirName={autopf}\DjShrek-Draiver
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\artifacts\installer
OutputBaseFilename=DjShrek-Draiver-Cyber-Club-Setup-x64
SetupIconFile=..\assets\DjShrek-Draiver.ico
UninstallDisplayIcon={app}\DjShrek-Draiver.exe
LicenseFile=..\LICENSE-TERMS-RU.txt
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
SetupLogging=yes
Uninstallable=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.6.0.0
VersionInfoProductVersion=1.6.0
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: checkedonce

[Files]
Source: "..\artifacts\win-x64\DjShrek-Draiver.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE-TERMS-RU.txt"; DestDir: "{app}"; DestName: "LICENSE-TERMS-RU.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\DjShrek-Draiver Cyber Club"; Filename: "{app}\DjShrek-Draiver.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\DjShrek-Draiver Cyber Club"; Filename: "{app}\DjShrek-Draiver.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DjShrek-Draiver.exe"; Description: "Запустить DjShrek-Draiver Cyber Club"; Flags: postinstall nowait skipifsilent runasoriginaluser