# Framework projections and OS metadata for Windows.Media.Ocr. These are
# build references; Windows supplies the implementation on the player's PC.
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
foreach ($name in 'System.Runtime', 'System.Runtime.WindowsRuntime', 'System.Runtime.InteropServices.WindowsRuntime', 'System.Collections') {
    '/r:' + (Join-Path $framework ($name + '.dll'))
}
$metadata=Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/UnionMetadata/*/Windows.winmd" |
    Where-Object { $_.Directory.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1
if (!$metadata) {throw 'Windows 10 or 11 SDK metadata is required for the build.'}
'/r:' + $metadata.FullName
