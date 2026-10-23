[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Directory, [switch]$DownloadOnly, [switch]$Graphics, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$tools = @(
    @{ Name='innosetup-6.7.3.exe'; Url='https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'; Hash='9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732' },
    @{ Name='mesa-llvmpipe-x64-26.2.5.7z'; Url='https://github.com/mmozeiko/build-mesa/releases/download/26.2.5/mesa-llvmpipe-x64-26.2.5.7z'; Hash='d1b713ca2a7fba47a50c058099f13c435ddb76a16a422c334e4d787f9755a31d' }
)
$downloadAll = $DownloadOnly -and -not ($Graphics -or $Installer)
foreach ($tool in $tools) {
    if (-not $downloadAll -and (($tool.Name -like 'inno*' -and -not $Installer) -or ($tool.Name -like 'mesa*' -and -not $Graphics))) { continue }
    $path = Join-Path $Directory $tool.Name
    if ($DownloadOnly) { Invoke-WebRequest $tool.Url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $tool.Hash) { throw "Pinned CI tool checksum mismatch: $($tool.Name)" }
}
if ($DownloadOnly) { return }
if ($Installer) {
    $setup = Join-Path $Directory 'innosetup-6.7.3.exe'
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') { throw 'Unexpected Inno Setup publisher.' }
    $inno = Join-Path $Directory 'inno'
    $process = Start-Process -FilePath $setup -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('/DIR="' + $inno + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Inno Setup installation failed.' }
    "MANA_ISCC=$inno/ISCC.exe" >> $env:GITHUB_ENV
}
if ($Graphics) {
    $mesa = Join-Path $Directory 'mesa'
    & 7z x (Join-Path $Directory 'mesa-llvmpipe-x64-26.2.5.7z') "-o$mesa" -y
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $mesa 'opengl32.dll'))) { throw 'Mesa extraction failed.' }
    "SDL_OPENGL_LIBRARY=$mesa/opengl32.dll" >> $env:GITHUB_ENV
    'GALLIUM_DRIVER=llvmpipe' >> $env:GITHUB_ENV
}
