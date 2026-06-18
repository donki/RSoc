# Empaqueta los artefactos de autoactualización que hospeda el servidor en <ServerBase>\updates\:
#   updates\windows\RSocClient.zip + version.txt   (zip del cliente Windows publicado)
#   updates\android\RSoc.apk       + version.txt   (APK firmado)
# La versión sale de la constante única AppVersion.Current (src\RSoc.Protocol\AppVersion.cs).
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ServerBase,  # carpeta del RSocServer publicado (donde corre el exe)
    [Parameter(Mandatory)] [string]$ClientDir,   # carpeta del cliente Windows publicado
    [string]$ApkPath                              # APK de Android (opcional)
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# Versión desde la constante única.
$verCs = Get-Content (Join-Path $repo "src\RSoc.Protocol\AppVersion.cs") -Raw
if ($verCs -notmatch 'Current\s*=\s*"([^"]+)"') { throw "No se pudo leer AppVersion.Current." }
$version = $Matches[1]

$updWin = Join-Path $ServerBase "updates\windows"
$updAnd = Join-Path $ServerBase "updates\android"
New-Item -ItemType Directory -Force -Path $updWin, $updAnd | Out-Null

# Cliente Windows -> zip (contenido en la raíz del zip).
$zip = Join-Path $updWin "RSocClient.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $ClientDir '*') -DestinationPath $zip
Set-Content (Join-Path $updWin "version.txt") $version -NoNewline -Encoding ascii
$winSha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Write-Host ("  updates\windows: v{0}  {1:N1} MB  sha256 {2}…" -f $version, ((Get-Item $zip).Length/1MB), $winSha.Substring(0,8)) -ForegroundColor Green

# APK Android (si existe).
if ($ApkPath -and (Test-Path $ApkPath)) {
    Copy-Item $ApkPath (Join-Path $updAnd "RSoc.apk") -Force
    Set-Content (Join-Path $updAnd "version.txt") $version -NoNewline -Encoding ascii
    $apkSha = (Get-FileHash (Join-Path $updAnd "RSoc.apk") -Algorithm SHA256).Hash.ToLower()
    Write-Host ("  updates\android: v{0}  {1:N1} MB  sha256 {2}…" -f $version, ((Get-Item (Join-Path $updAnd 'RSoc.apk')).Length/1MB), $apkSha.Substring(0,8)) -ForegroundColor Green
} else {
    Write-Host "  updates\android: (sin APK; se omite)" -ForegroundColor Yellow
}
