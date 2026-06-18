# Sube la versión a formato fecha AAAA.MM.DD.N e incrementa N en cada build.
#   · Si la versión actual ya es de HOY -> N = último + 1.
#   · Si es de otro día (o no-fecha) -> N = 1.
# Escribe la constante única AppVersion.Current y, para Android, ApplicationVersion (versionCode
# entero monótono) y ApplicationDisplayVersion. Devuelve la versión nueva por salida estándar.
[CmdletBinding()]
param([string]$Date)  # opcional, "AAAA.MM.DD" (por defecto hoy)

$ErrorActionPreference = "Stop"
$repo  = Split-Path -Parent $PSScriptRoot
$verCs = Join-Path $repo "src\RSoc.Protocol\AppVersion.cs"

$content = Get-Content $verCs -Raw
if ($content -notmatch 'Current\s*=\s*"([^"]+)"') { throw "No se encontró AppVersion.Current en $verCs." }
$current = $Matches[1]

if (-not $Date) { $Date = (Get-Date).ToString("yyyy.MM.dd") }
$parts = $Date.Split('.')
if ($parts.Count -ne 3) { throw "Fecha inválida '$Date' (esperado AAAA.MM.DD)." }
$y = [int]$parts[0]; $m = [int]$parts[1]; $d = [int]$parts[2]
$datePrefix = "{0:0000}.{1:00}.{2:00}" -f $y, $m, $d

$build = 1
if ($current -like "$datePrefix.*") {
    $last = ($current.Split('.'))[3]
    if ($last -match '^\d+$') { $build = [int]$last + 1 }
}
$newVersion = "$datePrefix.$build"

# 1) Constante única (cliente y servidor).
$content = [regex]::Replace($content, 'Current\s*=\s*"[^"]+"', "Current = `"$newVersion`"")
Set-Content $verCs $content -Encoding UTF8

# 2) Android: versionCode entero monótono ((yy*10000+MM*100+dd)*100+N) y displayVersion.
$andCsproj = Join-Path $repo "client-android\RSoc.Android\RSoc.Android.csproj"
$code = ((($y - 2000) * 10000) + ($m * 100) + $d) * 100 + $build
$a = Get-Content $andCsproj -Raw
$a = [regex]::Replace($a, '<ApplicationVersion>[^<]*</ApplicationVersion>', "<ApplicationVersion>$code</ApplicationVersion>")
$a = [regex]::Replace($a, '<ApplicationDisplayVersion>[^<]*</ApplicationDisplayVersion>', "<ApplicationDisplayVersion>$newVersion</ApplicationDisplayVersion>")
Set-Content $andCsproj $a -Encoding UTF8

Write-Host "Versión: $current -> $newVersion  (Android versionCode $code)" -ForegroundColor Green
return $newVersion
