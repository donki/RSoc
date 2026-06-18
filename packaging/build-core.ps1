# Compila RSocClientCore (núcleo nativo del cliente Windows) a DLL con MSVC.
# Captura DXGI + inyección de input. No requiere cmake.
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir   = Join-Path $repoRoot "client-windows\RSocClientCore"
$outDir   = Join-Path $srcDir "bin"
$dll      = Join-Path $outDir "RSocClientCore.dll"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$vcvarsCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
)
$vcvars = $vcvarsCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vcvars) { throw "No se encontró vcvars64.bat (BuildTools/VS 2022)." }

$opt = if ($Configuration -eq "Debug") { "/Od /Zi" } else { "/O2" }
$clCmd = "cl /nologo /LD /EHsc $opt /std:c++17 /DRSOC_CORE_EXPORTS " +
         "/I`"$srcDir\include`" `"$srcDir\src\rsoc_core.cpp`" `"$srcDir\src\rsoc_codec.cpp`" " +
         "d3d11.lib dxgi.lib user32.lib windowscodecs.lib ole32.lib /Fe:`"$dll`" /Fo:`"$outDir\\`""
$full = "call `"$vcvars`" >nul && $clCmd"

Write-Host "Compilando RSocClientCore ($Configuration)..." -ForegroundColor Cyan
& cmd.exe /c $full
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilación de RSocClientCore (cl exit $LASTEXITCODE)." }
if (-not (Test-Path $dll)) { throw "No se generó $dll." }
Write-Host "OK -> $dll" -ForegroundColor Green
