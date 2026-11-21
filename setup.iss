[Setup]
AppId=ReplikSubtitleTR
AppName=Replik
AppVersion={#AppVer}
#ifdef Patch
DefaultDirName={code:InstalledDir}
DisableDirPage=yes
UsePreviousAppDir=no
#else
DefaultDirName={autopf}\Replik
#endif
DefaultGroupName=Replik
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=dist
#ifdef Patch
OutputBaseFilename=Replik-Guncelleme-x64-r1
#else
OutputBaseFilename=Replik-Kurulum-x64
#endif
Compression=lzma2/fast
SolidCompression=yes
UninstallDisplayIcon={app}\Replik.exe
WizardStyle=modern
PrivilegesRequired=admin

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
#ifdef Patch
Source: "build\app\Replik.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\THIRD-PARTY.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\app\local\translate.py"; DestDir: "{app}\local"; Flags: ignoreversion
Source: "build\app\local\runtime-id.txt"; DestDir: "{app}\local"; Flags: ignoreversion
#else
Source: "build\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "build\inputs\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: dontcopy
#endif

[Icons]
Name: "{group}\Replik"; Filename: "{app}\Replik.exe"

[Run]
Filename: "{sys}\dism.exe"; Parameters: "/Online /Add-Capability /CapabilityName:Language.OCR~~~en-US~0.0.1.0 /Quiet /NoRestart"; StatusMsg: "Bu bilgisayarda İngilizce Windows OCR hazırlanıyor. Windows Update birkaç dakika sürebilir…"; Check: NeedsEnglishOcr; AfterInstall: CheckEnglishOcrResult; Flags: runhidden waituntilterminated
#ifndef Patch
Filename: "{app}\local\python.exe"; Parameters: "-B translate.py --sync-pack"; WorkingDir: "{app}\local"; StatusMsg: "CouchSubs GTA 6 paketi kontrol ediliyor…"; Flags: runhidden runasoriginaluser waituntilterminated
#endif
Filename: "{app}\Replik.exe"; Description: "Replik uygulamasını aç"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function HasEnglishOcr: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\Replik.exe'), '--check-windows-ocr', ExpandConstant('{app}'),
    SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function NeedsEnglishOcr: Boolean;
begin
  Result := not HasEnglishOcr;
  if Result then Log('Requesting English OCR from Windows servicing')
  else Log('English Windows OCR is already available');
end;

procedure CheckEnglishOcrResult;
begin
  if HasEnglishOcr then Log('English Windows OCR is available after setup')
  else begin
    Log('English Windows OCR unavailable after servicing; bundled Tesseract is active');
    SuppressibleMsgBox('İngilizce Windows OCR şu anda kullanılamıyor. Replik, kurulumdaki Tesseract ile çalışacak. Windows Update erişimi sağlandığında kurulumu yeniden çalıştırabilirsiniz.', mbInformation, MB_OK, IDOK);
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
    and Compatible and FileExists(Dir + '\local\model\model.bin') and FileExists(Dir + '\local\python.exe')
    and FileExists(Dir + '\tessdata\eng.traineddata') then Result := '';
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
