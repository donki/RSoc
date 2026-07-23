# Rellena credenciales por defecto en la config DISTRIBUIDA (build\), para que el paquete
# funcione recién instalado en una LAN. El repositorio mantiene la config con credenciales
# VACÍAS (sin secretos publicados); estos valores solo se escriben en los artefactos de salida.
#
# La autoactualización preserva rsoc-client-conf.json del usuario, así que estos defaults solo
# afectan a instalaciones nuevas, nunca pisan credenciales ya configuradas.
[CmdletBinding()]
param(
    [string]$ServerBase,                           # carpeta del RSocServer publicado (opcional)
    [string]$ClientDir,                            # carpeta del cliente Windows publicado (opcional)
    [string]$Role = "",                            # "Manager" | "Remote" para el cliente (opcional)
    [string]$ApiUser = "admin",
    [string]$ApiPassword = "admin",
    [string]$ConnectionPassword = "Remoto2024!"
)

$ErrorActionPreference = "Stop"

function Set-Prop($obj, $name, $value) {
    $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value -Force
}

if ($ServerBase) {
    $srvCfg = Join-Path $ServerBase "rsoc-server-config.json"
    if (Test-Path $srvCfg) {
        $j = Get-Content $srvCfg -Raw | ConvertFrom-Json
        $j.Api.User = $ApiUser
        $j.Api.Password = $ApiPassword
        ($j | ConvertTo-Json -Depth 10) | Set-Content $srvCfg -Encoding UTF8
    }
}

if ($ClientDir) {
    $cliCfg = Join-Path $ClientDir "rsoc-client-conf.json"
    if (Test-Path $cliCfg) {
        $j = Get-Content $cliCfg -Raw | ConvertFrom-Json
        Set-Prop $j 'ApiUser' $ApiUser
        Set-Prop $j 'ApiPassword' $ApiPassword
        Set-Prop $j 'ConnectionPassword' $ConnectionPassword
        # El rol del cliente: gestor (ve/controla todos) o remoto (solo se deja controlar).
        # El remoto no necesita credenciales del API (no lista dispositivos): se dejan vacías.
        if ($Role) {
            Set-Prop $j 'Role' $Role
            if ($Role -eq 'Remote') { Set-Prop $j 'ApiUser' ''; Set-Prop $j 'ApiPassword' '' }
        }
        ($j | ConvertTo-Json -Depth 10) | Set-Content $cliCfg -Encoding UTF8
        Write-Host ("  Config cliente '{0}' (rol {1})." -f (Split-Path $ClientDir -Leaf), ($Role ? $Role : 'por defecto')) -ForegroundColor Green
    }
}

Write-Host "  Defaults escritos (usuario API '$ApiUser')." -ForegroundColor Green
