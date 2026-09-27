#define AppVersion "0.3.2"

[Setup]
AppId={{E91A8FE5-CBD4-4FFC-B5F6-796B6737F77F}
AppName=AI 划词工具栏
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\AiSelectionToolbar
DefaultGroupName=AI 划词工具栏
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
OutputDir=..\..\..\dist
OutputBaseFilename=AISelectionToolbar-{#AppVersion}-win-x64-setup
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\AiSelectionToolbar.Desktop.exe

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"

[Files]
Source: "..\..\Platforms\Windows\bin\Release\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\AI 划词工具栏"; Filename: "{app}\AiSelectionToolbar.Desktop.exe"
Name: "{userdesktop}\AI 划词工具栏"; Filename: "{app}\AiSelectionToolbar.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AiSelectionToolbar.Desktop.exe"; Description: "运行 AI 划词工具栏"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM32, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('请先安装 .NET Framework 4.8 或更高版本。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'AiSelectionToolbar.Desktop', Command) and
      (CompareText(Command, '"' + ExpandConstant('{app}\AiSelectionToolbar.Desktop.exe') + '"') = 0) then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
        'AiSelectionToolbar.Desktop');
  end;
end;
