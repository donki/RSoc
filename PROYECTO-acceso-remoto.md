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

## 2. Arquitectura

- **Relay puro:** A se conecta al servidor, B se conecta al servidor, y **todo** el tráfico
  va A ↔ Relay ↔ B. **Sin P2P** (no hay hole-punching ni conexión directa).
- **Servidor (dos componentes):**
  - **RSocServer** — registro de IDs / señalización / API / panel. **C# / ASP.NET Core.**
  - **RSocRelay** — reenvío de tráfico de sesión. **C++** (alto throughput).
- **Servidor en producción:** host con **IP pública real**.
- **Servidor para la prueba inicial:** ejecutado en **Windows local**.

---

## 3. Restricciones y decisiones firmes

- **Licencia MIT.** Todo código nuevo; sin dependencias AGPL/GPL.
- **Stack:** **C++**, **C#** y **ASP.NET**. **Nada de Rust, Dart ni Flutter.**
- **Relay puro:** forzado por diseño; no existe ruta P2P.
- **Empaquetado e instalación con PowerShell.**
- **Servidor, relay y clave preconfigurados** en los clientes (el usuario final no configura
  nada).

---

## 4. Componentes

### 4.1 Servidor

- **RSocServer (C#/ASP.NET Core):** registro de dispositivos/IDs, autenticación,
  señalización (emparejar solicitante y destino, emitir token de sesión + dirección de
  relay), address book/lista de dispositivos, API de administración y **panel web de
  monitorización** (ASP.NET). Persistencia propia (SQLite/SQL Server según despliegue).
- **RSocRelay (C++):** acepta los dos extremos de cada sesión, valida el token y **reenvía
  bytes** full-duplex sin interpretarlos. Diseñado para streaming de vídeo.
- **Prueba inicial:** ejecutar en Windows local.
- **Producción:** host con IP pública real.

### 4.2 Clientes

- **Cliente Windows:** núcleo **C++** (captura DXGI, códec por Media Foundation, inyección de
  input, transporte TLS propio) + UI **C#/.NET** (WinUI/WPF).
- **Cliente Android:** **.NET for Android (C#)** + interop nativo **C++ (NDK)**: captura por
  **MediaProjection**, códec por **MediaCodec**, inyección de input por
  **AccessibilityService** (única vía soportada por Android — limitación de la plataforma).
- **Preconfiguración:** servidor/relay/clave fijados por fichero de config; el usuario final
  no configura nada.
- Branding propio: nombre **RSoc**, logo e icono propios.

### 4.3 Panel de monitorización

- Parte de **RSocServer** (ASP.NET): dispositivos registrados, última actividad, estado de
  servicios y logs. Al ser servidor propio, el panel puede exponer gestión real (no solo
  lectura), según se implemente.

---

## 5. Plan por fases

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
   - Despliegue del servidor en host con **IP pública real** para pruebas reales.

---

## 6. Requisitos de build (referencia)

- **RSocServer (C#/ASP.NET):** SDK .NET; `dotnet publish` self-contained. Orquestado por
  PowerShell.
- **RSocRelay y núcleo cliente (C++):** MSVC + CMake (Windows), NDK (Android).
- **UI Windows (.NET):** SDK .NET (WinUI/WPF).
- **Cliente Android (.NET for Android):** SDK .NET + workload Android + NDK.
- **Empaquetado/instalación:** scripts **PowerShell**.

