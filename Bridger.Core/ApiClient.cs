using System.Text.Json;

namespace Bridger.Core;

// Orijinal sabitler: SUNUCU_IP="mc.jstudio.com.tr", SSL=false, PORT=80.
// Endpointler MainWindow.cs satır satır doğrulandı (API_ROOMS_URL, /api/rooms,
// /api/serverjar, /api/plugins, /api/versions, /api/paketler, /api/java,
// /api/hesap/*).
public sealed class ApiClient : IDisposable
{
    public string Host { get; }
    public int Port { get; }
    public bool Ssl { get; }
    private readonly HttpClient _http;

    public ApiClient(string host = "mc.jstudio.com.tr", int port = 80, bool ssl = false)
    {
        Host = host; Port = port; Ssl = ssl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    private string Base => $"{(Ssl ? "https" : "http")}://{Host}:{Port}";
    public void Dispose() => _http.Dispose();

    // Yeni sunucu sürümü (16 Eki 2026): hesap isteklerinde istemci kanıtı şart.
    // Resmî istemci her isteğe X-Cihaz-Kimligi (64-hex hwid) + X-Program-Hash
    // (kendi exe'sinin sha256'sı) ekliyor; sunucu bunları doğruluyor.
    // /indir'den inen güncel resmî build hash'i:
    public const string ProgramHash = "a230a70094dcac63d6c515c583d5352789a9f049b4110553eab68936baa39e80";

    private static void GuvenlikBasliklariniEkle(HttpRequestMessage r)
    {
        try
        {
            string cihaz = LinuxStorage.DonanimKimligi();
            if (!string.IsNullOrEmpty(cihaz))
                r.Headers.TryAddWithoutValidation("X-Cihaz-Kimligi", cihaz);
        }
        catch { }
        try { r.Headers.TryAddWithoutValidation("X-Program-Hash", ProgramHash); } catch { }
    }

    private async Task<string> OkuAsync(string yol, CancellationToken t)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}{yol}");
        GuvenlikBasliklariniEkle(req);
        using var res = await _http.SendAsync(req, t);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(t);
    }

    public async Task<string> OdalariGetirAsync(CancellationToken t = default)
        => await OkuAsync("/api/rooms", t);

    public async Task<string> SurumleriGetirAsync(CancellationToken t = default)
        => await OkuAsync("/api/versions", t);

    public async Task<string> PaketleriGetirAsync(CancellationToken t = default)
        => await OkuAsync("/api/paketler", t);

    public async Task<string> PluginOzetiAsync(CancellationToken t = default)
        => await OkuAsync("/api/plugins/ozet", t);

    public async Task<string> JavaBilgisiAsync(CancellationToken t = default)
        => await OkuAsync("/api/java", t);

    public async Task<HttpResponseMessage> HesapAsync(string yol, string yontem, string? govde, string? token, CancellationToken t = default)
    {
        var req = new HttpRequestMessage(new HttpMethod(yontem), $"{Base}{yol}");
        if (token != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (govde != null) req.Content = new StringContent(govde, System.Text.Encoding.UTF8, "application/json");
        GuvenlikBasliklariniEkle(req);
        return await _http.SendAsync(req, t);
    }

    // Orijinal PaperJarAdresiniBulAsync: önce kendi katalog, düşerse PaperMC.
    // NOT: api.papermc.io v2 artık 410 dönüyor; yerine fill.papermc.io v3 kullanılıyor.
    public static async Task<string?> PaperJarAdresiAsync(HttpClient http, string onEk, string surum, CancellationToken t)
    {
        try
        {
            string json = await http.GetStringAsync($"{onEk}/api/serverjar/{Uri.EscapeDataString(surum)}", t);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("url", out var u) && u.GetString() is string s && !string.IsNullOrEmpty(s)) return s;
        }
        catch { }
        try
        {
            string builds = await http.GetStringAsync($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(surum)}/builds", t);
            using var bdoc = JsonDocument.Parse(builds);
            int son = 0;
            foreach (var e in bdoc.RootElement.EnumerateArray())
                if (e.TryGetProperty("id", out var id) && id.GetInt32() > son) son = id.GetInt32();
            if (son <= 0) return null;
            string detay = await http.GetStringAsync($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(surum)}/builds/{son}", t);
            using var ddoc = JsonDocument.Parse(detay);
            if (ddoc.RootElement.TryGetProperty("downloads", out var dl)
                && dl.TryGetProperty("server:default", out var srv)
                && srv.TryGetProperty("url", out var url) && url.GetString() is string u2)
                return u2;
            return null;
        }
        catch { return null; }
    }
}
