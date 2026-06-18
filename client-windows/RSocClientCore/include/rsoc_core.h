// RSocClientCore — ABI C del núcleo nativo del cliente Windows de RSoc.
//
// Expone la captura de pantalla (DXGI Desktop Duplication) y la inyección de entrada
// (SendInput) como una superficie C estable, pensada para invocarse por P/Invoke desde la
// capa .NET (RSoc.Client), que es quien posee el socket de relay y el protocolo de sesión.
//
// Reparto de responsabilidades (stack C++ + C#):
//   · C++ (este módulo): captura de frames y, más adelante, códec por Media Foundation, e
//     inyección de teclado/ratón.
//   · C#  (RSoc.Client): conexión al relay, handshake, cifrado de sesión y orquestación.

#ifndef RSOC_CORE_H
#define RSOC_CORE_H

#include <stdint.h>

#ifdef RSOC_CORE_EXPORTS
#define RSOC_API __declspec(dllexport)
#else
#define RSOC_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// --- Captura de pantalla ---

// Frame capturado. El puntero `data` apunta a memoria mapeada del backend y solo es válido
// hasta la siguiente llamada a rsoc_capture_release_frame() sobre el mismo handle.
// Formato: BGRA de 8 bits por canal, de arriba a abajo.
typedef struct RSocFrame {
    const uint8_t* data;
    int32_t        width;
    int32_t        height;
    int32_t        stride;        // bytes por fila (puede ser mayor que width*4)
    uint64_t       timestamp_qpc; // QueryPerformanceCounter en el momento de la captura
} RSocFrame;

// Crea un capturador del monitor indicado (0 = primario / primer output del adaptador 0).
// Devuelve un handle opaco o NULL si falla. Fija además el monitor activo para el mapeo del
// ratón en rsoc_input_inject (coordenadas relativas a ese monitor sobre el escritorio virtual).
RSOC_API void* rsoc_capture_create(int32_t monitor_index);

// Número de monitores capturables (outputs del adaptador 0). >=1, o 0 si no se puede consultar.
RSOC_API int32_t rsoc_capture_monitor_count(void);

// Intenta obtener el siguiente frame. Devuelve:
//   1  = frame disponible (rellena *out_frame),
//   0  = timeout sin cambios,
//  -1  = error (p.ej. pérdida de acceso; conviene recrear el capturador).
RSOC_API int32_t rsoc_capture_grab(void* handle, RSocFrame* out_frame, int32_t timeout_ms);

// Libera el frame en curso (desmapea el recurso). Debe llamarse tras cada grab que devuelva 1.
RSOC_API void rsoc_capture_release_frame(void* handle);

// Destruye el capturador y libera todos sus recursos.
RSOC_API void rsoc_capture_destroy(void* handle);

// --- Inyección de entrada ---

typedef enum RSocInputKind {
    RSOC_INPUT_MOUSE_MOVE   = 0, // x,y normalizados a [0,65535] sobre el escritorio virtual
    RSOC_INPUT_MOUSE_BUTTON = 1, // code: 0=izq 1=der 2=medio ; down: 1=pulsar 0=soltar
    RSOC_INPUT_KEY          = 2, // code: virtual-key ; down: 1=pulsar 0=soltar
    RSOC_INPUT_WHEEL        = 3, // wheel: delta (múltiplos de 120)
} RSocInputKind;

typedef struct RSocInputEvent {
    int32_t kind;   // RSocInputKind
    int32_t x;
    int32_t y;
    int32_t code;
    int32_t down;
    int32_t wheel;
} RSocInputEvent;

// Inyecta un evento de entrada en la sesión local (vía SendInput).
RSOC_API void rsoc_input_inject(const RSocInputEvent* ev);

// --- Códec de vídeo ---
//
// Primera implementación real: MJPEG vía WIC (compresión por frame, robusta). La misma ABI
// admite sustituir el backend por H.264/Media Foundation (inter-frame) sin tocar las capas
// superiores. Los punteros de salida son propiedad del códec y válidos hasta la siguiente
// llamada sobre el mismo handle o hasta destruirlo.

// Encoder: BGRA -> bytes comprimidos. `quality` 1..100.
RSOC_API void* rsoc_encoder_create(int32_t quality);
RSOC_API int32_t rsoc_encoder_encode(void* handle,
    const uint8_t* bgra, int32_t width, int32_t height, int32_t stride,
    const uint8_t** out_data, int32_t* out_len);
RSOC_API void rsoc_encoder_destroy(void* handle);

// Ajustes en caliente del encoder (sin recrearlo): calidad 1..100 y blanco y negro (0/1).
RSOC_API void rsoc_encoder_set_quality(void* handle, int32_t quality);
RSOC_API void rsoc_encoder_set_grayscale(void* handle, int32_t grayscale);

// Decoder: bytes comprimidos -> BGRA (top-down).
RSOC_API void* rsoc_decoder_create(void);
RSOC_API int32_t rsoc_decoder_decode(void* handle,
    const uint8_t* data, int32_t len,
    const uint8_t** out_bgra, int32_t* out_width, int32_t* out_height, int32_t* out_stride);
RSOC_API void rsoc_decoder_destroy(void* handle);

#ifdef __cplusplus
} // extern "C"
#endif

#endif // RSOC_CORE_H
