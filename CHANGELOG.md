# Changelog

Registro de cambios de RSoc. Versionado por fecha **`AAAA.MM.DD.N`** (constante única
[`AppVersion.Current`](src/RSoc.Protocol/AppVersion.cs)); el build `N` se incrementa
automáticamente en cada `build-dist.ps1`. Conforme a la sección 11 de la
[constitución](constitution/constitucion.md).

## [2026.06.18.3]

### Seguridad
- Sin credenciales por defecto en el repositorio: los ficheros de configuración versionados
  (`rsoc-server-config.json`, `rsoc-client-conf.json`) se publican con los campos sensibles vacíos;
  con credenciales vacías no se permite ningún login.

### Añadido
- Acceso y control remoto self-hosted desde cero (C++ / C# / ASP.NET Core), licencia MIT.
- Servidor: **RSocServer** (señalización + API REST + lista online + autoactualización + logging
  rotativo) y **RSocRelay** (reenvío emparejado por token).
- Cliente **Windows** (WinForms): agente + controlador + visor, captura DXGI, códec MJPEG (WIC),
  inyección de input, multimonitor, portapapeles y transferencia de ficheros.
- Cliente **Android** (.NET for Android): controlador y visor táctil.
- Cifrado: API por HTTPS y sesión por TLS extremo a extremo a través del relay.
- Autoactualización con verificación SHA-256 y rollout escalonado.
- Test de extremo a extremo (señalización + relay + flujo de bytes) y scripts PowerShell de build,
  empaquetado, instalación y despliegue (WinRM/OCI).

## Gobernanza

- Se incorpora la [constitución de proyectos de software](constitution/constitucion.md) como
  submódulo (`constitution/`) y se adopta como referencia de arquitectura, seguridad, versionado,
  publicación y mantenimiento.
