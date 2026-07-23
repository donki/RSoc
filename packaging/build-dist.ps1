# Genera el build completo en build\ (servidores + cliente Windows), listo para distribuir.
# Todo en ejecutables SINGLE-FILE (un solo .exe autónomo por componente + su JSON de config).
#   build\server\          -> RSocServer\RSocServer.exe (single-file) + RSocRelay.exe + install-service.ps1
#   build\client-gestor\   -> RSocGestor.exe (single-file) + rsoc-client-conf.json (rol Manager)
#   build\client-remoto\   -> RSocRemoto.exe (single-file) + rsoc-client-conf.json (rol Remote)
#   build\android\         -> RSoc.apk
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
$cliG  = Join-Path $build "client-gestor"
$cliR  = Join-Path $build "client-remoto"
$stage = Join-Path $build "_client-stage"
$andr  = Join-Path $build "android"

# Publicación single-file, autónoma y comprimida. IncludeAllContentForSelfExtract embebe también
# el núcleo nativo RSocClientCore.dll (la config JSON queda fuera por ExcludeFromSingleFile).
$sfClient = @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none", "-p:DebugSymbols=false"   # sin .pdb en el paquete: un único .exe
)
$sfServer = @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none", "-p:DebugSymbols=false"
)

# Versión AAAA.MM.DD.N: incrementa N en cada build (antes de compilar, para hornearla).
if (-not $NoVersionBump) {
    Write-Host "== Versionado ==" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "bump-version.ps1") | Out-Null
}

# Siempre se limpia por completo la carpeta de salida antes de construir.
Write-Host "== Limpiando build\ ==" -ForegroundColor Cyan
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $srv, $cliG, $cliR, $stage, $andr | Out-Null

Write-Host "== Componentes nativos (C++) ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "build-relay.ps1") -Configuration $Configuration
& (Join-Path $PSScriptRoot "build-core.ps1")  -Configuration $Configuration

Write-Host "== Publicando RSocServer (single-file) ==" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\RSocServer\RSocServer.csproj") `
    -c $Configuration -r $Runtime --self-contained true $sfServer `
    -o (Join-Path $srv "RSocServer") --nologo
if ($LASTEXITCODE -ne 0) { throw "Fallo publish RSocServer." }

Copy-Item (Join-Path $repo "src\RSocRelay\bin\RSocRelay.exe") $srv -Force
Copy-Item (Join-Path $PSScriptRoot "install-service.ps1") $srv -Force

Write-Host "== Publicando cliente Windows (single-file) ==" -ForegroundColor Cyan
dotnet publish (Join-Path $repo "client-windows\RSoc.WindowsApp\RSoc.WindowsApp.csproj") `
    -c $Configuration -r $Runtime --self-contained true $sfClient `
    -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "Fallo publish cliente." }

# Un ejecutable por rol, cada uno en su carpeta con su propia config. El rol se deduce del nombre
# del .exe (RSocGestor / RSocRemoto) y, además, se fija en la config.
$appExe = Join-Path $stage "RSoc.WindowsApp.exe"
$appCfg = Join-Path $stage "rsoc-client-conf.json"
Copy-Item $appExe (Join-Path $cliG "RSocGestor.exe") -Force
Copy-Item $appCfg $cliG -Force
Copy-Item $appExe (Join-Path $cliR "RSocRemoto.exe") -Force
Copy-Item $appCfg $cliR -Force
Write-Host "  Cliente gestor:  client-gestor\RSocGestor.exe" -ForegroundColor DarkGray
Write-Host "  Cliente remoto:  client-remoto\RSocRemoto.exe" -ForegroundColor DarkGray

Write-Host "== Compilando APK Android ==" -ForegroundColor Cyan
# EmbedAssembliesIntoApk: APK autónomo instalable suelto (sin Fast Deployment).
dotnet build (Join-Path $repo "client-android\RSoc.Android\RSoc.Android.csproj") -c Debug -p:EmbedAssembliesIntoApk=true --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Fallo build Android." }
$apk = Get-ChildItem (Join-Path $repo "client-android\RSoc.Android\bin\Debug\net10.0-android") -Filter "*-Signed.apk" |
       Select-Object -First 1
if ($apk) { Copy-Item $apk.FullName (Join-Path $andr "RSoc.apk") -Force }

Write-Host "== Datos por defecto en la config distribuida (no en el repo) ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "set-build-defaults.ps1") -ServerBase (Join-Path $srv "RSocServer") -ClientDir $cliG -Role Manager
& (Join-Path $PSScriptRoot "set-build-defaults.ps1") -ClientDir $cliR -Role Remote

Write-Host "== Empaquetando artefactos de autoactualización (updates\) ==" -ForegroundColor Cyan
# Se colocan dentro del RSocServer publicado para que el servidor los hospede y viajen con él.
& (Join-Path $PSScriptRoot "pack-updates.ps1") `
    -ServerBase (Join-Path $srv "RSocServer") `
    -ClientExe  $appExe `
    -ApkPath    (Join-Path $andr "RSoc.apk")

# Limpia el staging del cliente.
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`nBuild completo en: $build" -ForegroundColor Green
Write-Host "  Servidores: $srv   (ejecuta install-service.ps1 como admin)" -ForegroundColor Green
Write-Host "  Gestor:     $cliG   (RSocGestor.exe + rsoc-client-conf.json)" -ForegroundColor Green
Write-Host "  Remoto:     $cliR   (RSocRemoto.exe + rsoc-client-conf.json)" -ForegroundColor Green
Write-Host "  Android:    $andr  (RSoc.apk)" -ForegroundColor Green
