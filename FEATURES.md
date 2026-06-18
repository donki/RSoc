# RSoc — Características

Lista interna de capacidades del sistema (para visión rápida de lo que hace).

## Plataformas
- Cliente **Windows** de escritorio: actúa de **agente** (controlado) y de **controlador** a la vez.
- Cliente **Android**: **controlador** y visor táctil.
- Servidor **autoalojado** (RSocServer + RSocRelay), sin dependencia de nubes de terceros.

## Conexión y señalización
- Registro de dispositivos con keep-alive y lista de equipos online.
- Login del API de gestión con token bearer.
- Apertura de sesión validando contraseña de conexión (control desatendido).
- Relay puro: empareja los dos extremos por token aleatorio de 16 bytes y reenvía bytes.
- Reconexión resiliente del agente (se re-registra solo si el servidor reinicia).
- Relay accesible cross-red anunciando host público.

## Control remoto
- Captura de pantalla por **DXGI Desktop Duplication**.
- Códec de vídeo **MJPEG** vía Windows Imaging Component (encode/decode).
- Inyección de teclado, ratón y rueda (SendInput).
- **Multimonitor**: selección de pantalla a capturar desde el visor (Windows y Android).
- Mapeo de cursor correcto al monitor activo sobre el escritorio virtual.

## Calidad de imagen
- Calidad ajustable en caliente: **Alta / Media / Baja / Mínima**.
- Modo **blanco y negro** (JPEG en escala de grises).
- **Indicador de red** en la barra: KB/s y fps, con color según calidad (buena/regular/pobre).

## Productividad en sesión
- **Portapapeles** de texto bidireccional.
- **Transferencia de ficheros** bidireccional (arrastrar y soltar en Windows; selector en Android).
- Visor con pantalla completa; en Android, gestos: arrastrar=cursor, tocar=clic, pellizco=zoom,
  dos dedos=clic derecho/scroll.

## Seguridad y consentimiento
- **Tráfico cifrado de extremo a extremo:** API **siempre por HTTPS** (TLS) y sesión sobre el relay
  por **TLS E2E** entre los dos clientes — el relay reenvía bytes TLS opacos y no ve el contenido.
- Certificados **autofirmados** generados automáticamente (servidor y agente); los clientes los aceptan.
- **Confirmación de acceso** opcional en el equipo **controlado** (no en el origen), con aviso al
  frente y auto-denegación a los 30 s.
- Contraseñas de conexión almacenadas como hash (no en claro).
- Emparejado por token; el relay no interpreta el contenido de la sesión.

## Autoactualización
- Comprobación de versión contra el servidor al conectar (Windows y Android).
- Descarga del cliente desde el **propio servidor**, con verificación **SHA-256**.
- Windows: reinstalación por swap + reinicio preservando la config del usuario.
- Android: descarga del APK e instalación por el sistema (confirma el usuario).
- **Rollout escalonado**: límite de descargas concurrentes en el servidor + *jitter* por
  dispositivo, para no saturar (no se actualizan todos a la vez).

## Operación y despliegue
- **Logging rotativo** (10 MB × 10 ficheros) en servidor y relay; el relay genera además **un
  fichero de log por cada par** conectado.
- Despliegue a **VM Windows de Oracle Cloud (OCI)** por WinRM (`deploy-oracle.ps1`).
- Instalación como **servicios de Windows** con apertura de firewall.
- Empaquetado de distribución completo (servidor + cliente + APK + artefactos de update).

## Experiencia de usuario
- Tema claro/oscuro automático (Windows y Android).
- Ventana con cromo propio (sin marco del sistema) en Windows; minimiza a la bandeja.
- Arranque automático con Windows (configurable).

## Calidad del proyecto
- Test de extremo a extremo automatizado (señalización + relay + flujo de bytes).
- Licencia MIT.
