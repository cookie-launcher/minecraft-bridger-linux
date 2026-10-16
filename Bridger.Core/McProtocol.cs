using System.Text;

namespace Bridger.Core;

// MainWindow.cs'den birebir port: McElSikisma, McPaket, McVarintYaz/Oku,
// McStatusJsonAyikla. Yorumlardaki orijinal satır aralıkları korundu.
public static class McProtocol
{
    public const int VarsayilanPort = 25565;

    // Orijinal: McElSikisma(int sonrakiDurum, int protokol)
    public static byte[] ElSikisma(int sonrakiDurum, int protokol)
    {
        byte[] host = Encoding.UTF8.GetBytes("localhost");
        using var ms = new MemoryStream();
        VarintYaz(ms, 0);
        VarintYaz(ms, protokol);
        VarintYaz(ms, host.Length);
        ms.Write(host, 0, host.Length);
        ms.WriteByte(99); ms.WriteByte(221); // 25565 LE
        VarintYaz(ms, sonrakiDurum);
        byte[] ic = ms.ToArray();
        using var dis = new MemoryStream();
        VarintYaz(dis, ic.Length);
        dis.Write(ic, 0, ic.Length);
        return dis.ToArray();
    }

    // Orijinal: McPaket(int id, byte[] govde)
    public static byte[] Paket(int id, byte[]? govde)
    {
        using var ms = new MemoryStream();
        VarintYaz(ms, id);
        if (govde is { Length: > 0 }) ms.Write(govde, 0, govde.Length);
        byte[] ic = ms.ToArray();
        using var dis = new MemoryStream();
        VarintYaz(dis, ic.Length);
        dis.Write(ic, 0, ic.Length);
        return dis.ToArray();
    }

    // Orijinal: McVarintYaz(Stream s, int deger)
    public static void VarintYaz(Stream s, int deger)
    {
        uint n = (uint)deger;
        do
        {
            byte b = (byte)(n & 0x7F);
            n >>= 7;
            if (n != 0) b |= 0x80;
            s.WriteByte(b);
        } while (n != 0);
    }

    // Orijinal: McVarintOku(byte[] buf, ref int o) -> -1 = eksik
    public static int VarintOku(byte[] buf, ref int o)
    {
        int val = 0, shift = 0, p = o;
        while (p < buf.Length && shift < 35)
        {
            byte b = buf[p++];
            val |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) { o = p; return val; }
            shift += 7;
        }
        return -1;
    }

    // Orijinal: McStatusJsonAyikla — ham paketten ilk tam {...} JSON'u çeker.
    public static string? StatusJsonAyikla(byte[] ham)
    {
        if (ham == null || ham.Length < 3) return null;
        int bas = -1, bit = -1, derinlik = 0;
        for (int i = 0; i < ham.Length; i++)
        {
            if (ham[i] == 123) { if (bas < 0) bas = i; derinlik++; }
            else if (ham[i] == 125)
            {
                derinlik--;
                if (bas >= 0 && derinlik == 0) { bit = i; break; }
            }
        }
        if (bas < 0 || bit <= bas) return null;
        return Encoding.UTF8.GetString(ham, bas, bit - bas + 1);
    }

    // Yerel sunucuya status sorusu sorup MOTD + oyuncu sayısını döndürür.
    // Orijinal YerelMcPortSinaAsync gibi: ulaşılamazsa exception değil (null, null).
    public static async Task<(string? Motd, string? Json)> YerelDurumSorAsync(
        string host, int port, int protokol = 758, int zamanAsimiMs = 4000)
    {
        try
        {
        using var cts = new CancellationTokenSource(zamanAsimiMs);
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(host, port, cts.Token);
        using var stream = tcp.GetStream();
        stream.WriteTimeout = zamanAsimiMs; stream.ReadTimeout = zamanAsimiMs;
        byte[] el = ElSikisma(1, protokol);
        await stream.WriteAsync(el, 0, el.Length, cts.Token);
        byte[] istek = Paket(0, Array.Empty<byte>());
        await stream.WriteAsync(istek, 0, istek.Length, cts.Token);

        var buf = new List<byte>();
        var tmp = new byte[4096];
        using var bekle = new CancellationTokenSource(zamanAsimiMs);
        while (!bekle.IsCancellationRequested)
        {
            if (stream.DataAvailable || buf.Count == 0)
            {
                int n = await stream.ReadAsync(tmp, 0, tmp.Length, bekle.Token);
                if (n <= 0) break;
                buf.AddRange(tmp.Take(n));
                string? j = StatusJsonAyikla(buf.ToArray());
                if (j != null) return (MotdAyikla(j), j);
                if (buf.Count > 65536) break;
            }
            else await Task.Delay(50, bekle.Token);
        }
        return (null, StatusJsonAyikla(buf.ToArray()));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException
            or OperationCanceledException or IOException or TimeoutException)
        {
            return (null, null);
        }
    }

    private static string? MotdAyikla(string json)
    {
        // Basit: "text":"..." ilk eşleşmesi (orijinal McMotdAyikla karşılığı)
        int i = json.IndexOf("\"text\"", StringComparison.Ordinal);
        if (i < 0) return null;
        int a = json.IndexOf('"', i + 7); if (a < 0) return null;
        int b = json.IndexOf('"', a + 1); if (b < 0) return null;
        return json.Substring(a + 1, b - a - 1);
    }
}
