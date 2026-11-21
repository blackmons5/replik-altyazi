[Setup]
AppId=ReplikSubtitleTR
AppName=Replik
AppVersion=0.1.0
DefaultDirName={autopf}\Replik
DefaultGroupName=Replik
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=dist
OutputBaseFilename=Replik-Kurulum-x64
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\Replik.exe
WizardStyle=modern

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
Source: "build\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "build\inputs\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\Replik"; Filename: "{app}\Replik.exe"

[Run]
Filename: "{app}\Replik.exe"; Description: "Replik uygulamasını aç"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Minor: Cardinal;
begin
  Result := '';
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Minor', Minor) and (Minor >= 29) then Exit;
  ExtractTemporaryFile('VC_redist.x64.exe');
  if not Exec(ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := 'Microsoft Visual C++ kurulumu başlatılamadı.'
  else if Code = 3010 then NeedsRestart := True
  else if (Code <> 0) and (Code <> 1638) then
    Result := Format('Microsoft Visual C++ kurulumu tamamlanamadı (%d).', [Code]);
end;
