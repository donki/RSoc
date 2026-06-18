# Avisos de terceros (Third-Party Notices)

RSoc se publica bajo licencia **MIT** (ver [LICENSE](LICENSE)). Todo el código fuente es original.
Este documento lista el software de terceros del que depende en build o en ejecución, y su licencia.
Ninguna de estas dependencias es copyleft (GPL/AGPL/LGPL) y todas permiten redistribución.

| Dependencia | Uso | Licencia | Titular |
|-------------|-----|----------|---------|
| .NET 10 (runtime + BCL) | Ejecución de servidor y clientes | MIT | Microsoft |
| ASP.NET Core | API REST del servidor | MIT | Microsoft |
| msquic (`msquic.dll`) | Incluido por el runtime .NET self-contained | MIT | Microsoft |
| .NET for Android / Mono.Android | Cliente Android | MIT | Microsoft / .NET Foundation |
| AndroidX (transitiva, si aplica) | Cliente Android | Apache-2.0 | Google |
| SDK de Windows (DXGI, Direct3D 11, WIC, Winsock) | Captura, códec e input en Windows | Componentes del SO (uso vía SDK) | Microsoft |

Notas:

- **msquic** lo distribuye el propio runtime de .NET al publicar *self-contained*; se redistribuye
  bajo su licencia MIT.
- Las **APIs del SDK de Windows** se usan enlazando contra las bibliotecas de importación del
  sistema (`d3d11.lib`, `dxgi.lib`, `windowscodecs.lib`, `ws2_32.lib`, `user32.lib`, `ole32.lib`).
  No se redistribuyen binarios de Microsoft salvo los que el propio runtime/SO aporta.
- El códec de vídeo es **MJPEG vía Windows Imaging Component** (componente del sistema). No se
  incluye `libjpeg` ni códecs de terceros.
