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
$runtime = 'build/app/local'
$modelDir = "$runtime/model"
New-Item -ItemType Directory -Force $modelDir | Out-Null
Fetch 'python.zip' 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'
Expand-Archive -LiteralPath build/inputs/python.zip -DestinationPath $runtime -Force
Set-Content "$runtime/python312._pth" -Encoding ascii -Value @('python312.zip','.','Lib\site-packages','import site')
python -m pip install --disable-pip-version-check --only-binary=:all: --no-deps --require-hashes --python-version 3.12 --platform win_amd64 --implementation cp --target "$runtime/Lib/site-packages" -r local/requirements.txt
if ($LASTEXITCODE) { throw 'Local engine dependency installation failed' }
Fetch 'opus.zip' 'https://huggingface.co/Unbakedcookie/live-translator-models/resolve/50473df5681764ebdef107dd78f7d21da66c28a7/opus-mt-en-tr.zip' 'acabcdc3becfbf4d740c3012d6dc4e8a1ba6ed41a62a125c37f6c228667ff9b9'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path build/inputs/opus.zip))
try {
    foreach($entry in $zip.Entries) {
        if ($entry.Name -in @('model.bin','config.json','shared_vocabulary.json','source_vocabulary.json','target_vocabulary.json','LICENCE.txt')) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path (Resolve-Path $modelDir) $entry.Name),$true)
        }
    }
} finally { $zip.Dispose() }
$upstream='https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-tr/resolve/e539fc16a8a1a0ea5950eb339b595bfcce990e90'
Fetch 'source.spm' "$upstream/source.spm" '180d3f94ddfee5585d78b702290ded0f4f9bd40f299349c8383b32a579fefad4'
Fetch 'target.spm' "$upstream/target.spm" 'cd0c475692178b6c626aadaef01bd8f98d8053946938e4e67005fc1e4811d797'
Copy-Item build/inputs/source.spm,build/inputs/target.spm $modelDir
Copy-Item local/translate.py $runtime
& "$runtime/python.exe" -B -c "import ctranslate2, sentencepiece"
if ($LASTEXITCODE) {throw 'Bundled local runtime failed to start'}
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = (Get-ChildItem src/*.cs).FullName
& $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:build/app/Replik.exe /r:build/app/Tesseract.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll $sources
if ($LASTEXITCODE) { throw 'C# compilation failed' }
& $Compiler setup.iss
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
