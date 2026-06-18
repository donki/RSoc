using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using RSoc.Protocol;

namespace RSoc.Client;

/// <summary>
/// Lado servido (agente). Captura el escritorio con el núcleo nativo, comprime cada frame y
/// lo envía por el canal de sesión; en paralelo recibe del controlador eventos de input (que
/// inyecta), texto de portapapeles y ficheros. El stream ya debe estar emparejado en el relay.
/// </summary>
public sealed class SessionHost
{
    public int TargetFps { get; init; } = 15;
    public int Quality { get; init; } = 60;

    /// <summary>Texto de portapapeles recibido del controlador.</summary>
    public event Action<string>? ClipboardReceived;
    /// <summary>Ruta de un fichero recibido del controlador.</summary>
    public event Action<string>? FileReceived;

    private SessionChannel? _channel;
    private FileTransfer? _files;

    // Multimonitor: pantalla capturada ahora y la solicitada por el controlador.
    private int _currentMonitor;
    private int _requestedMonitor;
    private int _monitorCount = 1;

    // Calidad de la retransmisión, ajustable en caliente por el controlador.
    // Empaquetada en un int para leer/escribir atómicamente: bits 0..15 calidad, bit 16 grises.
    private int _qualityPacked;
    private int _appliedQuality = -1;

    public ValueTask SendClipboardAsync(string text, CancellationToken ct = default) =>
        _channel?.SendClipboardTextAsync(text, ct) ?? ValueTask.CompletedTask;

    public Task SendFileAsync(string path, CancellationToken ct = default) =>
        _files?.SendFileAsync(path, ct) ?? Task.CompletedTask;

    public async Task RunAsync(Stream stream, CancellationToken ct = default)
    {
        // Cifra el canal de sesión: este lado (agente) actúa de servidor TLS con cert autofirmado.
        // El relay solo ve bytes TLS opacos; el contenido va cifrado extremo a extremo entre clientes.
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        var cert = SelfSignedCert.LoadOrCreate(
            Path.Combine(AppContext.BaseDirectory, "rsoc-agent.pfx"), "RSoc Agent");
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = cert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, ct);

        var channel = new SessionChannel(ssl);
        _channel = channel;
        _files = new FileTransfer(channel);
        _files.FileReceived += p => FileReceived?.Invoke(p);

        var capture = NativeCore.CaptureCreate(_currentMonitor);
        if (capture == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo iniciar la captura DXGI (¿sin sesión de escritorio?).");
        _monitorCount = Math.Max(1, NativeCore.CaptureMonitorCount());
        var encoder = NativeCore.EncoderCreate(Quality);
        Volatile.Write(ref _qualityPacked, Quality & 0xFFFF); // calidad inicial, color

        var frameDelay = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, TargetFps));
        var receiveTask = ReceiveLoopAsync(channel, ct);

        try
        {
            bool configSent = false;
            var frame = new NativeCore.Frame();

            while (!ct.IsCancellationRequested)
            {
                // ¿El controlador pidió otra pantalla? Recrea el capturador para ese monitor.
                int want = Volatile.Read(ref _requestedMonitor);
                if (want != _currentMonitor && want >= 0 && want < _monitorCount)
                {
                    NativeCore.CaptureDestroy(capture);
                    var nc = NativeCore.CaptureCreate(want);
                    if (nc == IntPtr.Zero) nc = NativeCore.CaptureCreate(_currentMonitor); // revierte
                    else _currentMonitor = want;
                    if (nc == IntPtr.Zero) break;
                    capture = nc;
                    configSent = false; // reenvía config + MonitorInfo para la nueva pantalla
                }

                // ¿El controlador cambió la calidad / blanco y negro? Aplícalo al encoder.
                int qp = Volatile.Read(ref _qualityPacked);
                if (qp != _appliedQuality)
                {
                    NativeCore.EncoderSetQuality(encoder, qp & 0xFFFF);
                    NativeCore.EncoderSetGrayscale(encoder, (qp & 0x10000) != 0 ? 1 : 0);
                    _appliedQuality = qp;
                }

                int r = NativeCore.CaptureGrab(capture, ref frame, 100);
                if (r == 0) { await Task.Delay(frameDelay, ct); continue; }
                if (r < 0)
                {
                    NativeCore.CaptureDestroy(capture);
                    capture = NativeCore.CaptureCreate(_currentMonitor);
                    if (capture == IntPtr.Zero) break;
                    continue;
                }

                byte[]? encoded = null;
                try
                {
                    if (!configSent)
                    {
                        await channel.SendVideoConfigAsync(new VideoConfig(frame.Width, frame.Height), ct);
                        await channel.SendMonitorInfoAsync(_monitorCount, _currentMonitor, ct);
                        configSent = true;
                    }
                    if (NativeCore.EncoderEncode(encoder, frame.Data, frame.Width, frame.Height, frame.Stride,
                            out var outData, out var outLen) == 1 && outLen > 0)
                    {
                        encoded = new byte[outLen];
                        Marshal.Copy(outData, encoded, 0, outLen);
                    }
                }
                finally { NativeCore.CaptureReleaseFrame(capture); }

                if (encoded is not null)
                    await channel.SendVideoFrameAsync(new VideoFrame(true, (long)frame.TimestampQpc, encoded), ct);

                await Task.Delay(frameDelay, ct);
            }
        }
        finally
        {
            if (encoder != IntPtr.Zero) NativeCore.EncoderDestroy(encoder);
            if (capture != IntPtr.Zero) NativeCore.CaptureDestroy(capture);
            try { await receiveTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoopAsync(SessionChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var msg = await channel.ReadAsync(ct);
            if (msg is null) break;
            var (type, payload) = msg.Value;

            switch (type)
            {
                case SessionMessageType.Input:
                    var m = SessionChannel.ParseInput(payload);
                    var ev = new NativeCore.InputEvent
                    {
                        Kind = m.Kind, X = m.X, Y = m.Y, Code = m.Code, Down = m.Down, Wheel = m.Wheel,
                    };
                    NativeCore.InputInject(ref ev);
                    break;

                case SessionMessageType.ClipboardText:
                    ClipboardReceived?.Invoke(SessionChannel.ParseClipboardText(payload));
                    break;

                case SessionMessageType.FileOffer:
                    var (oid, name, len) = SessionChannel.ParseFileOffer(payload);
                    _files!.OnOffer(oid, name, len);
                    break;

                case SessionMessageType.FileChunk:
                    var (cid, data) = SessionChannel.ParseFileChunk(payload);
                    _files!.OnChunk(cid, data);
                    break;

                case SessionMessageType.FileEnd:
                    _files!.OnEnd(SessionChannel.ParseFileEnd(payload));
                    break;

                case SessionMessageType.SelectMonitor:
                    Volatile.Write(ref _requestedMonitor, SessionChannel.ParseSelectMonitor(payload));
                    break;

                case SessionMessageType.SetQuality:
                    var (q, gray) = SessionChannel.ParseSetQuality(payload);
                    q = Math.Clamp(q, 1, 100);
                    Volatile.Write(ref _qualityPacked, (q & 0xFFFF) | (gray ? 0x10000 : 0));
                    break;
            }
        }
    }
}
