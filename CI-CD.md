# CI/CD: compilar y desplegar RSoc en un servidor Windows

Guía para automatizar **build + despliegue** del servidor RSoc (RSocServer + RSocRelay) en una
VM/servidor **Windows** (p. ej. Oracle Cloud OCI), reutilizando los scripts del repo
(`build-dist.ps1`, `deploy-oracle.ps1`). El despliegue usa **PowerShell Remoting (WinRM)**.

El flujo es siempre el mismo, lo orqueste quien lo orqueste:

```
checkout → instalar toolchain → build-dist.ps1 → deploy-oracle.ps1 (WinRM) → verificar
```

## 0. Qué necesita el runner de build

El build toca tres toolchains, así que el agente que compila debe tener:

- **.NET SDK 10** (`dotnet`).
- **MSVC / Visual Studio Build Tools 2022** (para `cl.exe` vía `vcvars64`: relay y núcleo nativo).
- **Carga de trabajo Android de .NET** (`dotnet workload install android`) para el APK.

Un runner **`windows-latest` de GitHub** ya trae Visual Studio 2022 y .NET; solo hay que añadir la
carga Android. Si usas un **runner self-hosted**, instala las tres una vez.

> Si NO necesitas regenerar el APK de Android en cada despliegue del servidor, puedes saltarte la
> carga Android y compilar solo el servidor (más rápido). Ver la nota al final.

## 1. Preparar el servidor Windows (destino), una sola vez

En la VM/servidor que recibirá el despliegue:

```powershell
# Habilitar WinRM (PowerShell Remoting)
Enable-PSRemoting -Force

# Para cuentas LOCALES sobre WinRM, UAC filtra el token: necesario para crear servicios
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
  -Name LocalAccountTokenFilterPolicy -Value 1 -PropertyType DWord -Force

# (Recomendado) WinRM sobre HTTPS para que las credenciales no viajen en claro por Internet
$c = New-SelfSignedCertificate -DnsName "<IP-o-DNS-del-servidor>" -CertStoreLocation Cert:\LocalMachine\My
winrm create winrm/config/Listener?Address=*+Transport=HTTPS `
  "@{Hostname=`"<IP-o-DNS-del-servidor>`"; CertificateThumbprint=`"$($c.Thumbprint)`"}"
New-NetFirewallRule -DisplayName "WinRM HTTPS" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5986
```

Abre además en el firewall / **Security List o NSG de OCI** (ingress):

| Puerto | Uso |
|-------|-----|
| 5985 / 5986 | WinRM (HTTP / HTTPS) — para que el runner despliegue |
| 21114 | API RSocServer (**HTTPS**) |
| 21117 | Relay RSocRelay (TCP, TLS extremo a extremo) |

`install-service.ps1` (lo ejecuta el deploy) abre 21114 y 21117 en el firewall local del servidor;
los puertos de WinRM y la regla de OCI los abres tú.

## 2. Secretos del pipeline

Define como **secretos** del CI (nunca en el YAML):

| Secreto | Valor |
|--------|-------|
| `VM_HOST` | IP pública o DNS del servidor Windows |
| `VM_USER` | usuario administrador (p. ej. `opc` o `Administrator`) |
| `VM_PASSWORD` | contraseña de ese usuario |

## 3. GitHub Actions (recomendado)

`.github/workflows/deploy.yml`:

```yaml
name: build-and-deploy
on:
  push:
    branches: [ main ]          # despliega al hacer push a main
  workflow_dispatch:            # ...o manualmente

jobs:
  deploy:
    runs-on: windows-latest     # trae VS2022 (MSVC) y .NET; añadimos Android abajo
    steps:
      - uses: actions/checkout@v4

      - name: Instalar .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Instalar carga Android (para el APK)
        run: dotnet workload install android

      - name: Build completo (sube versión AAAA.MM.DD.N y empaqueta updates\)
        shell: pwsh
        run: .\packaging\build-dist.ps1

      - name: Desplegar al servidor Windows por WinRM
        shell: pwsh
        env:
          VM_HOST: ${{ secrets.VM_HOST }}
          VM_USER: ${{ secrets.VM_USER }}
          VM_PASSWORD: ${{ secrets.VM_PASSWORD }}
        run: |
          $sec  = ConvertTo-SecureString $env:VM_PASSWORD -AsPlainText -Force
          $cred = [System.Management.Automation.PSCredential]::new($env:VM_USER, $sec)
          .\packaging\deploy-oracle.ps1 `
            -VmHost $env:VM_HOST -Credential $cred `
            -SkipBuild `                 # reusa el build\ recién generado
            -UseSSL -ConfigureTrustedHosts

      - name: Publicar artefactos del build
        uses: actions/upload-artifact@v4
        with:
          name: rsoc-${{ github.run_number }}
          path: build/
```

Notas:

- `build-dist.ps1` **incrementa la versión** `AAAA.MM.DD.N` en cada ejecución (constante única
  `AppVersion.Current` + `versionCode` de Android) y deja en `build\server\RSocServer\updates\` el
  zip del cliente Windows y el APK con su `version.txt` y SHA-256. Al desplegarlos, los clientes
  con versión anterior se **autoactualizan** escalonadamente.
- `deploy-oracle.ps1 -SkipBuild` copia el servidor a la VM, fija `Relay.Host` al host público, y
  ejecuta `install-service.ps1` (instala/arranca servicios y abre el firewall). Pasa
  `-RelayHost <dns/ip>` si el relay se anuncia con un nombre distinto del de WinRM.
- `-UseSSL` usa WinRM sobre HTTPS (5986). Quítalo solo en una LAN de confianza (HTTP 5985).
- El runner debe **alcanzar** el WinRM del servidor (IP pública + reglas de OCI), o usa un
  **runner self-hosted** dentro de la misma red (ver §5).

## 4. Azure DevOps (alternativa)

`azure-pipelines.yml`:

```yaml
trigger: [ main ]
pool: { vmImage: 'windows-latest' }
steps:
  - task: UseDotNet@2
    inputs: { packageType: sdk, version: '10.0.x' }
  - powershell: dotnet workload install android
    displayName: Carga Android
  - powershell: .\packaging\build-dist.ps1
    displayName: Build completo
  - powershell: |
      $sec  = ConvertTo-SecureString "$(VM_PASSWORD)" -AsPlainText -Force
      $cred = [System.Management.Automation.PSCredential]::new("$(VM_USER)", $sec)
      .\packaging\deploy-oracle.ps1 -VmHost "$(VM_HOST)" -Credential $cred -SkipBuild -UseSSL -ConfigureTrustedHosts
    displayName: Deploy WinRM
    env: { VM_PASSWORD: $(VM_PASSWORD) }   # secreto del pipeline
```

Define `VM_HOST`, `VM_USER`, `VM_PASSWORD` como **variables secretas** del pipeline.

## 5. Runner self-hosted (si el servidor no es accesible desde Internet)

Si el servidor Windows está en una red privada (sin WinRM expuesto a Internet), instala un
**runner self-hosted** de GitHub/Azure en una máquina Windows de esa red:

1. En esa máquina instala .NET 10, VS Build Tools 2022 y la carga Android.
2. Registra el runner (Settings → Actions → Runners → New self-hosted runner) y lánzalo como servicio.
3. Cambia `runs-on: windows-latest` por `runs-on: [self-hosted, windows]`.
4. Como está en la LAN, puedes usar WinRM por HTTP (quita `-UseSSL`) si confías en la red.

## 6. Verificación post-despliegue (opcional)

Añade un paso que compruebe que el servidor responde por HTTPS y sirve la última versión:

```yaml
      - name: Verificar
        shell: pwsh
        run: |
          $v = Invoke-RestMethod -SkipCertificateCheck `
            "https://${{ secrets.VM_HOST }}:21114/api/update/check?platform=windows&version=0.0.0"
          Write-Host "Servidor sirviendo versión $($v.latestVersion)"
          if (-not $v.updateAvailable) { throw "El servidor no anuncia actualización" }
```

`-SkipCertificateCheck` es necesario porque la API usa **certificado autofirmado**.

## 7. Despliegue solo-servidor (más rápido, sin Android)

Si no quieres recompilar el APK en cada despliegue del servidor, deja que `deploy-oracle.ps1`
haga su propio build de **solo servidor** (relay + RSocServer) y omite `build-dist.ps1` y la carga
Android:

```powershell
.\packaging\deploy-oracle.ps1 -VmHost $env:VM_HOST -Credential $cred -UseSSL -ConfigureTrustedHosts
```

En ese modo, si existe un `build\client` / `build\android` previo, también empaqueta los artefactos
de autoactualización; si no, despliega el servidor **sin** capacidad de autoactualización (avisa por
consola). Para autoactualización completa, ejecuta `build-dist.ps1` antes y usa `-SkipBuild`.
