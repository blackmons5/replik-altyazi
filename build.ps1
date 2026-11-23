param([string]$Compiler = 'ISCC.exe', [switch]$BuildOnly, [switch]$PackageOnly, [ValidatePattern('^\d+\.\d+\.\d+$')][string]$CachePatchFrom)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version=(Get-Content version.txt -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version' }
function Build-Application {
    New-Item -ItemType Directory -Force build/inputs,build/app/ocr/models,build/antlr | Out-Null
    function Fetch($Name,$Url,$Hash) {
        $path = Join-Path 'build/inputs' $Name
        if (!(Test-Path $path)) {
            curl.exe -fsSL --retry 2 $Url -o $path
            if ($LASTEXITCODE) { throw "Download failed: $Name" }
        }
        if ($Hash -and (Get-FileHash $path).Hash -ne $Hash) { throw "Checksum mismatch: $Name" }
    }
    Fetch 'VC_redist.x64.exe' 'https://aka.ms/vc14/vc_redist.x64.exe' ''
    $sig = Get-AuthenticodeSignature build/inputs/VC_redist.x64.exe
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation,') { throw 'Visual C++ signature check failed' }
    Copy-Item THIRD-PARTY.txt,Apache-2.0.txt build/app
    $paddle='https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5'
    Fetch 'det.onnx' "$paddle/det/ch_PP-OCRv5_det_mobile.onnx" '4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae'
    Fetch 'rec.onnx' "$paddle/rec/ch_PP-OCRv5_rec_mobile.onnx" '5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5'
    Copy-Item build/inputs/det.onnx,build/inputs/rec.onnx build/app/ocr/models
    Fetch 'antlr.tar.gz' 'https://files.pythonhosted.org/packages/3e/38/7859ff46355f76f8d19459005ca000b6e7012f2f1ca597746cbcd1fbfe5e/antlr4-python3-runtime-4.9.3.tar.gz' 'f224469b4168294902bb1efa80a8bf7855f24c99aef99cbefc1bcd3cce77881b'
    Fetch 'ANTLR-4.9.3.txt' 'https://raw.githubusercontent.com/antlr/antlr4/4.9.3/LICENSE.txt' 'b1b379fcaf3219593a4c433feb1b35c780bed23fafaae440b1ae2771a9521e3a'
    tar.exe -xf build/inputs/antlr.tar.gz -C build/antlr
    if ($LASTEXITCODE) {throw 'ANTLR extraction failed'}
    python -m pip install --disable-pip-version-check --no-compile --only-binary=:all: --no-deps --require-hashes --python-version 3.12 --platform win_amd64 --implementation cp --target build/app/ocr/packages -r ocr/requirements.txt
    if ($LASTEXITCODE) {throw 'PaddleOCR dependency installation failed'}
    Copy-Item build/antlr/antlr4-python3-runtime-4.9.3/src/antlr4 build/app/ocr/packages -Recurse -Force
    Copy-Item ocr/read.py,build/inputs/ANTLR-4.9.3.txt build/app/ocr
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
    Copy-Item local/translate.py,local/script_index.py $runtime
    $database=Join-Path $runtime 'dialogue.sqlite3'
    if(Test-Path $database){Remove-Item -LiteralPath $database}
    & "$runtime/python.exe" -B local/script_index.py dialogue-source.zip $database
    if($LASTEXITCODE){throw 'Subtitle index build failed'}
    Copy-Item local/runtime-id.txt $runtime
    & "$runtime/python.exe" -B -c "import ctranslate2, sentencepiece"
    if ($LASTEXITCODE) {throw 'Bundled local runtime failed to start'}
    $csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    $sources = (Get-ChildItem src/*.cs).FullName
    Set-Content build/ReleaseVersion.cs -Encoding ascii -Value ('[assembly: System.Reflection.AssemblyVersion("' + $version + '.0")]')
    $sources+= (Resolve-Path build/ReleaseVersion.cs).Path
    & $csc /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /out:build/app/Replik.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll $sources
    if ($LASTEXITCODE) { throw 'C# compilation failed' }
}
if (!$PackageOnly) { Build-Application }
if ($BuildOnly) { return }
& $Compiler "/DAppVer=$version" setup.iss
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
& $Compiler "/DAppVer=$version" /DPatch=1 setup.iss
if ($LASTEXITCODE) { throw 'Update compilation failed' }
if ($CachePatchFrom) {
    & $Compiler "/DAppVer=$version" /DPatch=1 "/DCachePatchFrom=$CachePatchFrom" setup.iss
    if ($LASTEXITCODE) { throw 'Cache update compilation failed' }
}
