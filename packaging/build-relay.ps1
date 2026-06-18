# Compila RSocRelay (C++) con MSVC. No requiere cmake.
# Carga el entorno de desarrollador de BuildTools 2022 (vcvars64) y llama a cl.exe.
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir   = Join-Path $repoRoot "src\RSocRelay"
$outDir   = Join-Path $srcDir "bin"
$exePath  = Join-Path $outDir "RSocRelay.exe"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Localiza vcvars64.bat (BuildTools 2022 o Visual Studio 2022).
$vcvarsCandidates = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
)
$vcvars = $vcvarsCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vcvars) { throw "No se encontró vcvars64.bat (BuildTools/VS 2022)." }

$opt = if ($Configuration -eq "Debug") { "/Od /Zi" } else { "/O2" }

# Compila dentro del entorno vcvars (cmd) y captura el código de salida.
$clCmd = "cl /nologo /EHsc $opt /std:c++17 `"$srcDir\relay.cpp`" ws2_32.lib /Fe:`"$exePath`" /Fo:`"$outDir\\`""
$full  = "call `"$vcvars`" >nul && $clCmd"

Write-Host "Compilando RSocRelay ($Configuration)..." -ForegroundColor Cyan
& cmd.exe /c $full
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilación de RSocRelay (cl exit $LASTEXITCODE)." }

if (-not (Test-Path $exePath)) { throw "No se generó $exePath." }
Write-Host "OK -> $exePath" -ForegroundColor Green
