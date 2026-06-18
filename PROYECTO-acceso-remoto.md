# RSoc — Sistema de acceso remoto self-hosted — Brief de diseño

> Documento de contexto y decisiones de diseño. Resumen de las decisiones firmes del
> proyecto. Léelo entero antes de ejecutar nada.
>
> **Proyecto nuevo e independiente. Todo desarrollo propio. Licencia MIT.**

---

## 1. Objetivo

Construir un sistema de acceso remoto tipo AnyDesk/TeamViewer, **self-hosted y gratuito**,
**desde cero**. Clientes objetivo: **Windows y Android**.

---

## 2. Decisión de fondo

- El sistema se desarrolla **íntegramente como código nuevo** (captura, códecs, transporte,
  señalización y relay propios). **No se forkea, vendoriza ni reutiliza** ningún producto
  existente.
- Motivación de licencia: se quiere una **licencia MIT** limpia. Reutilizar o forkear código
  copyleft obligaría a mantener AGPL/GPL — descartado. Por eso: nada de vendorizar ni
  reutilizar; todo de primera mano.
- Dependencias de terceros: solo **permisivas** (MIT/BSD/Apache) o **APIs del SO**.

---

## 3. Arquitectura

- **Relay puro:** A se conecta al servidor, B se conecta al servidor, y **todo** el tráfico
  va A ↔ Relay ↔ B. **Sin P2P** (no hay hole-punching ni conexión directa).
- **Servidor (dos componentes):**
  - **RSocServer** — registro de IDs / señalización / API / panel. **C# / ASP.NET Core.**
  - **RSocRelay** — reenvío de tráfico de sesión. **C++** (alto throughput).
- **Servidor en producción:** host con **IP pública real** (tramo gratuito de algún
  proveedor). **SIN Docker.**
- **Servidor para la prueba inicial:** ejecutado en **Windows local**.

---

## 4. Restricciones y decisiones firmes

- **Licencia MIT.** Todo código nuevo; sin dependencias AGPL/GPL.
- **Stack:** **C++**, **C#** y **ASP.NET**. **Nada de Rust, Dart ni Flutter.**
- **Sin Docker** en ninguna fase. Binarios/servicios nativos.
- **Relay puro:** forzado por diseño; no existe ruta P2P.
- **Empaquetado e instalación con PowerShell.**
- **Servidor, relay y clave preconfigurados** en los clientes (el usuario final no configura
  nada).
- Coste objetivo: **0 €**.

---

## 5. Componentes

### 5.1 Servidor

- **RSocServer (C#/ASP.NET Core):** registro de dispositivos/IDs, autenticación,
  señalización (emparejar solicitante y destino, emitir token de sesión + dirección de
  relay), address book/lista de dispositivos, API de administración y **panel web de
  monitorización** (ASP.NET). Persistencia propia (SQLite/SQL Server según despliegue).
- **RSocRelay (C++):** acepta los dos extremos de cada sesión, valida el token y **reenvía
  bytes** full-duplex sin interpretarlos. Diseñado para streaming de vídeo.
- **Prueba inicial:** ejecutar en Windows local.
- **Producción:** host con IP pública real, sin Docker.

### 5.2 Clientes

- **Cliente Windows:** núcleo **C++** (captura DXGI, códec por Media Foundation, inyección de
  input, transporte TLS propio) + UI **C#/.NET** (WinUI/WPF).
- **Cliente Android:** **.NET for Android (C#)** + interop nativo **C++ (NDK)**: captura por
  **MediaProjection**, códec por **MediaCodec**, inyección de input por
  **AccessibilityService** (única vía soportada por Android — limitación de la plataforma).
- **Preconfiguración:** servidor/relay/clave fijados por fichero de config; el usuario final
  no configura nada.
- Branding propio: nombre **RSoc**, logo e icono propios.

### 5.3 Panel de monitorización

- Parte de **RSocServer** (ASP.NET): dispositivos registrados, última actividad, estado de
  servicios y logs. Al ser servidor propio, el panel puede exponer gestión real (no solo
  lectura), según se implemente.

---

## 6. Plan por fases

1. **FASE 1 — Servidores + Cliente Windows completo + test automatizado:**
   - Definir el **protocolo de transporte propio** (framing, sesión, sync A/V, reconexión).
   - Implementar los **servidores**: **RSocServer** (ASP.NET) y **RSocRelay** (C++).
   - Implementar el **cliente Windows completo** (núcleo C++ + UI .NET): captura, códec,
     input, transporte y conexión desde/hacia la lista de dispositivos.
   - **Test automatizado** de extremo a extremo (señalización + relay + sesión), ejecutable
     sin intervención manual.
2. **FASE 2 — Cliente Android:**
   - Implementar el **cliente Android** (.NET for Android + C++ NDK): MediaProjection,
     MediaCodec, AccessibilityService, mismo protocolo y servidores que Windows.
   - Despliegue del servidor en host con **IP pública real** (sin Docker) para pruebas reales.

---

## 7. Requisitos de build (referencia)

- **RSocServer (C#/ASP.NET):** SDK .NET; `dotnet publish` self-contained. Orquestado por
  PowerShell.
- **RSocRelay y núcleo cliente (C++):** MSVC + CMake (Windows), NDK (Android).
- **UI Windows (.NET):** SDK .NET (WinUI/WPF).
- **Cliente Android (.NET for Android):** SDK .NET + workload Android + NDK.
- **Empaquetado/instalación:** scripts **PowerShell**.

---

## 8. Fuera de alcance / descartado

- **Reutilizar/forkear/vendorizar** cualquier producto existente: descartado (motivo de
  licencia — se quiere MIT, no AGPL).
- **Rust, Dart, Flutter:** descartados.
- **P2P / hole-punching:** descartado (arquitectura de relay puro).
- **Docker:** descartado.

---

## 9. Riesgos / avisos honestos

- **Relay puro = 100 % del streaming por el servidor** (egress). Requiere ancho de banda de
  salida suficiente y, para Internet, IP pública real.
- **Construir el núcleo de medios desde cero es un proyecto grande.** Se mitiga apoyándose en
  **APIs del SO** para captura y códec por hardware (permisivas/propias del SO), en vez de
  reimplementar compresión de vídeo.
- **El servidor en Windows es solo para la prueba**; el definitivo va en un host con IP
  pública real.
- **Control de Android** = AccessibilityService (limitación de Android).

---

## 10. Notas de entorno

- Idioma de trabajo: castellano.
- No usar Docker.
- Estilo esperado: directo, scripts completos (no fragmentos), señalar dudas y bloqueos.
