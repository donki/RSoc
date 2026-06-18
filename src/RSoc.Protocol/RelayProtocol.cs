using System.Buffers.Binary;

namespace RSoc.Protocol;

/// <summary>
/// Definición del protocolo de enlace (handshake) entre un cliente y RSocRelay.
///
/// RSocRelay es un reenviador puro y sin estado: no entiende el contenido de la sesión,
/// solo empareja las dos conexiones que presentan el mismo <c>token</c> de 16 bytes
/// (emitido por RSocServer al crear la sesión) y reenvía los bytes en ambos sentidos.
///
/// Trama de handshake (22 bytes, big-endian donde aplique):
///   [0..3]   Magic  = "RSOC" (0x52 0x53 0x4F 0x43)
///   [4]      Version
///   [5]      Role   (0 = Controller, 1 = Agent)
///   [6..21]  Token  (16 bytes opacos)
/// Tras el handshake, el canal transporta bytes de sesión sin interpretar.
///
/// Estas mismas constantes están replicadas en el relay nativo (relay.cpp). Si cambian
/// aquí, hay que actualizarlas allí.
/// </summary>
public static class RelayProtocol
{
    public static readonly byte[] Magic = "RSOC"u8.ToArray();
    public const byte Version = 1;
    public const int TokenSize = 16;
    public const int HandshakeSize = 4 + 1 + 1 + TokenSize; // 22

    public enum Role : byte
    {
        Controller = 0,
        Agent = 1,
    }

    /// <summary>Serializa el handshake en un buffer de <see cref="HandshakeSize"/> bytes.</summary>
    public static byte[] BuildHandshake(Role role, ReadOnlySpan<byte> token)
    {
        if (token.Length != TokenSize)
            throw new ArgumentException($"El token debe medir {TokenSize} bytes.", nameof(token));

        var buf = new byte[HandshakeSize];
        Magic.CopyTo(buf, 0);
        buf[4] = Version;
        buf[5] = (byte)role;
        token.CopyTo(buf.AsSpan(6));
        return buf;
    }

    /// <summary>Valida y descompone un handshake recibido.</summary>
    public static bool TryParseHandshake(ReadOnlySpan<byte> buf, out Role role, out byte[] token)
    {
        role = default;
        token = [];
        if (buf.Length < HandshakeSize) return false;
        if (!buf[..4].SequenceEqual(Magic)) return false;
        if (buf[4] != Version) return false;
        role = (Role)buf[5];
        token = buf.Slice(6, TokenSize).ToArray();
        return true;
    }
}
