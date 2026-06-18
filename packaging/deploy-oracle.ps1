# Despliega RSoc (RSocServer + RSocRelay) en una VM Windows de Oracle Cloud (OCI) desde la
# máquina de desarrollo, usando PowerShell Remoting (WinRM).
#
# Hace tres cosas:
#   1) Build "solo servidor" en build\server\  (RSocRelay.exe + RSocServer\publish + install-service.ps1).
#      Reutiliza build-relay.ps1 y dotnet publish; NO compila cliente ni Android.
#   2) Copia ese build a la VM (RemoteDir) por la sesión WinRM.
#   3) Ejecuta install-service.ps1 en la VM: instala/arranca los servicios y abre el firewall local.
#
# Requisitos en la VM (Windows Server en OCI):
#   - WinRM habilitado:  winrm quickconfig   (o  Enable-PSRemoting -Force)
#   - Security List / NSG de OCI con ingress abierto para: WinRM (5985/5986),
#     API (por defecto 21114/TCP) y Relay (por defecto 21117/TCP).
#   - Cuenta administradora. Para cuentas LOCALES sobre WinRM, UAC filtra el token: define
#     en la VM  HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\LocalAccountTokenFilterPolicy = 1
#     (si no, install-service.ps1 no podrá crear servicios y el script lo avisará).
#
# Ejemplos:
#   .\packaging\deploy-oracle.ps1 -VmHost 140.x.x.x -User opc
#   .\packaging\deploy-oracle.ps1 -VmHost vm.midominio.com -UseSSL -ConfigureTrustedHosts
#   .\packaging\deploy-oracle.ps1 -VmHost 140.x.x.x -SkipBuild -ArtifactsDir .\build\server
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$VmHost,           # IP pública o DNS de la VM
    [string]$User = "opc",                            # usuario admin de la VM
    [pscredential]$Credential,                        # si no se pasa, se pide por consola
    [int]$RelayPort = 21117,
    [int]$ApiPort   = 21114,
    [string]$RemoteDir = "C:\RSoc",                   # destino en la VM
    [string]$RelayHost,                               # IP/DNS PÚBLICO del relay que se anuncia a los
                                                      # clientes (por defecto = VmHost). Debe ser
                                                      # alcanzable desde otras redes, no una IP de LAN.
    [switch]$UseSSL,                                  # WinRM por HTTPS (5986) en vez de HTTP (5985)
    [int]$WinRmPort,                                  # fuerza puerto WinRM (por defecto 5985/5986)
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ArtifactsDir,                            # carpeta de build ya hecha (con -SkipBuild)
    [switch]$SkipBuild,                               # no recompilar; usa ArtifactsDir o build\server
    [switch]$ConfigureTrustedHosts                    # añade VmHost a WSMan TrustedHosts (HTTP)
)

$ErrorActionPreference = "Stop"
$repo  = Split-Path -Parent $PSScriptRoot

# El relay corre en la misma VM, así que por defecto se anuncia con la dirección de la VM
# (pública). Si es una IP de LAN, los equipos de OTRA red no podrán conectarse al relay.
if (-not $RelayHost) { $RelayHost = $VmHost }

# --------------------------------------------------------------------------------------------
# 1) Build solo-servidor (salvo -SkipBuild).
# --------------------------------------------------------------------------------------------
if ($SkipBuild) {
    $staging = if ($ArtifactsDir) { (Resolve-Path $ArtifactsDir).Path } else { Join-Path $repo "build\server" }
    if (-not (Test-Path $staging)) { throw "No existe el build en '$staging'. Quita -SkipBuild o pasa -ArtifactsDir." }
    Write-Host "== Usando build existente: $staging ==" -ForegroundColor Cyan
} else {
    $staging = Join-Path $repo "build\server"
    Write-Host "== Build solo-servidor en $staging ==" -ForegroundColor Cyan
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    # Relay nativo (C++/winsock).
    & (Join-Path $PSScriptRoot "build-relay.ps1") -Configuration $Configuration

    # RSocServer (API/señalización) self-contained.
    dotnet publish (Join-Path $repo "src\RSocServer\RSocServer.csproj") `
        -c $Configuration -r $Runtime --self-contained true `
        -o (Join-Path $staging "RSocServer") --nologo
    if ($LASTEXITCODE -ne 0) { throw "Fallo publish RSocServer." }

    Copy-Item (Join-Path $repo "src\RSocRelay\bin\RSocRelay.exe") $staging -Force
    Copy-Item (Join-Path $PSScriptRoot "install-service.ps1")     $staging -Force

    # Artefactos de autoactualización (si hay cliente/APK de un build-dist previo).
    $cliDir = Join-Path $repo "build\client"
    $apkPath = Join-Path $repo "build\android\RSoc.apk"
    if (Test-Path $cliDir) {
        & (Join-Path $PSScriptRoot "pack-updates.ps1") `
            -ServerBase (Join-Path $staging "RSocServer") -ClientDir $cliDir -ApkPath $apkPath
    } else {
        Write-Warning "Sin build\client: el servidor irá SIN artefactos de autoactualización. Ejecuta build-dist.ps1 antes para incluirlos."
    }
}

# Verificación mínima de que el build trae lo que install-service.ps1 espera.
foreach ($must in @("RSocRelay.exe", "install-service.ps1", "RSocServer\RSocServer.exe")) {
    if (-not (Test-Path (Join-Path $staging $must))) { throw "El build no contiene '$must' en $staging." }
}

# --------------------------------------------------------------------------------------------
# 2) Sesión WinRM contra la VM.
# --------------------------------------------------------------------------------------------
if (-not $Credential) { $Credential = Get-Credential -UserName $User -Message "Credenciales admin de la VM $VmHost" }

if ($ConfigureTrustedHosts -and -not $UseSSL) {
    Write-Host "Añadiendo $VmHost a WSMan TrustedHosts (HTTP)…" -ForegroundColor Yellow
    $cur = (Get-Item WSMan:\localhost\Client\TrustedHosts).Value
    if ($cur -notmatch [regex]::Escape($VmHost)) {
        $new = if ([string]::IsNullOrWhiteSpace($cur)) { $VmHost } else { "$cur,$VmHost" }
        Set-Item WSMan:\localhost\Client\TrustedHosts -Value $new -Force
    }
}

$so = New-PSSessionOption -SkipCACheck -SkipCNCheck   # tolera certs autofirmados en -UseSSL
$connect = @{ ComputerName = $VmHost; Credential = $Credential; SessionOption = $so }
if ($UseSSL)    { $connect.UseSSL = $true }
if ($WinRmPort) { $connect.Port   = $WinRmPort }

$proto = if ($UseSSL) { "HTTPS" } else { "HTTP" }
if (-not $UseSSL) { Write-Warning "WinRM por HTTP: tráfico de despliegue sin cifrar. Usa -UseSSL sobre Internet." }
Write-Host "== Conectando a $VmHost ($proto) como $($Credential.UserName) ==" -ForegroundColor Cyan
$session = New-PSSession @connect

try {
    # ----------------------------------------------------------------------------------------
    # 3) Copia de artefactos a la VM.
    # ----------------------------------------------------------------------------------------
    Write-Host "== Preparando $RemoteDir en la VM ==" -ForegroundColor Cyan
    Invoke-Command -Session $session -ScriptBlock {
        param($dir)
        if (Test-Path $dir) {
            # Detiene servicios previos para liberar los .exe antes de sobrescribir.
            foreach ($svc in @("RSocServer", "RSocRelay")) {
                if (Get-Service $svc -ErrorAction SilentlyContinue) { sc.exe stop $svc | Out-Null }
            }
            Start-Sleep -Seconds 1
            Remove-Item (Join-Path $dir '*') -Recurse -Force -ErrorAction SilentlyContinue
        } else {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
    } -ArgumentList $RemoteDir

    Write-Host "== Copiando build a la VM ==" -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $staging '*') -Destination $RemoteDir -ToSession $session -Recurse -Force

    # Fija en rsoc-server-config.json el host PÚBLICO del relay y los puertos. Es lo que evita el
    # "conexión denegada" desde otras redes: el servidor debe anunciar una dirección alcanzable.
    Write-Host "== Configurando relay público $RelayHost`:$RelayPort en rsoc-server-config.json ==" -ForegroundColor Cyan
    Invoke-Command -Session $session -ScriptBlock {
        param($dir, $relayHost, $relayPort, $apiPort)
        $cfgPath = Join-Path $dir 'RSocServer\rsoc-server-config.json'
        if (-not (Test-Path $cfgPath)) { throw "No existe $cfgPath en la VM." }
        $j = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $j.Relay.Host = $relayHost
        $j.Relay.Port = $relayPort
        $j.Server.ApiPort = $apiPort
        ($j | ConvertTo-Json -Depth 10) | Set-Content $cfgPath -Encoding UTF8
        Write-Host "  Relay.Host=$relayHost Relay.Port=$relayPort Server.ApiPort=$apiPort"
    } -ArgumentList $RemoteDir, $RelayHost, $RelayPort, $ApiPort

    # ----------------------------------------------------------------------------------------
    # 4) Instalación de servicios + firewall en la VM (reutiliza install-service.ps1).
    # ----------------------------------------------------------------------------------------
    Write-Host "== Instalando servicios en la VM (relay $RelayPort, API $ApiPort) ==" -ForegroundColor Cyan
    Invoke-Command -Session $session -ScriptBlock {
        param($dir, $rp, $ap)
        Set-ExecutionPolicy -Scope Process Bypass -Force

        # La sesión remota debe estar elevada; si no, install-service.ps1 intentaría auto-elevarse
        # con RunAs (imposible aquí) y fallaría en silencio. Lo comprobamos y avisamos claro.
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
        if (-not $admin) {
            throw "La sesión WinRM no tiene token elevado. Conecta con cuenta admin; para cuentas " +
                  "LOCALES define LocalAccountTokenFilterPolicy=1 en la VM y reintenta."
        }

        & (Join-Path $dir 'install-service.ps1') -RelayPort $rp -ApiPort $ap

        # Estado final de los servicios.
        Get-Service RSocRelay, RSocServer | Format-Table Name, Status, StartType -AutoSize | Out-String
    } -ArgumentList $RemoteDir, $RelayPort, $ApiPort | Write-Host
}
finally {
    if ($session) { Remove-PSSession $session }
}

Write-Host "`n== Despliegue completado ==" -ForegroundColor Green
Write-Host "  VM:    $VmHost" -ForegroundColor Green
Write-Host "  API:   http://$VmHost`:$ApiPort   (TCP $ApiPort)" -ForegroundColor Green
Write-Host "  Relay: $VmHost`:$RelayPort         (TCP $RelayPort)" -ForegroundColor Green
Write-Host "  Recuerda abrir $ApiPort y $RelayPort en la Security List / NSG de OCI (ingress)." -ForegroundColor Yellow
Write-Host "  Edita $RemoteDir\RSocServer\rsoc-server-config.json en la VM y reinicia servicios si cambias IPs/credenciales." -ForegroundColor Yellow
