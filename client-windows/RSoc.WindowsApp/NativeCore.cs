using System.Runtime.InteropServices;

namespace RSoc.Client;

/// <summary>
/// Envoltura P/Invoke del núcleo nativo <c>RSocClientCore.dll</c> (captura DXGI + inyección
/// de input). Mantiene el plano de medios en C++ y deja la orquestación/transporte en C#.
/// </summary>
public static class NativeCore
{
    private const string Dll = "RSocClientCore";

    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public IntPtr Data;          // BGRA top-down; válido hasta ReleaseFrame
        public int Width;
        public int Height;
        public int Stride;           // bytes por fila
        public ulong TimestampQpc;
    }

    public enum InputKind
    {
        MouseMove = 0,
        MouseButton = 1,
        Key = 2,
        Wheel = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputEvent
    {
        public int Kind;
        public int X;
        public int Y;
        public int Code;
        public int Down;
        public int Wheel;
    }

    [DllImport(Dll, EntryPoint = "rsoc_capture_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr CaptureCreate(int monitorIndex);

    /// <summary>Número de monitores capturables (>=1, o 0 si no se puede consultar).</summary>
    [DllImport(Dll, EntryPoint = "rsoc_capture_monitor_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int CaptureMonitorCount();

    /// <summary>1 = frame disponible, 0 = timeout, -1 = error (recrear el capturador).</summary>
    [DllImport(Dll, EntryPoint = "rsoc_capture_grab", CallingConvention = CallingConvention.Cdecl)]
    public static extern int CaptureGrab(IntPtr handle, ref Frame frame, int timeoutMs);

    [DllImport(Dll, EntryPoint = "rsoc_capture_release_frame", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CaptureReleaseFrame(IntPtr handle);

    [DllImport(Dll, EntryPoint = "rsoc_capture_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CaptureDestroy(IntPtr handle);

    [DllImport(Dll, EntryPoint = "rsoc_input_inject", CallingConvention = CallingConvention.Cdecl)]
    public static extern void InputInject(ref InputEvent ev);

    // --- Códec ---

    [DllImport(Dll, EntryPoint = "rsoc_encoder_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr EncoderCreate(int quality);

    /// <summary>1 = ok (out_data/out_len válidos hasta la próxima llamada), 0 = error.</summary>
    [DllImport(Dll, EntryPoint = "rsoc_encoder_encode", CallingConvention = CallingConvention.Cdecl)]
    public static extern int EncoderEncode(IntPtr handle, IntPtr bgra, int width, int height, int stride,
        out IntPtr outData, out int outLen);

    [DllImport(Dll, EntryPoint = "rsoc_encoder_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void EncoderDestroy(IntPtr handle);

    [DllImport(Dll, EntryPoint = "rsoc_encoder_set_quality", CallingConvention = CallingConvention.Cdecl)]
    public static extern void EncoderSetQuality(IntPtr handle, int quality);

    [DllImport(Dll, EntryPoint = "rsoc_encoder_set_grayscale", CallingConvention = CallingConvention.Cdecl)]
    public static extern void EncoderSetGrayscale(IntPtr handle, int grayscale);

    [DllImport(Dll, EntryPoint = "rsoc_decoder_create", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr DecoderCreate();

    /// <summary>1 = ok (out_bgra válido hasta la próxima llamada), 0 = error.</summary>
    [DllImport(Dll, EntryPoint = "rsoc_decoder_decode", CallingConvention = CallingConvention.Cdecl)]
    public static extern int DecoderDecode(IntPtr handle, byte[] data, int len,
        out IntPtr outBgra, out int outWidth, out int outHeight, out int outStride);

    [DllImport(Dll, EntryPoint = "rsoc_decoder_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void DecoderDestroy(IntPtr handle);
}
