# Instala RSocServer y RSocRelay como servicios de Windows (arranque automático) y abre el
# firewall. Se auto-eleva a administrador. Pensado para ejecutarse desde build\server (donde
# build-dist.ps1 deja este script junto a RSocRelay.exe y a la carpeta RSocServer\).
#
# Puertos por defecto (originales): API 21114, relay 21117. El puerto del API también se puede
# fijar en RSocServer\rsoc-server-config.json (Server.ApiPort).
[CmdletBinding()]
param(
    [int]$RelayPort = 21117,
    [int]$ApiPort   = 21114
)

$ErrorActionPreference = "Stop"

# Auto-elevación.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) {
    Write-Host "Elevando a administrador…" -ForegroundColor Yellow
    Start-Process pwsh -Verb RunAs -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath,
        "-RelayPort", $RelayPort, "-ApiPort", $ApiPort)
    return
}

$here      = $PSScriptRoot
$relayExe  = Join-Path $here "RSocRelay.exe"
$serverExe = Join-Path $here "RSocServer\RSocServer.exe"
foreach ($e in @($relayExe, $serverExe)) {
    if (-not (Test-Path $e)) { throw "No existe $e. Ejecuta antes packaging\build-dist.ps1." }
}

function Install-RSocService([string]$Name, [string]$BinPath, [string]$Desc) {
    if (Get-Service $Name -ErrorAction SilentlyContinue) {
        Write-Host "Reinstalando servicio $Name…" -ForegroundColor Yellow
        sc.exe stop $Name | Out-Null
        sc.exe delete $Name | Out-Null
        Start-Sleep -Seconds 1
    }
    sc.exe create $Name binPath= $BinPath start= auto | Out-Null
    sc.exe description $Name $Desc | Out-Null
    sc.exe start $Name | Out-Null
    Write-Host "Servicio $Name instalado y arrancado." -ForegroundColor Green
}

# RSocRelay: reenviador TCP en el puerto del relay.
Install-RSocService "RSocRelay" "`"$relayExe`" $RelayPort" "RSoc - reenviador de tráfico de sesión"

# RSocServer: API/señalización por HTTPS. El puerto sale de rsoc-server-config.json (Server.ApiPort).
Install-RSocService "RSocServer" "`"$serverExe`"" "RSoc - registro y señalización (HTTPS)"

# Firewall.
foreach ($rule in @(
    @{ Name = "RSoc Relay (TCP $RelayPort)"; Port = $RelayPort },
    @{ Name = "RSoc API (TCP $ApiPort)";     Port = $ApiPort })) {
    Remove-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $rule.Port | Out-Null
}

Write-Host "`nInstalación completada. Relay TCP $RelayPort, API TCP $ApiPort." -ForegroundColor Cyan
Write-Host "Edita RSocServer\rsoc-server-config.json para IPs/credenciales y reinicia los servicios." -ForegroundColor Cyan
