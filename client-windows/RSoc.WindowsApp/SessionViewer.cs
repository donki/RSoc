using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Lado controlador. Recibe los frames comprimidos, los descomprime a BGRA y los expone vía
/// <see cref="FrameReceived"/> para que la UI los pinte; envía al agente input, texto de
/// portapapeles y ficheros, y recibe del agente portapapeles y ficheros.
/// </summary>
public sealed class SessionViewer
{
    /// <summary>Aceptar el certificado autofirmado del agente en el TLS de la sesión.</summary>
    public bool AcceptSelfSigned { get; init; } = true;

    public event Action<int, int>? ConfigReceived;
    public event Action<int, int, int, byte[]>? FrameReceived;
    /// <summary>Pantallas disponibles en el agente: (count, currentIndex).</summary>
    public event Action<int, int>? MonitorInfoReceived;
    /// <summary>Texto de portapapeles recibido del agente.</summary>
    public event Action<string>? ClipboardReceived;
    /// <summary>Ruta de un fichero recibido del agente.</summary>
    public event Action<string>? FileReceived;

    private SessionChannel? _channel;
    private FileTransfer? _files;

    // Estadísticas de red (las lee la UI por sondeo para mostrar KB/s y fps).
    private long _totalEncodedBytes;
    private int _totalFrames;
    /// <summary>Bytes comprimidos de vídeo recibidos en total (acumulado).</summary>
    public long TotalEncodedBytes => Interlocked.Read(ref _totalEncodedBytes);
    /// <summary>Frames de vídeo recibidos en total (acumulado).</summary>
    public int TotalFrames => Volatile.Read(ref _totalFrames);

    public ValueTask SendInputAsync(InputMessage m, CancellationToken ct = default) =>
        _channel?.SendInputAsync(m, ct) ?? ValueTask.CompletedTask;

    public ValueTask SendClipboardAsync(string text, CancellationToken ct = default) =>
        _channel?.SendClipboardTextAsync(text, ct) ?? ValueTask.CompletedTask;

    /// <summary>Pide al agente capturar otra pantalla (índice 0..count-1).</summary>
    public ValueTask SendSelectMonitorAsync(int index, CancellationToken ct = default) =>
        _channel?.SendSelectMonitorAsync(index, ct) ?? ValueTask.CompletedTask;

    /// <summary>Pide al agente cambiar la calidad (1..100) y el modo blanco y negro.</summary>
    public ValueTask SendSetQualityAsync(int quality, bool grayscale, CancellationToken ct = default) =>
        _channel?.SendSetQualityAsync(quality, grayscale, ct) ?? ValueTask.CompletedTask;

    public Task SendFileAsync(string path, CancellationToken ct = default) =>
        _files?.SendFileAsync(path, ct) ?? Task.CompletedTask;

    public async Task RunAsync(Stream stream, CancellationToken ct = default)
    {
        // Cliente TLS sobre el relay. Si AcceptSelfSigned, acepta el cert autofirmado del agente;
        // si no, exige validación estándar del sistema (CA de confianza).
        var ssl = AcceptSelfSigned
            ? new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true)
            : new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "RSoc",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);

        var channel = new SessionChannel(ssl);
        _channel = channel;
        _files = new FileTransfer(channel);
        _files.FileReceived += p => FileReceived?.Invoke(p);

        var decoder = NativeCore.DecoderCreate();
        if (decoder == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo crear el descodificador.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await channel.ReadAsync(ct);
                if (msg is null) break;
                var (type, payload) = msg.Value;

                switch (type)
                {
                    case SessionMessageType.VideoConfig:
                        var cfg = SessionChannel.ParseVideoConfig(payload);
                        ConfigReceived?.Invoke(cfg.Width, cfg.Height);
                        break;

                    case SessionMessageType.VideoFrame:
                        var vf = SessionChannel.ParseVideoFrame(payload);
                        Interlocked.Add(ref _totalEncodedBytes, vf.Encoded.Length);
                        Interlocked.Increment(ref _totalFrames);
                        if (NativeCore.DecoderDecode(decoder, vf.Encoded, vf.Encoded.Length,
                                out var ptr, out var w, out var h, out var stride) == 1)
                        {
                            var buf = new byte[stride * h];
                            Marshal.Copy(ptr, buf, 0, buf.Length);
                            FrameReceived?.Invoke(w, h, stride, buf);
                        }
                        break;

                    case SessionMessageType.MonitorInfo:
                        var mi = SessionChannel.ParseMonitorInfo(payload);
                        MonitorInfoReceived?.Invoke(mi.Count, mi.Current);
                        break;

                    case SessionMessageType.ClipboardText:
                        ClipboardReceived?.Invoke(SessionChannel.ParseClipboardText(payload));
                        break;

                    case SessionMessageType.FileOffer:
                        var (oid, name, len) = SessionChannel.ParseFileOffer(payload);
                        _files.OnOffer(oid, name, len);
                        break;

                    case SessionMessageType.FileChunk:
                        var (cid, data) = SessionChannel.ParseFileChunk(payload);
                        _files.OnChunk(cid, data);
                        break;

                    case SessionMessageType.FileEnd:
                        _files.OnEnd(SessionChannel.ParseFileEnd(payload));
                        break;
                }
            }
        }
        finally
        {
            if (decoder != IntPtr.Zero) NativeCore.DecoderDestroy(decoder);
        }
    }
}
