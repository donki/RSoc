# RSoc

Sistema de **acceso y control remoto self-hosted**, desarrollado **desde cero**. Proyecto nuevo
e independiente — no es un fork ni reutiliza código de terceros copyleft. **Licencia [MIT](LICENSE).**

- **Stack:** C++, C# y ASP.NET Core. Sin Rust, Dart ni Flutter.
- **Arquitectura:** relay puro (sin P2P). Todo el tráfico de sesión va `A ↔ RSocRelay ↔ B`.
- **Clientes:** Windows (escritorio) y Android.
- **Autoalojado:** tú controlas el servidor; no depende de ninguna nube de terceros.

> **Uso responsable.** Herramienta de acceso remoto pensada para soporte y administración de
> equipos **propios o con autorización explícita** del usuario. No la uses para acceder a equipos
> sin permiso: puede ser ilegal en tu jurisdicción.

> **Cifrado.** Todo el tráfico va cifrado: la **API siempre por HTTPS** (TLS) y la **sesión sobre
> el relay por TLS extremo a extremo** entre los dos clientes (el agente presenta un certificado
> autofirmado y el controlador lo acepta), de modo que **ni el relay ve el contenido**. Los
> certificados son **autofirmados** (se generan solos en servidor y agente); los clientes los
> aceptan. Lo único en claro es el handshake de emparejado del relay (22 bytes: magic + token
> aleatorio de un solo uso), que no contiene datos sensibles.

## Arquitectura

```
   Windows / Android (controlador)                 Windows (agente, controlado)
            │  1. login + lista + crear sesión               ▲
            ▼                                                 │ 3. sondea sesión pendiente
     ┌──────────────┐  HTTPS/JSON (plano de control)   ┌──────────────┐
     │  RSocServer  │◄────────────────────────────────►│   (agente)   │
     └──────┬───────┘                                  └──────┬───────┘
            │ 2. emite token de relay                         │
            ▼                                                 ▼
     ┌────────────────────  TCP + TLS E2E (plano de datos)  ────────────────┐
     │           RSocRelay (empareja por token; reenvía TLS opaco)          │
     └──────────────────────────────────────────────────────────────────────┘
```

## Estructura

```
src/
  RSoc.Protocol/   Modelos del API, handshake del relay y versión (C#, compartido)
  RSocServer/      Señalización + API REST + lista + autoactualización (ASP.NET Core)
  RSocRelay/       Reenviador de tráfico emparejado por token (C++ / Winsock)
client-windows/
  RSocClientCore/  Núcleo nativo: captura DXGI + códec WIC (MJPEG) + input (C++)
  RSoc.Client/     Cliente del plano de control y de sesión (C#, compartido)
  RSoc.WindowsApp/ UI de escritorio (WinForms): agente + controlador + visor
client-android/
  RSoc.Android/    Cliente Android (controlador + visor táctil)
tests/
  RSoc.Tests/      Test E2E (señalización + relay + flujo de bytes)
packaging/         Scripts PowerShell de build, empaquetado, instalación y despliegue
```

## Componentes del servidor

| Componente | Lenguaje | Función |
|-----------|----------|---------|
| **RSocServer** | C# / ASP.NET Core | Registro de dispositivos, login del API, lista online, señalización de sesión (emite token de relay), distribución de actualizaciones y logging rotativo. |
| **RSocRelay** | C++ (Winsock) | Empareja las dos conexiones con el mismo token y reenvía bytes en ambos sentidos. Sin estado de negocio. Logging por par. |

## Requisitos de build

- **.NET SDK 10** (`dotnet`).
- **Visual Studio Build Tools 2022** (MSVC `cl.exe`, vía `vcvars64`) para los módulos C++.
- **Carga de trabajo Android de .NET** (`dotnet workload install android`) para el APK.

## Build

```powershell
# Relay nativo (C++)
.\packaging\build-relay.ps1

# Núcleo nativo del cliente Windows (C++)
.\packaging\build-core.ps1

# Build completo de distribución: servidores + cliente Windows + APK + artefactos de update
.\packaging\build-dist.ps1
# -> build\server\   (RSocRelay.exe + RSocServer\ + install-service.ps1 + updates\)
#    build\client\   (RSoc.WindowsApp.exe + RSocClientCore.dll + rsoc-client-conf.json)
#    build\android\  (RSoc.apk)
```

## Test

```powershell
dotnet test
```

El test de extremo a extremo arranca RSocServer en proceso y el RSocRelay nativo real, simula un
agente y un controlador, y comprueba que se emparejan por token y que los bytes fluyen en ambos
sentidos.

## Instalación del servidor

```powershell
# Desde build\server\ , como administrador:
.\install-service.ps1   # instala RSocRelay y RSocServer como servicios y abre el firewall
```

Edita `RSocServer\rsoc-server-config.json` (IPs, puertos, credenciales) y reinicia los servicios.
**Importante:** `Relay.Host` debe ser una dirección **alcanzable por los clientes** (IP pública o
DNS del servidor); si pones una IP de LAN privada, los equipos de otra red no podrán conectarse.

## Despliegue en VM Windows (Oracle Cloud / OCI)

```powershell
# Build completo primero (incluye artefactos de autoactualización):
.\packaging\build-dist.ps1

# Despliegue por WinRM a la VM (te pedirá credenciales admin):
.\packaging\deploy-oracle.ps1 -VmHost <IP-pública> -User <admin> -SkipBuild
```

`deploy-oracle.ps1` copia el servidor a la VM, fija `Relay.Host` al host público, instala los
servicios y abre el firewall local. Recuerda abrir en la **Security List / NSG de OCI** los
puertos API (21114), relay (21117) y WinRM (5985/5986).

## Autoactualización del cliente

El servidor hospeda el cliente en `updates/windows/` (zip) y `updates/android/` (APK), junto con
su `version.txt`. Al arrancar, cada cliente consulta `GET /api/update/check`; si la versión del
servidor es más nueva, descarga el artefacto, **verifica su SHA-256** y se reinstala (Windows:
swap + reinicio preservando la config; Android: instalador del sistema, el usuario confirma).

**Rollout escalonado:** para no saturar al servidor no se actualizan todos a la vez. El servidor
limita las descargas concurrentes (`Update.MaxConcurrentDownloads`); si no hay ranura, responde
"reintenta luego" y el cliente espera con *jitter* por dispositivo. Configurable en
`rsoc-server-config.json`.

**Versionado:** formato fecha **`AAAA.MM.DD.N`** (p.ej. `2026.06.18.1`). El número de build `N`
se **incrementa automáticamente en cada `build-dist.ps1`** ([`bump-version.ps1`](packaging/bump-version.ps1)
actualiza la constante única `AppVersion.Current` y el `versionCode` de Android). Usa
`build-dist.ps1 -NoVersionBump` para reusar la versión actual sin subirla.

**Publicar una versión nueva:**

1. `.\packaging\build-dist.ps1` — sube la versión y regenera `updates\` con su hash.
2. Despliega (`deploy-oracle.ps1`) o copia `build\server\RSocServer\updates\` a la carpeta del
   servidor. Los clientes con versión anterior se actualizarán solos, escalonadamente.

## Ficheros de configuración

| Fichero | Dónde | Qué |
|--------|-------|-----|
| `rsoc-server-config.json` | junto a `RSocServer.exe` | API (usuario/clave), relay (host/puerto público), puertos, timeouts, política de actualización. |
| `rsoc-client-conf.json`   | junto a `RSoc.WindowsApp.exe` | Servidor, credenciales del API, contraseña de conexión, alias, arranque con Windows. |

## Protocolo (resumen)

- **Plano de control** (cliente ↔ RSocServer): **HTTPS**/JSON (cert autofirmado).
  `login` · `devices/register` · `devices/{id}/heartbeat` · `devices` · `sessions` ·
  `sessions/pending/{id}` · `update/check` · `update/download`.
- **Plano de datos** (cliente ↔ RSocRelay): TCP. Handshake de 22 bytes
  (`"RSOC"` + versión + rol + token de 16 bytes) para el emparejado y, a partir de ahí, un
  túnel **TLS extremo a extremo** entre los dos clientes (el relay reenvía bytes TLS opacos) que
  transporta los mensajes de sesión (vídeo, input, portapapeles, ficheros, multimonitor, calidad).

## Terceros y licencias

Todo el código de RSoc es original y se publica bajo **MIT**. No incorpora código de terceros
copyleft (GPL/AGPL) ni de otros proyectos de acceso remoto. Dependencias:

- **.NET 10 / ASP.NET Core** (MIT, Microsoft) — runtime y framework.
- **msquic** (MIT, Microsoft) — incluido por el runtime .NET self-contained.
- **Carga de trabajo Android de .NET / AndroidX** (MIT/Apache-2.0) — solo el cliente Android.
- **APIs de Windows** (Desktop Duplication/DXGI, Direct3D 11, Windows Imaging Component, Winsock)
  vía el SDK de Windows — componentes del sistema, sin redistribución de binarios de terceros.

Ver [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) para el detalle.

## Documentación

[ESPECIFICACION-RSoc.md](ESPECIFICACION-RSoc.md) · [PROYECTO-acceso-remoto.md](PROYECTO-acceso-remoto.md) · [FEATURES.md](FEATURES.md) · [CI-CD.md](CI-CD.md) · [CHANGELOG.md](CHANGELOG.md) · [Constitución](constitution/constitucion.md)

## Créditos

Creado por **sOCratic**. Diseñado y desarrollado **íntegramente con [Claude](https://www.anthropic.com/claude)**
(Claude Code, de Anthropic). El código se publica bajo licencia [MIT](LICENSE); su titular conserva
todos los derechos que esta otorga.
