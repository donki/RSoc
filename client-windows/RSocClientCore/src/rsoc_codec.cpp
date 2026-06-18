// RSocClientCore — códec de vídeo (MJPEG vía Windows Imaging Component).
//
// Implementación real y robusta basada en WIC: cada frame BGRA se comprime a JPEG y se
// descomprime de vuelta a BGRA. Es la primera versión funcional del plano de vídeo; la ABI
// (rsoc_encoder_* / rsoc_decoder_*) permite cambiar el backend a H.264/Media Foundation más
// adelante sin tocar C# ni la UI.

#define WIN32_LEAN_AND_MEAN
#include "rsoc_core.h"

#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <vector>
#include <new>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

namespace {

// Inicializa COM por hilo/handle de códec. Si el hilo ya está en otro apartamento (p.ej.
// STA de la UI), CoInitializeEx devuelve RPC_E_CHANGED_MODE: COM sigue siendo usable (WIC
// funciona en STA y MTA), solo que no debemos hacer el CoUninitialize correspondiente.
struct ComScope {
    bool usable = false; // COM utilizable en este hilo
    bool owns   = false; // nosotros inicializamos COM (hay que balancear con CoUninitialize)
    ComScope() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (hr == S_OK || hr == S_FALSE) { usable = true; owns = true; }
        else if (hr == RPC_E_CHANGED_MODE) { usable = true; owns = false; }
    }
    ~ComScope() { if (owns) CoUninitialize(); }
};

struct EncoderContext {
    ComScope com;
    ComPtr<IWICImagingFactory> factory;
    std::vector<uint8_t> out; // buffer de salida persistente
    float quality = 0.6f;
    bool  grayscale = false;  // comprimir en blanco y negro (JPEG 8bpp gris)
};

struct DecoderContext {
    ComScope com;
    ComPtr<IWICImagingFactory> factory;
    std::vector<uint8_t> out; // BGRA persistente
    int width = 0, height = 0;
};

ComPtr<IWICImagingFactory> CreateFactory() {
    ComPtr<IWICImagingFactory> f;
    CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                     IID_PPV_ARGS(&f));
    return f;
}

} // namespace

// --- Encoder ---

extern "C" RSOC_API void* rsoc_encoder_create(int32_t quality) {
    auto* e = new (std::nothrow) EncoderContext();
    if (!e || !e->com.usable) { delete e; return nullptr; }
    e->factory = CreateFactory();
    if (!e->factory) { delete e; return nullptr; }
    if (quality < 1) quality = 1;
    if (quality > 100) quality = 100;
    e->quality = quality / 100.0f;
    return e;
}

extern "C" RSOC_API int32_t rsoc_encoder_encode(void* handle,
    const uint8_t* bgra, int32_t width, int32_t height, int32_t stride,
    const uint8_t** out_data, int32_t* out_len) {
    auto* e = static_cast<EncoderContext*>(handle);
    if (!e || !bgra || !out_data || !out_len) return 0;

    ComPtr<IWICBitmap> bmp;
    if (FAILED(e->factory->CreateBitmapFromMemory(
            (UINT)width, (UINT)height, GUID_WICPixelFormat32bppBGRA,
            (UINT)stride, (UINT)stride * (UINT)height,
            const_cast<BYTE*>(bgra), &bmp)))
        return 0;

    ComPtr<IStream> stream;
    if (FAILED(CreateStreamOnHGlobal(nullptr, TRUE, &stream))) return 0;

    ComPtr<IWICBitmapEncoder> enc;
    if (FAILED(e->factory->CreateEncoder(GUID_ContainerFormatJpeg, nullptr, &enc))) return 0;
    if (FAILED(enc->Initialize(stream.Get(), WICBitmapEncoderNoCache))) return 0;

    ComPtr<IWICBitmapFrameEncode> frame;
    ComPtr<IPropertyBag2> props;
    if (FAILED(enc->CreateNewFrame(&frame, &props))) return 0;

    PROPBAG2 opt = {};
    opt.pstrName = const_cast<LPOLESTR>(L"ImageQuality");
    VARIANT v = {}; v.vt = VT_R4; v.fltVal = e->quality;
    props->Write(1, &opt, &v);

    if (FAILED(frame->Initialize(props.Get()))) return 0;
    if (FAILED(frame->SetSize((UINT)width, (UINT)height))) return 0;

    if (e->grayscale) {
        // JPEG en escala de grises: convierte BGRA -> 8bpp gris y vuelca esa fuente.
        WICPixelFormatGUID fmt = GUID_WICPixelFormat8bppGray;
        frame->SetPixelFormat(&fmt);
        ComPtr<IWICFormatConverter> conv;
        if (FAILED(e->factory->CreateFormatConverter(&conv))) return 0;
        if (FAILED(conv->Initialize(bmp.Get(), GUID_WICPixelFormat8bppGray,
                WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
            return 0;
        if (FAILED(frame->WriteSource(conv.Get(), nullptr))) return 0;
    } else {
        WICPixelFormatGUID fmt = GUID_WICPixelFormat24bppBGR;
        frame->SetPixelFormat(&fmt);
        if (FAILED(frame->WriteSource(bmp.Get(), nullptr))) return 0;
    }
    if (FAILED(frame->Commit())) return 0;
    if (FAILED(enc->Commit())) return 0;

    // Vuelca el IStream al buffer persistente.
    STATSTG stat = {};
    if (FAILED(stream->Stat(&stat, STATFLAG_NONAME))) return 0;
    ULONG size = (ULONG)stat.cbSize.QuadPart;
    e->out.resize(size);
    LARGE_INTEGER zero = {};
    stream->Seek(zero, STREAM_SEEK_SET, nullptr);
    ULONG read = 0;
    if (size > 0 && FAILED(stream->Read(e->out.data(), size, &read))) return 0;

    *out_data = e->out.data();
    *out_len = (int32_t)read;
    return 1;
}

extern "C" RSOC_API void rsoc_encoder_set_quality(void* handle, int32_t quality) {
    auto* e = static_cast<EncoderContext*>(handle);
    if (!e) return;
    if (quality < 1) quality = 1;
    if (quality > 100) quality = 100;
    e->quality = quality / 100.0f;
}

extern "C" RSOC_API void rsoc_encoder_set_grayscale(void* handle, int32_t grayscale) {
    auto* e = static_cast<EncoderContext*>(handle);
    if (e) e->grayscale = (grayscale != 0);
}

extern "C" RSOC_API void rsoc_encoder_destroy(void* handle) {
    delete static_cast<EncoderContext*>(handle);
}

// --- Decoder ---

extern "C" RSOC_API void* rsoc_decoder_create(void) {
    auto* d = new (std::nothrow) DecoderContext();
    if (!d || !d->com.usable) { delete d; return nullptr; }
    d->factory = CreateFactory();
    if (!d->factory) { delete d; return nullptr; }
    return d;
}

extern "C" RSOC_API int32_t rsoc_decoder_decode(void* handle,
    const uint8_t* data, int32_t len,
    const uint8_t** out_bgra, int32_t* out_width, int32_t* out_height, int32_t* out_stride) {
    auto* d = static_cast<DecoderContext*>(handle);
    if (!d || !data || len <= 0 || !out_bgra || !out_width || !out_height || !out_stride) return 0;

    ComPtr<IWICStream> stream;
    if (FAILED(d->factory->CreateStream(&stream))) return 0;
    if (FAILED(stream->InitializeFromMemory(const_cast<BYTE*>(data), (DWORD)len))) return 0;

    ComPtr<IWICBitmapDecoder> dec;
    if (FAILED(d->factory->CreateDecoderFromStream(
            stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, &dec)))
        return 0;

    ComPtr<IWICBitmapFrameDecode> frame;
    if (FAILED(dec->GetFrame(0, &frame))) return 0;

    ComPtr<IWICFormatConverter> conv;
    if (FAILED(d->factory->CreateFormatConverter(&conv))) return 0;
    if (FAILED(conv->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA,
            WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom)))
        return 0;

    UINT w = 0, h = 0;
    conv->GetSize(&w, &h);
    UINT stride = w * 4;
    d->out.resize((size_t)stride * h);
    d->width = (int)w; d->height = (int)h;
    if (FAILED(conv->CopyPixels(nullptr, stride, (UINT)d->out.size(), d->out.data())))
        return 0;

    *out_bgra = d->out.data();
    *out_width = (int32_t)w;
    *out_height = (int32_t)h;
    *out_stride = (int32_t)stride;
    return 1;
}

extern "C" RSOC_API void rsoc_decoder_destroy(void* handle) {
    delete static_cast<DecoderContext*>(handle);
}
