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
Source: "..\..\dist\SimplyTransfer-Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{srcexe}"; DestDir: "{app}"; DestName: "SimplyTransfer-Setup.exe"; Flags: external skipifsourcedoesntexist
Source: "{src}\profile.json"; DestDir: "{app}"; Flags: external skipifsourcedoesntexist
Source: "{src}\*.pub"; DestDir: "{app}"; Flags: external skipifsourcedoesntexist

[Icons]
Name: "{group}\Simply Transfer"; Filename: "{app}\SimplyTransfer.UI.exe"
Name: "{commondesktop}\Simply Transfer"; Filename: "{app}\SimplyTransfer.UI.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\SimplyTransfer.UI.exe"; Parameters: "--setup-source"; StatusMsg: "Configuring host for Simply Transfer..."; Flags: waituntilterminated runascurrentuser
Filename: "{app}\SimplyTransfer.UI.exe"; Description: "Launch Simply Transfer"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im SimplyTransfer.UI.exe"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  DeleteDataCheckbox: TNewCheckBox;

procedure InitializeUninstallProgressForm();
begin
  DeleteDataCheckbox := TNewCheckBox.Create(UninstallProgressForm);
  DeleteDataCheckbox.Parent := UninstallProgressForm.InnerPage;
  DeleteDataCheckbox.Top := UninstallProgressForm.StatusLabel.Top + UninstallProgressForm.StatusLabel.Height + 20;
  DeleteDataCheckbox.Left := UninstallProgressForm.StatusLabel.Left;
  DeleteDataCheckbox.Width := UninstallProgressForm.InnerPage.ClientWidth;
  DeleteDataCheckbox.Height := 20;
  DeleteDataCheckbox.Caption := 'Delete all profiles, configurations, and keys';
  DeleteDataCheckbox.Checked := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DeleteDataCheckbox.Checked then
    begin
      AppDir := ExpandConstant('{localappdata}\SimplyTransfer');
      if DirExists(AppDir) then
        DelTree(AppDir, True, True, True);
    end;
  end;
end;

