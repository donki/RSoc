# Genera el build completo en build\ (servidores + cliente Windows), listo para distribuir.
#   build\server\   -> RSocServer (publish) + RSocRelay.exe + install-service.ps1
#   build\client\   -> RSoc.WindowsApp (publish) + RSocClientCore.dll + rsoc-client-conf.json
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoVersionBump   # no incrementar la versión (reusa la actual)
)

$ErrorActionPreference = "Stop"
$repo  = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "build"
$srv   = Join-Path $build "server"
$cli   = Join-Path $build "client"
$andr  = Join-Path $build "android"

# Versión AAAA.MM.DD.N: incrementa N en cada build (antes de compilar, para hornearla).
if (-not $NoVersionBump) {
    Write-Host "== Versionado ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "bump-version.ps1") | Out-Null
}

Write-Host "== Limpiando build\ ==" -ForegroundColor Cyan
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $srv, $cli, $andr | Out-Null

Write-Host "== Componentes nativos (C++) ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "build-relay.ps1") -Configuration $Configuration
& (Join-Path $PSScriptRoot "build-core.ps1")  -Configuration $Configuration

Write-Host "== Publicando RSocServer ==" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\RSocServer\RSocServer.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -o (Join-Path $srv "RSocServer") --nologo
if ($LASTEXITCODE -ne 0) { throw "Fallo publish RSocServer." }

Copy-Item (Join-Path $repo "src\RSocRelay\bin\RSocRelay.exe") $srv -Force
Copy-Item (Join-Path $PSScriptRoot "install-service.ps1") $srv -Force

Write-Host "== Publicando cliente Windows ==" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "client-windows\RSoc.WindowsApp\RSoc.WindowsApp.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -o $cli --nologo
if ($LASTEXITCODE -ne 0) { throw "Fallo publish cliente." }

# Asegura el núcleo nativo y la config junto al cliente.
Copy-Item (Join-Path $repo "client-windows\RSocClientCore\bin\RSocClientCore.dll") $cli -Force
Copy-Item (Join-Path $repo "client-windows\RSoc.WindowsApp\rsoc-client-conf.json") $cli -Force

Write-Host "== Compilando APK Android ==" -ForegroundColor Cyan
# EmbedAssembliesIntoApk: APK autónomo instalable suelto (sin Fast Deployment).
dotnet build (Join-Path $repo "client-android\RSoc.Android\RSoc.Android.csproj") -c Debug -p:EmbedAssembliesIntoApk=true --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Fallo build Android." }
$apk = Get-ChildItem (Join-Path $repo "client-android\RSoc.Android\bin\Debug\net10.0-android") -Filter "*-Signed.apk" |
       Select-Object -First 1
if ($apk) { Copy-Item $apk.FullName (Join-Path $andr "RSoc.apk") -Force }

Write-Host "== Datos por defecto en la config distribuida (no en el repo) ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "set-build-defaults.ps1") `
    -ServerBase (Join-Path $srv "RSocServer") -ClientDir $cli

Write-Host "== Empaquetando artefactos de autoactualización (updates\) ==" -ForegroundColor Cyan
# Se colocan dentro del RSocServer publicado para que el servidor los hospede y viajen con él.
& (Join-Path $PSScriptRoot "pack-updates.ps1") `
    -ServerBase (Join-Path $srv "RSocServer") `
    -ClientDir  $cli `
    -ApkPath    (Join-Path $andr "RSoc.apk")

Write-Host "`nBuild completo en: $build" -ForegroundColor Green
Write-Host "  Servidores: $srv   (ejecuta install-service.ps1 como admin)" -ForegroundColor Green
Write-Host "  Cliente:    $cli   (RSoc.WindowsApp.exe + rsoc-client-conf.json)" -ForegroundColor Green
Write-Host "  Android:    $andr  (RSoc.apk)" -ForegroundColor Green
