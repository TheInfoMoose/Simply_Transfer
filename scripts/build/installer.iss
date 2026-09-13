#ifndef AppVersion
#define AppVersion "1.0.0"
#endif

[Setup]
AppName=Simply Transfer
AppVersion={#AppVersion}
DefaultDirName={commonpf}\SimplyTransfer
DefaultGroupName=Simply Transfer
UninstallDisplayIcon={app}\SimplyTransfer.UI.exe
Compression=lzma2
SolidCompression=yes
OutputDir=..\..\dist
OutputBaseFilename=SimplyTransfer-Setup-v{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsedUserAreasWarning=no

[Files]
Source: "..\..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\utils\setup-prerequisites.ps1"; DestDir: "{tmp}"; Flags: ignoreversion

[Icons]
Name: "{group}\Simply Transfer"; Filename: "{app}\SimplyTransfer.UI.exe"
Name: "{commondesktop}\Simply Transfer"; Filename: "{app}\SimplyTransfer.UI.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Run]
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{tmp}\setup-prerequisites.ps1"" -InstallMissingDependencies -Elevate -NonInteractive"; StatusMsg: "Installing prerequisites (.NET 8, OpenSSH, VC++)..."; Flags: waituntilterminated
Filename: "{app}\SimplyTransfer.UI.exe"; Description: "Launch Simply Transfer"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im SimplyTransfer.UI.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{cmd}"; Parameters: "/c taskkill /f /im powershell.exe /fi ""WINDOWTITLE eq Simply Transfer*"""; Flags: runhidden; RunOnceId: "KillPS"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\SimplyTransfer"
