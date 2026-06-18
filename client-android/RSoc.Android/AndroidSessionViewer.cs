using System.Net.Security;
using System.Security.Authentication;
using Android.Graphics;
using RSoc.Client;
using RSoc.Protocol;

namespace RSoc.Android;

/// <summary>
/// Visor de sesión para Android (lado controlador). Descodifica los frames MJPEG con
/// BitmapFactory, envía input táctil y soporta transferencia de ficheros en ambos sentidos.
/// </summary>
public sealed class AndroidSessionViewer
{
    /// <summary>Aceptar el certificado autofirmado del agente en el TLS de la sesión.</summary>
    public bool AcceptSelfSigned { get; init; } = true;

    public event Action<Bitmap>? FrameReady;
    public event Action<int, int>? ConfigReceived;
    /// <summary>Pantallas disponibles en el agente: (count, currentIndex).</summary>
    public event Action<int, int>? MonitorInfoReceived;
    /// <summary>Ruta de un fichero recibido del equipo remoto.</summary>
    public event Action<string>? FileReceived;

    /// <summary>Carpeta donde guardar ficheros recibidos (la fija la actividad).</summary>
    public string? DownloadDir { get; set; }

    private SessionChannel? _channel;
    private FileTransfer? _files;

    private long _totalEncodedBytes;
    private int _totalFrames;
    /// <summary>Bytes comprimidos de vídeo recibidos en total (para el indicador de velocidad).</summary>
    public long TotalEncodedBytes => Interlocked.Read(ref _totalEncodedBytes);
    public int TotalFrames => Volatile.Read(ref _totalFrames);

    public ValueTask SendInputAsync(InputMessage m, CancellationToken ct = default) =>
        _channel?.SendInputAsync(m, ct) ?? ValueTask.CompletedTask;

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
        _files = new FileTransfer(channel, DownloadDir);
        _files.FileReceived += p => FileReceived?.Invoke(p);

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
                    var bmp = BitmapFactory.DecodeByteArray(vf.Encoded, 0, vf.Encoded.Length);
                    if (bmp is not null) FrameReady?.Invoke(bmp);
                    break;

                case SessionMessageType.MonitorInfo:
                    var mi = SessionChannel.ParseMonitorInfo(payload);
                    MonitorInfoReceived?.Invoke(mi.Count, mi.Current);
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
}
