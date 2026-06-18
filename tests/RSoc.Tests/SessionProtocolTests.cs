// =====================================================================================
//  RSoc — Suite de tests: SessionProtocolTests
// -------------------------------------------------------------------------------------
//  QUÉ SE PRUEBA
//    El protocolo del plano de datos (RSoc.Protocol.SessionChannel) y la transferencia de
//    ficheros (RSoc.Client.FileTransfer), sin red ni escritorio: sobre MemoryStream.
//
//  COBERTURA
//    1. Round-trip de cada tipo de mensaje (VideoConfig, Input, ClipboardText, VideoFrame,
//       FileOffer/Chunk/End): se serializan, se vuelven a leer y se comparan los campos.
//    2. Transferencia de un fichero de extremo a extremo: se envía con FileTransfer, se
//       reensambla en el receptor y el contenido resultante coincide byte a byte.
// =====================================================================================

using System.Security.Cryptography;
using RSoc.Client;
using RSoc.Protocol;
using Xunit;

namespace RSoc.Tests;

public sealed class SessionProtocolTests
{
    [Fact]
    public async Task Round_trips_every_message_type()
    {
        var ms = new MemoryStream();
        var w = new SessionChannel(ms);

        await w.SendVideoConfigAsync(new VideoConfig(1920, 1080));
        await w.SendInputAsync(new InputMessage(2, 10, 20, 65, 1, 0));
        await w.SendClipboardTextAsync("hola portapapeles áéí");
        await w.SendVideoFrameAsync(new VideoFrame(true, 123456789, [1, 2, 3, 4, 5]));
        await w.SendFileOfferAsync(7, "informe.pdf", 9000);
        await w.SendFileChunkAsync(7, [9, 8, 7, 6], 4);
        await w.SendFileEndAsync(7);

        ms.Position = 0;
        var r = new SessionChannel(ms);

        var m1 = await r.ReadAsync();
        Assert.Equal(SessionMessageType.VideoConfig, m1!.Value.Type);
        var cfg = SessionChannel.ParseVideoConfig(m1.Value.Payload);
        Assert.Equal((1920, 1080), (cfg.Width, cfg.Height));

        var m2 = await r.ReadAsync();
        Assert.Equal(SessionMessageType.Input, m2!.Value.Type);
        var input = SessionChannel.ParseInput(m2.Value.Payload);
        Assert.Equal((2, 10, 20, 65, 1, 0), (input.Kind, input.X, input.Y, input.Code, input.Down, input.Wheel));

        var m3 = await r.ReadAsync();
        Assert.Equal(SessionMessageType.ClipboardText, m3!.Value.Type);
        Assert.Equal("hola portapapeles áéí", SessionChannel.ParseClipboardText(m3.Value.Payload));

        var m4 = await r.ReadAsync();
        Assert.Equal(SessionMessageType.VideoFrame, m4!.Value.Type);
        var vf = SessionChannel.ParseVideoFrame(m4.Value.Payload);
        Assert.True(vf.KeyFrame);
        Assert.Equal(123456789, vf.TimestampQpc);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, vf.Encoded);

        var m5 = await r.ReadAsync();
        var offer = SessionChannel.ParseFileOffer(m5!.Value.Payload);
        Assert.Equal((7, "informe.pdf", 9000L), (offer.Id, offer.Name, offer.Length));

        var m6 = await r.ReadAsync();
        var chunk = SessionChannel.ParseFileChunk(m6!.Value.Payload);
        Assert.Equal(7, chunk.Id);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, chunk.Data);

        var m7 = await r.ReadAsync();
        Assert.Equal(7, SessionChannel.ParseFileEnd(m7!.Value.Payload));
    }

    [Fact]
    public async Task File_transfer_reassembles_identical_bytes()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "rsoc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var srcPath = Path.Combine(tmp, "datos.bin");
        var dstDir = Path.Combine(tmp, "recibidos");
        var original = RandomNumberGenerator.GetBytes(300_000); // > varios chunks
        await File.WriteAllBytesAsync(srcPath, original);

        // Emisor: escribe offer/chunks/end en el buffer.
        var ms = new MemoryStream();
        var sender = new FileTransfer(new SessionChannel(ms), dstDir);
        await sender.SendFileAsync(srcPath);

        // Receptor: lee el buffer y reensambla.
        ms.Position = 0;
        var readCh = new SessionChannel(ms);
        var receiver = new FileTransfer(readCh, dstDir);
        string? receivedPath = null;
        receiver.FileReceived += p => receivedPath = p;

        while (await readCh.ReadAsync() is { } msg)
        {
            switch (msg.Type)
            {
                case SessionMessageType.FileOffer:
                    var o = SessionChannel.ParseFileOffer(msg.Payload);
                    receiver.OnOffer(o.Id, o.Name, o.Length);
                    break;
                case SessionMessageType.FileChunk:
                    var c = SessionChannel.ParseFileChunk(msg.Payload);
                    receiver.OnChunk(c.Id, c.Data);
                    break;
                case SessionMessageType.FileEnd:
                    receiver.OnEnd(SessionChannel.ParseFileEnd(msg.Payload));
                    break;
            }
        }

        Assert.NotNull(receivedPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(receivedPath!));

        Directory.Delete(tmp, recursive: true);
    }
}
