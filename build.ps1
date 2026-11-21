param([string]$Compiler = 'ISCC.exe')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force build/inputs,build/app/x64,build/app/tessdata | Out-Null
function Fetch($Name,$Url,$Hash) {
    $path = Join-Path 'build/inputs' $Name
    if (!(Test-Path $path)) {
        curl.exe -fsSL --retry 2 $Url -o $path
        if ($LASTEXITCODE) { throw "Download failed: $Name" }
    }
    if ($Hash -and (Get-FileHash $path).Hash -ne $Hash) { throw "Checksum mismatch: $Name" }
}
Fetch 'tesseract.zip' 'https://api.nuget.org/v3-flatcontainer/tesseract/5.2.0/tesseract.5.2.0.nupkg' '202D82FC7C7D8384DF7DA57206D5E1F456CCDABD648C46E67CDFAA3A911D4795'
Fetch 'eng.traineddata' 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/65727574dfcd264acbb0c3e07860e4e9e9b22185/eng.traineddata' '7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2'
Fetch 'VC_redist.x64.exe' 'https://aka.ms/vc14/vc_redist.x64.exe' ''
$sig = Get-AuthenticodeSignature build/inputs/VC_redist.x64.exe
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation,') { throw 'Visual C++ signature check failed' }
Expand-Archive build/inputs/tesseract.zip build/tesseract -Force
Copy-Item build/tesseract/x64/*.dll build/app/x64
Copy-Item build/tesseract/lib/net48/Tesseract.dll build/app
Copy-Item build/inputs/eng.traineddata build/app/tessdata
Copy-Item THIRD-PARTY.txt,Apache-2.0.txt,Leptonica-license.txt build/app
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = (Get-ChildItem src/*.cs).FullName
& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:build/app/Replik.exe /r:build/app/Tesseract.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll $sources
if ($LASTEXITCODE) { throw 'C# compilation failed' }
& $Compiler setup.iss
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
