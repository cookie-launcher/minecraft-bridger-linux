using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Bridger.Core;

// Orijinal MainWindow tünel akışının birebir portu:
// SunucuyaOdaKaydetAsync (bağlantıyı AÇIK tutar) + WsDinlemeDongusunuBaslat +
// TunnelMetinIsle + TunnelAcAsync + TunnelVerisiniYereleYaz +
// TunnelYereldenWsYePompalaAsync + TunnelKapat(Bildir) + DurumSorCevaplaAsync.
// Kritik: CREATE_ROOM sonrası soket kapatılmaz; oda listede bu sayede kalır.
public sealed class TunnelIstemcisi : IDisposable
{
    public event Action<string>? OdaKapatildi;

    private TcpClient? _tcp;
    private Stream? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _yazKilidi = new(1, 1);
    private readonly Dictionary<string, TunnelBaglanti> _tuneller = new();
    private readonly object _kilit = new();

    private int _yerelPort = 25565;
    private int _maksOyuncu = 20;

    private sealed class TunnelBaglanti
    {
        public TcpClient? Yerel;
        public NetworkStream? Akim;
        public readonly List<byte[]> Bekleyen = new();
        public int BekleyenBayt;
        public bool Hazir;
    }

    public async Task<string?> YayinlaAsync(
        OdaModel oda, int yerelPort, int maksOyuncu, string? hesapToken,
        string host = "mc.jstudio.com.tr", int port = 80, bool ssl = false,
        int zamanAsimiMs = 12000)
    {
        Kapat();
        _yerelPort = yerelPort;
        _maksOyuncu = maksOyuncu > 0 ? maksOyuncu : 20;
        _cts = new CancellationTokenSource();
        try
        {
            var (tcp, stream) = await WsTransport.BaglanAsync(host, port, ssl,
                new CancellationTokenSource(zamanAsimiMs).Token);
            _tcp = tcp; _ws = stream;
            var paket = new Dictionary<string, object?>
            {
                ["type"] = "CREATE_ROOM",
                ["odaAdi"] = oda.OdaAdi,
                ["kurucu"] = oda.Kurucu,
                ["kategori"] = oda.Kategori,
                ["aktifOyuncu"] = oda.AktifOyuncu,
                ["maksOyuncu"] = oda.MaksOyuncu,
                ["surum"] = oda.Surum,
                ["yerelPort"] = yerelPort,
                ["kategoriNo"] = oda.PortKategorisi,
                ["hesapToken"] = hesapToken ?? "",
                ["hwid"] = LinuxStorage.DonanimKimligi(),
                ["exeHash"] = ApiClient.ProgramHash,
                ["yerelDogrulandi"] = oda.YerelDogrulandi,
                ["dnsAd"] = oda.DnsAdi,
            };
            await GuvenliGonderAsync(0x1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paket)), _cts.Token);
            string? yanit = await MetinOkuPingCevapliAsync(8000, _cts.Token);
            if (string.IsNullOrEmpty(yanit)) { Kapat(); return null; }
            using var doc = JsonDocument.Parse(yanit);
            var kok = doc.RootElement;
            if (kok.TryGetProperty("type", out var tip) && string.Equals(tip.GetString(), "ROOM_ERROR", StringComparison.OrdinalIgnoreCase))
            { Kapat(); return null; }
            if (kok.TryGetProperty("id", out var id) && id.GetString() is string s) oda.Id = s;
            if (kok.TryGetProperty("dnsAdi", out var dn) && dn.ValueKind == JsonValueKind.String) oda.DnsAdi = dn.GetString() ?? oda.DnsAdi;
            if (kok.TryGetProperty("maskeliIp", out var m) && m.GetString() is string maskeli)
            {
                _ = Task.Run(DinlemeDongusu);
                return maskeli;
            }
            Kapat();
            return null;
        }
        catch { Kapat(); return null; }
    }

    private async Task GuvenliGonderAsync(byte opcode, byte[] yuk, CancellationToken t)
    {
        if (_ws == null) throw new InvalidOperationException("ws kapalı");
        await _yazKilidi.WaitAsync(t);
        try { await WsTransport.GonderAsync(_ws, opcode, yuk, t); }
        finally { _yazKilidi.Release(); }
    }

    private async Task<string?> MetinOkuPingCevapliAsync(int ms, CancellationToken t)
    {
        var bitis = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            int kalan = (int)(bitis - DateTime.UtcNow).TotalMilliseconds;
            if (kalan <= 0 || _ws == null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(t);
            cts.CancelAfter(Math.Max(100, kalan));
            var f = await WsTransport.OkuAsync(_ws, cts.Token);
            if (f == null) return null;
            if (f.Opcode == 9) { try { await GuvenliGonderAsync(10, f.Yuk, cts.Token); } catch { return null; } continue; }
            if (f.Opcode == 8) return null;
            if (f.Opcode == 1 && f.Yuk != null) return Encoding.UTF8.GetString(f.Yuk);
        }
    }

    private async Task DinlemeDongusu()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        try
        {
            while (!token.IsCancellationRequested && _ws != null)
            {
                var f = await WsTransport.OkuAsync(_ws, token);
                if (f == null) break;
                if (f.Opcode == 9)
                {
                    try { await GuvenliGonderAsync(10, f.Yuk, token); } catch { break; }
                }
                else if (f.Opcode == 8) break;
                else if (f.Opcode == 2) VeriyiYereleYaz(f.Yuk);
                else if (f.Opcode == 1) MetinIsle(f.Yuk);
            }
        }
        catch { }
    }

    private void MetinIsle(byte[] yuk)
    {
        try
        {
            if (yuk == null || yuk.Length == 0) return;
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(yuk));
            var r = doc.RootElement;
            if (!r.TryGetProperty("type", out var tip)) return;
            string type = (tip.GetString() ?? "").ToUpperInvariant();
            string? tid = r.TryGetProperty("tunnelId", out var t) ? t.GetString() : null;
            if (type == "DURUM_SOR") { _ = Task.Run(DurumSorCevaplaAsync); return; }
            if (string.IsNullOrEmpty(tid)) return;
            switch (type)
            {
                case "TUNNEL_OPEN": _ = Task.Run(() => TunnelAcAsync(tid)); break;
                case "TUNNEL_CLOSE": TunnelKapat(tid); break;
                case "ODA_KAPATILDI":
                    string mesaj = r.TryGetProperty("mesaj", out var m) ? m.GetString() ?? "Odan kapatıldı." : "Odan kapatıldı.";
                    try { OdaKapatildi?.Invoke(mesaj); } catch { }
                    break;
            }
        }
        catch { }
    }

    private async Task TunnelAcAsync(string tunnelId)
    {
        var ot = new TunnelBaglanti();
        lock (_kilit) _tuneller[tunnelId] = ot;
        TcpClient? yerel = null;
        try
        {
            yerel = new TcpClient { NoDelay = true };
            var bag = yerel.ConnectAsync("127.0.0.1", _yerelPort);
            if (await Task.WhenAny(bag, Task.Delay(4000)) != bag || !yerel.Connected)
            {
                try { yerel.Close(); } catch { }
                TunnelKapat(tunnelId);
                await TunnelKapatBildirAsync(tunnelId, "yerel-kapali");
                return;
            }
            ot.Yerel = yerel;
            ot.Akim = yerel.GetStream();
            lock (ot.Bekleyen)
            {
                ot.Hazir = true;
                foreach (var p in ot.Bekleyen)
                    try { ot.Akim.Write(p, 0, p.Length); } catch { break; }
                ot.Bekleyen.Clear();
                ot.BekleyenBayt = 0;
            }
            await PompalaAsync(tunnelId, ot);
        }
        catch
        {
            if (yerel != null) try { yerel.Close(); } catch { }
            TunnelKapat(tunnelId);
            await TunnelKapatBildirAsync(tunnelId, null);
        }
    }

    private void VeriyiYereleYaz(byte[] yuk)
    {
        try
        {
            if (yuk == null || yuk.Length <= 16) return;
            string id = Encoding.ASCII.GetString(yuk, 0, 16);
            TunnelBaglanti? ot;
            lock (_kilit) _tuneller.TryGetValue(id, out ot);
            if (ot == null) return;
            int n = yuk.Length - 16;
            lock (ot.Bekleyen)
            {
                if (ot.Hazir && ot.Akim != null && ot.Yerel != null && ot.Yerel.Connected)
                {
                    try { ot.Akim.Write(yuk, 16, n); return; }
                    catch { TunnelKapat(id); _ = TunnelKapatBildirAsync(id, null); return; }
                }
                if (ot.BekleyenBayt + n > 262144) { TunnelKapat(id); _ = TunnelKapatBildirAsync(id, null); return; }
                var kopya = new byte[n];
                Buffer.BlockCopy(yuk, 16, kopya, 0, n);
                ot.Bekleyen.Add(kopya);
                ot.BekleyenBayt += n;
            }
        }
        catch { }
    }

    private async Task PompalaAsync(string tunnelId, TunnelBaglanti ot)
    {
        try
        {
            byte[] idHam = Encoding.ASCII.GetBytes(tunnelId);
            byte[] id16 = new byte[16];
            Buffer.BlockCopy(idHam, 0, id16, 0, Math.Min(16, idHam.Length));
            byte[] tampon = new byte[8192];
            var token = _cts?.Token ?? CancellationToken.None;
            if (_ws == null || ot.Akim == null)
            {
                TunnelKapat(tunnelId);
                await TunnelKapatBildirAsync(tunnelId, null);
                return;
            }
            while (!token.IsCancellationRequested)
            {
                int okunan;
                try { okunan = await ot.Akim.ReadAsync(tampon, 0, tampon.Length, token); }
                catch { break; }
                if (okunan <= 0) break;
                byte[] cerceve = new byte[16 + okunan];
                Buffer.BlockCopy(id16, 0, cerceve, 0, 16);
                Buffer.BlockCopy(tampon, 0, cerceve, 16, okunan);
                try { await GuvenliGonderAsync(2, cerceve, token); }
                catch { break; }
            }
        }
        catch { }
        TunnelKapat(tunnelId);
        await TunnelKapatBildirAsync(tunnelId, null);
    }

    private void TunnelKapat(string tunnelId)
    {
        try
        {
            TunnelBaglanti? ot;
            lock (_kilit)
            {
                if (!_tuneller.TryGetValue(tunnelId, out ot)) return;
                _tuneller.Remove(tunnelId);
            }
            try { ot.Yerel?.Close(); } catch { }
        }
        catch { }
    }

    private async Task TunnelKapatBildirAsync(string tunnelId, string? neden)
    {
        try
        {
            var paket = new Dictionary<string, object?> { ["type"] = "TUNNEL_CLOSE", ["tunnelId"] = tunnelId };
            if (!string.IsNullOrEmpty(neden)) paket["neden"] = neden;
            var token = _cts?.Token ?? CancellationToken.None;
            await GuvenliGonderAsync(1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paket)), token);
        }
        catch { }
    }

    private async Task DurumSorCevaplaAsync()
    {
        bool acik = false;
        string motd = "", surum = "";
        int protokol = 0, cevrimici = 0, maks = _maksOyuncu > 0 ? _maksOyuncu : 20;
        try
        {
            using var yerel = new TcpClient { NoDelay = true };
            var bag = yerel.ConnectAsync("127.0.0.1", _yerelPort);
            if (await Task.WhenAny(bag, Task.Delay(1500)) == bag && yerel.Connected)
            {
                acik = true;
                using var ns = yerel.GetStream();
                ns.ReadTimeout = 1500; ns.WriteTimeout = 1500;
                byte[] hs = McProtocol.ElSikisma(2, 758);
                byte[] sr = McProtocol.Paket(0, Array.Empty<byte>());
                ns.Write(hs, 0, hs.Length);
                ns.Write(sr, 0, sr.Length);
                await Task.Delay(150);
                byte[] ham = McOku(ns, 60000);
                string? json = McProtocol.StatusJsonAyikla(ham);
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var d = doc.RootElement;
                        if (d.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Object)
                        {
                            if (v.TryGetProperty("name", out var n)) surum = n.GetString() ?? "";
                            if (v.TryGetProperty("protocol", out var p) && p.TryGetInt32(out int pr)) protokol = pr;
                        }
                        if (d.TryGetProperty("players", out var pl) && pl.ValueKind == JsonValueKind.Object)
                        {
                            if (pl.TryGetProperty("online", out var o) && o.TryGetInt32(out int oo)) cevrimici = oo;
                            if (pl.TryGetProperty("max", out var mm) && mm.TryGetInt32(out int mx)) maks = mx;
                        }
                        if (d.TryGetProperty("description", out var desc)) motd = MotdAyikla(desc) ?? "";
                    }
                    catch { }
                }
            }
        }
        catch { acik = false; }
        try
        {
            // NOT: "cevreici" yazımı orijinaldeki gibi (sunucu böyle bekliyor).
            var paket = new Dictionary<string, object?>
            {
                ["type"] = "DURUM_CEVAP", ["acik"] = acik, ["motd"] = motd,
                ["surum"] = surum, ["protokol"] = protokol, ["cevreici"] = cevrimici, ["maks"] = maks,
            };
            var token = _cts?.Token ?? CancellationToken.None;
            await GuvenliGonderAsync(1, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paket)), token);
        }
        catch { }
    }

    private static string? MotdAyikla(JsonElement desc)
    {
        try
        {
            if (desc.ValueKind == JsonValueKind.String) return desc.GetString();
            if (desc.ValueKind == JsonValueKind.Object && desc.TryGetProperty("text", out var t))
                return t.GetString();
            string s = desc.GetRawText();
            int a = s.IndexOf("\"text\"", StringComparison.Ordinal);
            if (a < 0) return null;
            int b = s.IndexOf('"', a + 7); if (b < 0) return null;
            int c = s.IndexOf('"', b + 1); if (c < 0) return null;
            return s.Substring(b + 1, c - b - 1);
        }
        catch { return null; }
    }

    private static byte[] McOku(NetworkStream ns, int azami)
    {
        var buf = new List<byte>();
        var tmp = new byte[8192];
        try
        {
            var bitis = DateTime.UtcNow.AddMilliseconds(2000);
            while (buf.Count == 0 || (ns.DataAvailable && buf.Count < azami))
            {
                if (DateTime.UtcNow > bitis) break;
                if (!ns.DataAvailable) { Thread.Sleep(30); continue; }
                int n = ns.Read(tmp, 0, Math.Min(tmp.Length, azami - buf.Count));
                if (n <= 0) break;
                buf.AddRange(tmp.Take(n));
            }
        }
        catch { }
        return buf.ToArray();
    }

    public void Kapat()
    {
        List<string> idler;
        lock (_kilit) { idler = new List<string>(_tuneller.Keys); _tuneller.Clear(); }
        foreach (var id in idler) { }
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _ws = null; _tcp = null;
    }

    public void Dispose() { Kapat(); _yazKilidi.Dispose(); _cts?.Dispose(); }
}
