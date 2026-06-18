# RSoc — Especificación y Requisitos

> Sistema de acceso/control remoto **self-hosted**, desarrollado **desde cero**.
> Documento maestro de especificación, arquitectura y requisitos del proyecto.
>
> **Proyecto nuevo e independiente.** No es un fork ni deriva de ningún producto
> existente. Todo el código es de desarrollo propio. **Licencia: MIT.**

---

## 1. Objetivo

Sistema de acceso y control remoto tipo AnyDesk/TeamViewer, **self-hosted y gratuito**,
construido **íntegramente como desarrollo nuevo** (captura, códecs, transporte, señalización
y relay propios). Sin reutilizar, vendorizar ni forkear código de terceros con licencia
copyleft.

- **Clientes objetivo:** Windows y Android.
- **Servidor:** propio, dos componentes — **RSocServer** (señalización/registro) y
  **RSocRelay** (reenvío de tráfico). Desplegable en LAN o en host público.
- **Coste objetivo:** 0 € (infraestructura propia / host gratuito para producción).

---

## 2. Stack tecnológico

| Capa | Tecnología |
|------|-----------|
| RSocServer (señalización + API + panel) | **C# / ASP.NET Core** |
| RSocRelay (reenvío de tráfico) | **C++** |
| Núcleo del cliente (captura, códec, input, transporte) | **C++** |
| UI cliente Windows | **C# / .NET** (WinUI/WPF) sobre núcleo nativo C++ |
| Cliente Android | **C# / .NET for Android** + interop nativo **C++** (NDK) |
| Empaquetado e instalación | **PowerShell** |

**Lenguajes del proyecto:** **C++**, **C#** y **ASP.NET**. **Nada de Rust, Dart ni Flutter.**

**Política de dependencias (clave para la licencia MIT):**
- Todo el código de primera mano es **nuevo** y se publica bajo **MIT**.
- No se forkea, vendoriza ni reutiliza código de terceros con licencia **AGPL/GPL**.
- Solo se admiten dependencias de terceros con licencia **permisiva** (MIT/BSD/Apache-2.0)
  o **APIs del sistema operativo** (p.ej. Media Foundation/MediaCodec para códec por
  hardware, DXGI/MediaProjection para captura). El uso de APIs del SO no contamina la
  licencia del proyecto.

> **Nota honesta de alcance:** construir captura + códec + transporte + NAT desde cero es
> un proyecto grande (orden de magnitud de varios meses-año). Se mitiga apoyándose en
> **APIs del SO** para captura y códec por hardware (permisivas/propias del SO), en lugar
> de reimplementar compresión de vídeo. Ver sección 5.

---

## 3. Arquitectura

**Relay puro. Sin P2P.** Todo el tráfico de sesión va A ↔ RSocRelay ↔ B. No se intenta
hole-punching ni conexión directa: el cliente A se conecta al relay, el cliente B se conecta
al relay, y el relay reenvía. Esto simplifica drásticamente la red (un único punto de
entrada con IP/puerto conocidos) a cambio de que el 100 % del streaming pase por el servidor.

- **RSocServer (C#/ASP.NET Core):**
  - Registro de dispositivos e IDs, autenticación.
  - Señalización: empareja al que solicita control con el dispositivo destino y les entrega
    un *token de sesión* y la dirección del relay.
  - **Address book / lista de dispositivos** (online) y API de administración.
  - **Panel web** de monitorización (ASP.NET, mismo proceso o servicio aparte).
  - Persistencia en base de datos propia (p.ej. SQLite/SQL Server según despliegue).
- **RSocRelay (C++):**
  - Acepta conexiones de los dos extremos de una sesión, las valida con el token emitido por
    RSocServer y **reenvía bytes** entre ambos (full-duplex), sin interpretar el contenido.
  - Diseñado para alto throughput (streaming de vídeo). Sin estado de negocio.
- **Transporte:** TCP con **TLS** extremo-relay (y opcionalmente WebSocket sobre TLS para
  atravesar proxys). El cifrado de la *sesión* (contenido) es extremo a extremo entre los dos
  clientes; el relay solo ve bytes cifrados.

**Flujo de conexión (resumen):**
1. Cada cliente se registra en RSocServer (ID + clave + heartbeat).
2. A pide controlar a B → RSocServer autoriza, crea sesión, devuelve `relay-addr` + `token`.
3. A y B abren conexión TLS a RSocRelay presentando el `token` → el relay los empareja.
4. A y B negocian clave de sesión (extremo a extremo) y arranca el streaming por el relay.

---

## 4. Componentes y ubicaciones (objetivo del nuevo proyecto)

```
c:\ID\OneDrive\RemoteSoc\                ← raíz del proyecto (docs + futuro código nuevo)
  ├─ ESPECIFICACION-RSoc.md              (este documento)
  ├─ PROYECTO-acceso-remoto.md           (brief / decisiones de diseño)
  ├─ LICENSE                             (MIT)
  ├─ server\                             RSocServer (C#/ASP.NET) + RSocRelay (C++)
  │   ├─ RSocServer\                     ASP.NET Core: API, señalización, panel
  │   └─ RSocRelay\                      C++: relay de tráfico
  ├─ client-windows\                     cliente Windows (núcleo C++ + UI C#/.NET)
  ├─ client-android\                     cliente Android (.NET for Android + C++ NDK)
  └─ packaging\                          scripts PowerShell de build/empaquetado/instalación
```

> El árbol anterior describe el **objetivo** del proyecto nuevo. Cualquier código de la
> implementación anterior queda **fuera de alcance** y será retirado en un paso aparte; no se
> reutiliza nada de él.

---

## 5. Núcleo de medios (desarrollo propio + APIs del SO)

- **Captura de pantalla:**
  - Windows: **DXGI Desktop Duplication** (API del SO).
  - Android: **MediaProjection** (API del SO).
- **Códec de vídeo:** códec **por hardware vía API del SO** — **Media Foundation (H.264/H.265)**
  en Windows, **MediaCodec** en Android. No se reimplementa un códec ni se enlaza librería
  copyleft.
- **Entrada (input):**
  - Windows: `SendInput` / inyección de teclado y ratón (API del SO).
  - Android: **AccessibilityService** (única vía soportada por Android para inyectar input;
    es una limitación del propio Android, no del diseño).
- **Transporte y protocolo:** **propio**, definido en este proyecto (framing, control de
  sesión, sincronización audio/vídeo, reconexión). C++ en el núcleo, compartido entre
  Windows y Android (NDK).
- **Cifrado:** TLS para el canal cliente↔relay; clave de sesión extremo a extremo entre los
  dos clientes (p.ej. intercambio X25519 + AEAD), usando primitivas de librerías permisivas
  o del SO.

---

## 6. Lógica "equipo" (lista de dispositivos + desatendido)

- **Lista de dispositivos** propia, servida por RSocServer (`/api/devices`, online).
- **Control desatendido:** cada dispositivo registra una *connection-password* permanente;
  desde la lista se conecta sin intervención del lado remoto.
- **Dos credenciales:** usuario/contraseña de API (login → ver lista) y la
  *connection-password* del dispositivo (control desatendido).
- **Autoconfiguración del cliente** vía fichero de config (`server`, `relay`, `key`,
  `api-user`, `api-password`, `connection-password`, `device-alias`). El mismo binario sirve
  para LAN o Internet cambiando solo el JSON; el usuario final no configura nada.
- **Keep-alive:** RSocServer marca offline / elimina los dispositivos sin heartbeat durante
  > N minutos; los clientes re-descargan la lista periódicamente.

---

## 7. Marca

- Nombre de producto: **RSoc**. Aplicado a UI, carpeta de config, bandeja, servicios.
- Binarios/servicios del servidor: **RSocServer**, **RSocRelay**.
- Cliente: **RSoc** (Windows y Android) con identidad visual común (icono/logo propios).

---

## 8. Build y empaquetado (PowerShell)

- **RSocServer (C#/ASP.NET):** `dotnet build` / `dotnet publish` orquestado por scripts
  PowerShell. Salida autocontenida (publish self-contained) para no depender del runtime en
  el servidor.
- **RSocRelay (C++):** compilación nativa (MSVC/CMake) orquestada por PowerShell.
- **Cliente Windows:** núcleo C++ (CMake/MSVC) + UI .NET (`dotnet publish`); empaquetado a
  instalador/ZIP por PowerShell.
- **Cliente Android:** `dotnet build` (.NET for Android) + librerías nativas C++ (NDK);
  empaquetado del APK por PowerShell.
- **Instalación del servidor:** script PowerShell que registra **RSocServer** y **RSocRelay**
  como servicios de Windows (o unidades systemd en Linux), abre el firewall y aplica la
  configuración.

---

## 9. Despliegue del servidor

1. Publicar `server\` (RSocServer + RSocRelay) en la máquina servidor.
2. Ejecutar el script PowerShell de instalación (se auto-eleva): instala los servicios
   **RSocServer** y **RSocRelay**, abre firewall y aplica configuración.
3. Configuración de RSocServer: puertos, usuarios de API, `connection-password`,
   dirección pública del relay, política de keep-alive.
4. La clave pública del servidor debe coincidir con la `key` de la config de los clientes.
5. Para acceso remoto robusto, el servidor (relay) debe tener **IP pública real alcanzable**.

**Puertos:** a definir en la implementación; un único puerto TLS de relay + el puerto del API
del servidor (ambos abiertos/reenviados). Al ser relay puro no hay rango de hole-punching.

---

## 10. Requisitos

### Funcionales
- RF1. Registro de dispositivos y lista compartida (online).
- RF2. Control remoto desatendido (connection-password) desde la lista.
- RF3. Cliente Windows y Android con la misma identidad RSoc.
- RF4. Configuración por fichero (el usuario final no configura nada).
- RF5. Limpieza automática de dispositivos inactivos (keep-alive).
- RF6. Panel web de monitorización (ASP.NET).

### No funcionales
- RNF1. **Licencia MIT**; todo código nuevo, sin dependencias AGPL/GPL.
- RNF2. Stack **C++ / C# / ASP.NET**; sin Rust, Dart ni Flutter.
- RNF3. **Relay puro** (sin P2P): todo el tráfico por RSocRelay.
- RNF4. Coste 0 €.
- RNF5. Despliegue como servicios (arranque automático), empaquetado con PowerShell.

### Red
- **Relay puro:** el 100 % del streaming pasa por RSocRelay → el servidor necesita ancho de
  banda de salida (egress) suficiente y, para acceso desde Internet, **IP pública real**.
- Sin hole-punching ni dependencia del tipo de NAT del cliente: solo necesita alcanzar el
  relay.

---

## 11. Estado actual / pendientes

- ⏳ **Proyecto nuevo: implementación por empezar.** Esta especificación define el objetivo.

**FASE 1 — Servidores + Cliente Windows completo + test automatizado:**
- ⏳ Definir protocolo de transporte propio (framing, sesión, sync A/V, reconexión).
- ⏳ RSocServer (ASP.NET): registro, señalización, API, panel.
- ⏳ RSocRelay (C++): reenvío con validación de token.
- ⏳ Cliente Windows completo (núcleo C++ + UI .NET): captura DXGI + códec MF + input +
  transporte + conexión desde la lista.
- ⏳ Test automatizado de extremo a extremo (señalización + relay + sesión), sin intervención.
- ⏳ Scripts PowerShell de build/empaquetado/instalación.

**FASE 2 — Cliente Android:**
- ⏳ Cliente Android (.NET for Android + C++ NDK): MediaProjection + MediaCodec +
  AccessibilityService, mismo protocolo y servidores.
- ⏳ Despliegue del servidor en host con IP pública real (sin Docker)

> La implementación anterior ya fue retirada; respaldo en `RustRSoc-backup.zip`. Nada se
> reutiliza de ella.

---

## 12. Notas

- Idioma de trabajo: castellano.
- No usar Docker.
- Documento de decisiones de diseño: `PROYECTO-acceso-remoto.md`.
