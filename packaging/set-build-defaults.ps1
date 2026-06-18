# Rellena credenciales por defecto en la config DISTRIBUIDA (build\), para que el paquete
# funcione recién instalado en una LAN. El repositorio mantiene la config con credenciales
# VACÍAS (sin secretos publicados); estos valores solo se escriben en los artefactos de salida.
#
# La autoactualización preserva rsoc-client-conf.json del usuario, así que estos defaults solo
# afectan a instalaciones nuevas, nunca pisan credenciales ya configuradas.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ServerBase,   # carpeta del RSocServer publicado
    [Parameter(Mandatory)] [string]$ClientDir,    # carpeta del cliente Windows publicado
    [string]$ApiUser = "admin",
    [string]$ApiPassword = "admin",
    [string]$ConnectionPassword = "Remoto2024!"
)

$ErrorActionPreference = "Stop"

$srvCfg = Join-Path $ServerBase "rsoc-server-config.json"
if (Test-Path $srvCfg) {
    $j = Get-Content $srvCfg -Raw | ConvertFrom-Json
    $j.Api.User = $ApiUser
    $j.Api.Password = $ApiPassword
    ($j | ConvertTo-Json -Depth 10) | Set-Content $srvCfg -Encoding UTF8
}

$cliCfg = Join-Path $ClientDir "rsoc-client-conf.json"
if (Test-Path $cliCfg) {
    $j = Get-Content $cliCfg -Raw | ConvertFrom-Json
    $j.ApiUser = $ApiUser
    $j.ApiPassword = $ApiPassword
    $j.ConnectionPassword = $ConnectionPassword
    ($j | ConvertTo-Json -Depth 10) | Set-Content $cliCfg -Encoding UTF8
}

Write-Host "  Config distribuida con credenciales por defecto (usuario '$ApiUser')." -ForegroundColor Green
