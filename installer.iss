[Setup]
AppName=Simply Transfer
AppVersion=1.0.0
DefaultDirName={pf}\SimplyTransfer
DefaultGroupName=Simply Transfer
UninstallDisplayIcon={app}\SimplyTransfer.UI.exe
Compression=lzma2
SolidCompression=yes
OutputDir=dist
OutputBaseFilename=SimplyTransfer-Setup
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Simply Transfer"; Filename: "{app}\SimplyTransfer.UI.exe"
Name: "{commondesktop}\Simply Transfer"; Filename: "{app}\SimplyTransfer.UI.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Run]
; Run the prerequisites script silently after install
Filename: "{app}\SimplyTransfer.UI.exe"; Parameters: "--run-embedded-script setup-prerequisites.ps1 -Elevate -NonInteractive"; Description: "Install required prerequisites (OpenSSH, etc.)"; Flags: runascurrentuser postinstall waituntilterminated
; Launch the app
Filename: "{app}\SimplyTransfer.UI.exe"; Description: "Launch Simply Transfer"; Flags: nowait postinstall skipifsilent
