using System.Text;
using System.Text.Json;

namespace Bridger.Core;

// Oda listesi + yayınla (CREATE_ROOM) + katıl sinyali.
// Orijinal MainWindow metotlarından port:
// OdalariSunucudanYukleAsync, JsonOdaListesiCozumle, SozlukDegeriAl,
// SunucuyaOdaKaydetAsync, SunucuyaKatilmaSinyaliGonderAsync.
public static class OdaService
{
    public static async Task<List<OdaModel>> OdalariGetirAsync(
        string host = "mc.jstudio.com.tr", int port = 80, bool ssl = false, CancellationToken t = default)
    {
        string on = $"{(ssl ? "https" : "http")}://{host}:{port}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        string? json = null;
        try { json = await http.GetStringAsync($"{on}/api/rooms", t); }
        catch { json = await http.GetStringAsync($"https://{host}/api/rooms", t); }
        return OdaListesiniCoz(json);
    }

    public static List<OdaModel> OdaListesiniCoz(string? json)
    {
        var liste = new List<OdaModel>();
        if (string.IsNullOrWhiteSpace(json)) return liste;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement kok = doc.RootElement;
            if (kok.ValueKind == JsonDocument.Parse("{}").RootElement.ValueKind) { }
            IEnumerable<JsonElement> ogeler = kok.ValueKind == JsonValueKind.Array
                ? kok.EnumerateArray()
                : kok.TryGetProperty("rooms", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray()
                : Enumerable.Empty<JsonElement>();
            foreach (var e in ogeler)
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                liste.Add(new OdaModel
                {
                    Id = Deger(e, "id", "Id", Guid.NewGuid().ToString("N")[..8]),
                    OdaAdi = Deger(e, "odaAdi", "OdaAdi", "Minecraft Odası"),
                    Kurucu = Deger(e, "kurucu", "Kurucu", "Oyuncu"),
                    Kategori = Deger(e, "kategori", "Kategori", "Survival"),
                    AktifOyuncu = Sayi(e, "aktifOyuncu", "AktifOyuncu", 0),
                    MaksOyuncu = Sayi(e, "maksOyuncu", "MaksOyuncu", 8),
                    Surum = Deger(e, "surum", "Surum", ""),
                    MaskeliIp = Deger(e, "maskeliIp", "MaskeliIp", ""),
                    HerkeseAcik = Bool(e, "herkeseAcik", "HerkeseAcik", true),
                    YerelPort = Sayi(e, "yerelPort", "YerelPort", 25565),
                    PortKategorisi = Sayi(e, "kategoriNo", "KategoriNo", 2),
                    DnsAdi = Deger(e, "dnsAdi", "DnsAdi", ""),
                    OdaDurumu = Deger(e, "durum", "Durum", "acik"),
                    Sahip = Deger(e, "sahip", "Sahip", ""),
                });
            }
        }
        catch { }
        return liste;
    }

    private static string Deger(JsonElement e, string camel, string pascal, string varsayilan)
    {
        if (e.TryGetProperty(camel, out var a) && a.ValueKind == JsonValueKind.String) return a.GetString() ?? varsayilan;
        if (e.TryGetProperty(pascal, out var b) && b.ValueKind == JsonValueKind.String) return b.GetString() ?? varsayilan;
        return varsayilan;
    }

    private static int Sayi(JsonElement e, string camel, string pascal, int varsayilan)
    {
        if (e.TryGetProperty(camel, out var a) && a.TryGetInt32(out int v)) return v;
        if (e.TryGetProperty(pascal, out var b) && b.TryGetInt32(out int w)) return w;
        return varsayilan;
    }

    private static bool Bool(JsonElement e, string camel, string pascal, bool varsayilan)
    {
        if (e.TryGetProperty(camel, out var a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)) return a.GetBoolean();
        if (e.TryGetProperty(pascal, out var b) && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False)) return b.GetBoolean();
        return varsayilan;
    }

    // Oda yayınla: WS handshake + CREATE_ROOM + cevapta maskeliIp.
    // Orijinal SunucuyaOdaKaydetAsync birebir.
    public static async Task<string?> OdaYayinlaAsync(
        OdaModel oda, int yerelPort, string? hesapToken,
        string host = "mc.jstudio.com.tr", int port = 80, bool ssl = false,
        int zamanAsimiMs = 12000)
    {
        try
        {
        using var cts = new CancellationTokenSource(zamanAsimiMs);
        var (tcp, stream) = await WsTransport.BaglanAsync(host, port, ssl, cts.Token);
        try
        {
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
            await WsTransport.MetinGonderAsync(stream, JsonSerializer.Serialize(paket), cts.Token);
            string? yanit = await MetinOkuPingCevapliAsync(stream, 8000, cts.Token);
            if (string.IsNullOrEmpty(yanit)) return null;
            using var doc = JsonDocument.Parse(yanit);
            var kok = doc.RootElement;
            if (kok.TryGetProperty("type", out var tip) && string.Equals(tip.GetString(), "ROOM_ERROR", StringComparison.OrdinalIgnoreCase))
                return null;
            if (kok.TryGetProperty("id", out var id)) oda.Id = id.GetString() ?? oda.Id;
            if (kok.TryGetProperty("dnsAdi", out var dn) && dn.ValueKind == JsonValueKind.String) oda.DnsAdi = dn.GetString() ?? oda.DnsAdi;
            if (kok.TryGetProperty("maskeliIp", out var m)) return m.GetString();
            return null;
        }
        finally { try { tcp.Close(); } catch { } }
        }
        catch { return null; }
    }

    // Orijinal SunucuyaKatilmaSinyaliGonderAsync: JOIN_ROOM gönderip
    // katılım için taze maskeliIp alır; başarısızsa null döner
    // (çağıran eski MaskeliIp ile devam eder).
    public static async Task<string?> OdayaKatilSinyaliAsync(
        OdaModel oda, string oyuncuAdi,
        string host = "mc.jstudio.com.tr", int port = 80, bool ssl = false,
        int zamanAsimiMs = 8000)
    {
        if (oda == null || string.IsNullOrEmpty(oda.Id)) return null;
        try
        {
            using var cts = new CancellationTokenSource(zamanAsimiMs);
            var (tcp, stream) = await WsTransport.BaglanAsync(host, port, ssl, cts.Token);
            try
            {
                var paket = new Dictionary<string, object?>
                {
                    ["type"] = "JOIN_ROOM",
                    ["roomId"] = oda.Id,
                    ["oyuncuAdi"] = oyuncuAdi,
                };
                await WsTransport.MetinGonderAsync(stream, JsonSerializer.Serialize(paket), cts.Token);
                string? yanit = await MetinOkuPingCevapliAsync(stream, zamanAsimiMs, cts.Token);
                if (string.IsNullOrEmpty(yanit)) return null;
                using var doc = JsonDocument.Parse(yanit);
                if (doc.RootElement.TryGetProperty("maskeliIp", out var m)) return m.GetString();
                return null;
            }
            finally { try { tcp.Close(); } catch { } }
        }
        catch { return null; }
    }

    private static async Task<string?> MetinOkuPingCevapliAsync(Stream s, int ms, CancellationToken t)
    {
        var bitis = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            int kalan = (int)(bitis - DateTime.UtcNow).TotalMilliseconds;
            if (kalan <= 0) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(t);
            cts.CancelAfter(Math.Max(100, kalan));
            var f = await WsTransport.OkuAsync(s, cts.Token);
            if (f == null) return null;
            if (f.Opcode == 9) { await WsTransport.GonderAsync(s, 10, f.Yuk, cts.Token); continue; }
            if (f.Opcode == 8) return null;
            if (f.Opcode == 1 && f.Yuk != null) return Encoding.UTF8.GetString(f.Yuk);
        }
    }
}
