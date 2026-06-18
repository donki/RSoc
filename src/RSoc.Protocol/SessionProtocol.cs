using System.Buffers.Binary;

namespace RSoc.Protocol;

/// <summary>
/// Protocolo del plano de datos de una sesión RSoc, transportado sobre el stream del relay
/// una vez hecho el handshake. Mensajes con prefijo de longitud:
///
///   [4 bytes  longitud del payload, big-endian]
///   [1 byte   tipo (<see cref="SessionMessageType"/>)]
///   [N bytes  payload]
///
/// Tipos:
///   · VideoConfig: ancho/alto de origen (int32 + int32).
///   · VideoFrame:  [1 byte keyframe][8 bytes timestamp][bytes codificados].
///   · Input:       6 × int32 (kind, x, y, code, down, wheel) — espejo del evento nativo.
///
/// El contenido va, por ahora, en claro sobre el relay; el cifrado extremo a extremo entre
/// clientes se añade como capa por encima de este framing (seam marcado en la sesión).
/// </summary>
public enum SessionMessageType : byte
{
    VideoConfig = 1,
    VideoFrame = 2,
    Input = 3,
    ClipboardText = 4, // portapapeles de texto (UTF-8), bidireccional
    FileOffer = 5,     // [4 id][8 length][4 nameLen][name UTF-8]
    FileChunk = 6,     // [4 id][bytes…]
    FileEnd = 7,       // [4 id]
    MonitorInfo = 8,   // agente -> controlador: [4 count][4 currentIndex] (pantallas disponibles)
    SelectMonitor = 9, // controlador -> agente: [4 index] (cambiar de pantalla capturada)
    SetQuality = 10,   // controlador -> agente: [4 quality 1..100][1 grayscale 0/1]
}

public readonly record struct VideoConfig(int Width, int Height);

public readonly record struct VideoFrame(bool KeyFrame, long TimestampQpc, byte[] Encoded);

public readonly record struct InputMessage(int Kind, int X, int Y, int Code, int Down, int Wheel);

/// <summary>Lectura/escritura asíncrona de mensajes de sesión sobre un <see cref="Stream"/>.</summary>
public sealed class SessionChannel(Stream stream)
{
    private const int MaxPayload = 64 * 1024 * 1024; // guardia anti-basura
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async ValueTask SendVideoConfigAsync(VideoConfig cfg, CancellationToken ct = default)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), cfg.Width);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4), cfg.Height);
        await SendAsync(SessionMessageType.VideoConfig, p, ct);
    }

    public async ValueTask SendVideoFrameAsync(VideoFrame frame, CancellationToken ct = default)
    {
        var p = new byte[1 + 8 + frame.Encoded.Length];
        p[0] = (byte)(frame.KeyFrame ? 1 : 0);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(1), frame.TimestampQpc);
        frame.Encoded.CopyTo(p.AsSpan(9));
        await SendAsync(SessionMessageType.VideoFrame, p, ct);
    }

    public async ValueTask SendInputAsync(InputMessage m, CancellationToken ct = default)
    {
        var p = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), m.Kind);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4), m.X);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(8), m.Y);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(12), m.Code);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(16), m.Down);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(20), m.Wheel);
        await SendAsync(SessionMessageType.Input, p, ct);
    }

    // --- Portapapeles ---

    public ValueTask SendClipboardTextAsync(string text, CancellationToken ct = default) =>
        SendAsync(SessionMessageType.ClipboardText, System.Text.Encoding.UTF8.GetBytes(text), ct);

    public static string ParseClipboardText(byte[] p) => System.Text.Encoding.UTF8.GetString(p);

    // --- Transferencia de ficheros ---

    public ValueTask SendFileOfferAsync(int id, string name, long length, CancellationToken ct = default)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var p = new byte[4 + 8 + 4 + nameBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), id);
        BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(4), length);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(12), nameBytes.Length);
        nameBytes.CopyTo(p.AsSpan(16));
        return SendAsync(SessionMessageType.FileOffer, p, ct);
    }

    public ValueTask SendFileChunkAsync(int id, byte[] data, int count, CancellationToken ct = default)
    {
        var p = new byte[4 + count];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), id);
        data.AsSpan(0, count).CopyTo(p.AsSpan(4));
        return SendAsync(SessionMessageType.FileChunk, p, ct);
    }

    public ValueTask SendFileEndAsync(int id, CancellationToken ct = default)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), id);
        return SendAsync(SessionMessageType.FileEnd, p, ct);
    }

    public static (int Id, string Name, long Length) ParseFileOffer(byte[] p)
    {
        int id = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0));
        long length = BinaryPrimitives.ReadInt64BigEndian(p.AsSpan(4));
        int nameLen = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(12));
        string name = System.Text.Encoding.UTF8.GetString(p, 16, nameLen);
        return (id, name, length);
    }

    public static (int Id, byte[] Data) ParseFileChunk(byte[] p) =>
        (BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0)), p[4..]);

    public static int ParseFileEnd(byte[] p) => BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0));

    // --- Selección de pantalla (multimonitor) ---

    public ValueTask SendMonitorInfoAsync(int count, int current, CancellationToken ct = default)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), count);
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(4), current);
        return SendAsync(SessionMessageType.MonitorInfo, p, ct);
    }

    public static (int Count, int Current) ParseMonitorInfo(byte[] p) =>
        (BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0)),
         BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4)));

    public ValueTask SendSelectMonitorAsync(int index, CancellationToken ct = default)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), index);
        return SendAsync(SessionMessageType.SelectMonitor, p, ct);
    }

    public static int ParseSelectMonitor(byte[] p) => BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0));

    // --- Calidad de la retransmisión ---

    public ValueTask SendSetQualityAsync(int quality, bool grayscale, CancellationToken ct = default)
    {
        var p = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(0), quality);
        p[4] = (byte)(grayscale ? 1 : 0);
        return SendAsync(SessionMessageType.SetQuality, p, ct);
    }

    public static (int Quality, bool Grayscale) ParseSetQuality(byte[] p) =>
        (BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0)), p.Length > 4 && p[4] != 0);

    private async ValueTask SendAsync(SessionMessageType type, byte[] payload, CancellationToken ct)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), payload.Length);
        header[4] = (byte)type;
        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Lee el siguiente mensaje. Devuelve (tipo, payload) o null si el stream se cerró.</summary>
    public async ValueTask<(SessionMessageType Type, byte[] Payload)?> ReadAsync(CancellationToken ct = default)
    {
        var header = new byte[5];
        if (!await ReadExactAsync(header, ct)) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0));
        var type = (SessionMessageType)header[4];
        if (len < 0 || len > MaxPayload) throw new InvalidDataException($"Longitud de payload inválida: {len}");

        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(payload, ct)) return null;
        return (type, payload);
    }

    public static VideoConfig ParseVideoConfig(byte[] p) =>
        new(BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4)));

    public static VideoFrame ParseVideoFrame(byte[] p) =>
        new(p[0] == 1,
            BinaryPrimitives.ReadInt64BigEndian(p.AsSpan(1)),
            p[9..]);

    public static InputMessage ParseInput(byte[] p) =>
        new(BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(0)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(4)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(8)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(12)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(16)),
            BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(20)));

    private async ValueTask<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = await stream.ReadAsync(buf.AsMemory(got), ct);
            if (r == 0) return false;
            got += r;
        }
        return true;
    }
}
