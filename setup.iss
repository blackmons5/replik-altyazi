[Setup]
AppId=ReplikSubtitleTR
AppName=AccelSubs
AppVersion={#AppVer}
#ifdef Patch
DefaultDirName={code:InstalledDir}
DisableDirPage=yes
UsePreviousAppDir=no
#else
DefaultDirName={autopf}\AccelSubs
#endif
DefaultGroupName=AccelSubs
UsePreviousGroup=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=dist
#ifdef CachePatchFrom
OutputBaseFilename=AccelSubs-Guncelleme-{#AppVer}-x64
#else
#ifdef Patch
OutputBaseFilename=Replik-Guncelleme-x64-r1
#else
OutputBaseFilename=AccelSubs-Kurulum-x64
#endif
#endif
Compression=lzma2/fast
SolidCompression=yes
UninstallDisplayIcon={app}\Replik.exe
WizardStyle=modern
PrivilegesRequired=admin

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
Source: "build\app\Replik.exe"; DestDir: "{app}"; Flags: ignoreversion
#ifndef CachePatchFrom
Source: "build\app\THIRD-PARTY.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\Apache-2.0.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\ocr\*"; DestDir: "{app}\ocr"; Flags: recursesubdirs ignoreversion
#ifdef Patch
Source: "build\app\local\translate.py"; DestDir: "{app}\local"; Flags: ignoreversion
Source: "build\app\local\runtime-id.txt"; DestDir: "{app}\local"; Flags: ignoreversion
#else
Source: "build\app\local\*"; DestDir: "{app}\local"; Flags: recursesubdirs ignoreversion
Source: "build\inputs\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: dontcopy
#endif
#endif

[InstallDelete]
Type: files; Name: "{commonprograms}\Replik\Replik.lnk"
Type: dirifempty; Name: "{commonprograms}\Replik"
Type: files; Name: "{app}\Tesseract.dll"
Type: files; Name: "{app}\x64\tesseract*.dll"
Type: files; Name: "{app}\x64\leptonica*.dll"
Type: files; Name: "{app}\tessdata\eng.traineddata"
Type: files; Name: "{app}\Leptonica-license.txt"

[Icons]
Name: "{group}\AccelSubs"; Filename: "{app}\Replik.exe"

[Run]
#ifndef CachePatchFrom
Filename: "{app}\local\python.exe"; Parameters: "-B translate.py --sync-pack"; WorkingDir: "{app}\local"; StatusMsg: "CouchSubs GTA 6 paketi kontrol ediliyor…"; Flags: runhidden runasoriginaluser waituntilterminated
#endif
Filename: "{app}\Replik.exe"; Description: "AccelSubs uygulamasını aç"; Flags: postinstall nowait skipifsilent unchecked

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssDone then begin
    try
      if not ExecAsOriginalUser(ExpandConstant('{app}\Replik.exe'),
        '--clean-update-cache "' + ExpandConstant('{srcexe}') + '"', ExpandConstant('{app}'),
        SW_HIDE, ewNoWait, Code) then Log('Update cache cleanup could not start: ' + SysErrorMessage(Code));
    except
      Log('Update cache cleanup could not start: ' + GetExceptionMessage);
    end;
  end;
end;

#ifdef Patch
const RegPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ReplikSubtitleTR_is1';
function InstalledDir(Param: String): String;
begin
  if not RegQueryStringValue(HKLM64, RegPath, 'InstallLocation', Result) then Result := '';
  Result := RemoveBackslashUnlessRoot(Result);
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir, InstalledVersion: String;
  Marker: AnsiString;
  Compatible: Boolean;
begin
  Dir := InstalledDir('');
  Compatible := LoadStringFromFile(Dir + '\local\runtime-id.txt', Marker) and (Trim(String(Marker)) = 'r1');
  if not Compatible and not FileExists(Dir + '\local\runtime-id.txt') then
    Compatible := RegQueryStringValue(HKLM64, RegPath, 'DisplayVersion', InstalledVersion) and (InstalledVersion = '0.2.0');
  Result := 'Bu küçük güncelleme için uyumlu Replik kurulumu gerekiyor. Tam kurulum dosyasını kullanın.';
  if (Dir <> '') and (CompareText(Dir, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0)
    and Compatible and FileExists(Dir + '\local\model\model.bin') and FileExists(Dir + '\local\python.exe') then Result := '';
#ifdef CachePatchFrom
  if (Result <> '') or not RegQueryStringValue(HKLM64, RegPath, 'DisplayVersion', InstalledVersion) or
    ((InstalledVersion <> '{#CachePatchFrom}') and (InstalledVersion <> '{#AppVer}')) or
    not FileExists(Dir + '\ocr\models\det.onnx') or not FileExists(Dir + '\ocr\models\rec.onnx') or
    not FileExists(Dir + '\ocr\read.py') or not FileExists(Dir + '\ocr\packages\onnxruntime\__init__.py') then
    Result := 'Bu yama için Replik {#CachePatchFrom} veya AccelSubs {#AppVer} kurulumu gerekir.';
#endif
end;
#else
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
#endif
