using System.Net.Sockets;
using System.Text;

namespace Bridger.Core;

// MainWindow.cs WebSocketFrameGonderAsync/WebSocketFrameOkuAsync birebir portu.
// Orijinal: ham TCP + el yapımı WS handshake (SUNUCU_IP:PORT, path "/").
// Linux'ta da ham soket — System.Net.WebSockets'e bağımlı değil, davranış aynı.
public static class WsTransport
{
    private static readonly Random Rnd = new();

    public static async Task<(TcpClient Tcp, Stream Stream)> BaglanAsync(
        string host, int port, bool ssl, CancellationToken token)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, token);
        Stream s = tcp.GetStream();
        string scheme = ssl ? "https" : "http";
        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        string el = $"GET / HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        byte[] b = Encoding.ASCII.GetBytes(el);
        await s.WriteAsync(b, 0, b.Length, token);
        // Orijinal handshake cevabını tam parse etmez; ilk 1KB'ı tüketir.
        var tmp = new byte[1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(8000);
        await s.ReadAsync(tmp, 0, tmp.Length, cts.Token);
        _ = scheme;
        return (tcp, s);
    }

    // Orijinal WebSocketFrameGonderAsync: FIN+opcode, maskeli, max 64KB.
    public static async Task GonderAsync(Stream stream, byte opcode, byte[]? yuk, CancellationToken token)
    {
        yuk ??= Array.Empty<byte>();
        if (yuk.Length > 65535) throw new InvalidOperationException("WebSocket yuku 64KB sinirini asiyor.");
        byte[] mask = new byte[4];
        lock (Rnd) Rnd.NextBytes(mask);
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | (opcode & 0xF)));
        if (yuk.Length < 126) ms.WriteByte((byte)(0x80 | yuk.Length));
        else { ms.WriteByte(254); ms.WriteByte((byte)((yuk.Length >> 8) & 0xFF)); ms.WriteByte((byte)(yuk.Length & 0xFF)); }
        ms.Write(mask, 0, 4);
        for (int i = 0; i < yuk.Length; i++) ms.WriteByte((byte)(yuk[i] ^ mask[i % 4]));
        byte[] cerceve = ms.ToArray();
        await stream.WriteAsync(cerceve, 0, cerceve.Length, token);
    }

    public static Task MetinGonderAsync(Stream s, string metin, CancellationToken t)
        => GonderAsync(s, 0x1, Encoding.UTF8.GetBytes(metin), t);

    // Orijinal WebSocketFrameOkuAsync: sunucu->istemci maskesiz frame okur.
    public static async Task<WsFrame?> OkuAsync(Stream stream, CancellationToken token)
    {
        byte[]? bas = await TamOkuAsync(stream, 2, token);
        if (bas == null) return null;
        bool fin = (bas[0] & 0x80) != 0;
        byte opcode = (byte)(bas[0] & 0x0F);
        bool maskeli = (bas[1] & 0x80) != 0;
        ulong uzunluk = (ulong)(bas[1] & 0x7F);
        if (uzunluk == 126) { byte[]? e = await TamOkuAsync(stream, 2, token); if (e == null) return null; uzunluk = (ulong)((e[0] << 8) | e[1]); }
        else if (uzunluk == 127) { byte[]? e = await TamOkuAsync(stream, 8, token); if (e == null) return null; uzunluk = 0; for (int i = 0; i < 8; i++) uzunluk = (uzunluk << 8) | e[i]; }
        byte[]? mask = maskeli ? await TamOkuAsync(stream, 4, token) : null;
        if (maskeli && mask == null) return null;
        if (uzunluk > 16 * 1024 * 1024) throw new InvalidOperationException("WS cercevesi çok büyük.");
        byte[]? yuk = uzunluk == 0 ? Array.Empty<byte>() : await TamOkuAsync(stream, (int)uzunluk, token);
        if (yuk == null) return null;
        if (maskeli) for (ulong i = 0; i < uzunluk; i++) yuk[i] ^= mask![i % 4];
        _ = fin;
        return new WsFrame { Opcode = opcode, Yuk = yuk };
    }

    private static async Task<byte[]?> TamOkuAsync(Stream s, int n, CancellationToken t)
    {
        var buf = new byte[n]; int o = 0;
        while (o < n)
        {
            int r = await s.ReadAsync(buf, o, n - o, t);
            if (r <= 0) return null;
            o += r;
        }
        return buf;
    }
}
