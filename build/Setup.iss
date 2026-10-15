#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDirectory
  #error PayloadDirectory is required
#endif
[Setup]
AppId={{792F3FDE-9875-4D6F-A27E-529BBC1600B1}
AppName=Duplicate Video Cleaner
AppVersion={#AppVersion}
AppPublisher=Softhe
AppPublisherURL=https://github.com/Softhe/Duplicate-Video-Cleaner
DefaultDirName={localappdata}\Programs\Duplicate Video Cleaner
DefaultGroupName=Duplicate Video Cleaner
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=DuplicateVideoCleaner-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\DuplicateVideoCleaner\assets\DuplicateVideoCleaner.ico
UninstallDisplayIcon={app}\DuplicateVideoCleaner.exe
LicenseFile=..\LICENSE.txt
CloseApplications=yes
RestartApplications=no
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Duplicate Video Cleaner"; Filename: "{app}\DuplicateVideoCleaner.exe"
Name: "{userdesktop}\Duplicate Video Cleaner"; Filename: "{app}\DuplicateVideoCleaner.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\DuplicateVideoCleaner.exe"; Description: "Launch Duplicate Video Cleaner"; Flags: nowait postinstall skipifsilent
