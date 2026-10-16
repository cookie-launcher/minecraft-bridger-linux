using System.Text.Json;

namespace Bridger.Core;

// Orijinal MainWindow hesap akışının birebir portu:
// HesapIslemiAsync, HesapOyuncuAdiniEksikseTanimlaAsync, DurumuYukleAsync,
// BeniHatirlaKaydetAsync, OtomatikGirisDeneAsync, EkranNicki.
// Tek fark: DPAPI yerine 0600 dosya (Linux'ta DPAPI yok).
public sealed class HesapOturumu
{
    public string? Token { get; set; }
    public string? Kullanici { get; set; }
    public string? OyuncuAdi { get; set; }
    public string PlanAdi { get; set; } = "Standart";
    public int PlanMaxOyuncu { get; set; } = 6;
    public int PlanMaksSunucu { get; set; } = 1;
    public long PlanBitis { get; set; }
    public int SunucuSayisi { get; set; }
    public string? ProfilMotd { get; set; }
    public string? ProfilSurum { get; set; }

    public string EkranNicki(string varsayilan = "Oyuncu") =>
        !string.IsNullOrEmpty(OyuncuAdi) ? OyuncuAdi
        : !string.IsNullOrEmpty(Kullanici) ? Kullanici
        : string.IsNullOrEmpty(varsayilan) ? "Oyuncu" : varsayilan;

    public string BilgiMetni()
    {
        if (string.IsNullOrEmpty(Kullanici)) return "";
        string ad = !string.IsNullOrEmpty(OyuncuAdi) ? OyuncuAdi : Kullanici;
        string ek = "";
        if (PlanBitis > 0)
        {
            long kalan = PlanBitis - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (kalan <= 0) ek = " (süre bitti)";
            else { long g = kalan / 86400000, s = kalan % 86400000 / 3600000; ek = g > 0 ? $" ({g}g {s}sa kaldı)" : $" ({s}sa kaldı)"; }
        }
        return $"{ad} | {PlanAdi}{ek}";
    }
}

public static class HesapService
{
    // Orijinal istemci-içi doğrulama, birebir.
    public static string? Dogrula(string ad, string sifre, string mc, bool kayitMi)
    {
        ad = (ad ?? "").Trim().ToLowerInvariant();
        if (ad.Length < 3 || ad.Length > 16) return "Kullanıcı adı 3-16 karakter olmalı.";
        foreach (char c in ad)
            if ((c < 'a' || c > 'z') && (c < '0' || c > '9') && c != '_')
                return "Kullanıcı adında yalnız a-z, 0-9 ve _ olur.";
        if ((sifre ?? "").Length < 6) return "Şifre en az 6 karakter olmalı.";
        if (kayitMi)
        {
            mc = (mc ?? "").Trim();
            if (mc.Length < 3 || mc.Length > 16) return "MC oyuncu adı 3-16 karakter olmalı.";
            foreach (char c in mc)
                if ((c < 'a' || c > 'z') && (c < 'A' || c > 'Z') && (c < '0' || c > '9') && c != '_')
                    return "MC adında yalnız A-Z, a-z, 0-9 ve _ olur.";
        }
        return null;
    }

    public sealed record GirisSonucu(bool Ok, string? Token, string? Hata);

    public static async Task<GirisSonucu> GirisVeyaKayitAsync(
        string ad, string sifre, string mc, bool kayitMi, CancellationToken t = default)
    {
        ad = ad.Trim().ToLowerInvariant();
        using var api = new ApiClient();
        var govde = new Dictionary<string, object> { ["kullanici"] = ad, ["sifre"] = sifre };
        if (kayitMi) govde["oyuncuAdi"] = mc.Trim();
        try { govde["hwid"] = LinuxStorage.DonanimKimligi(); } catch { }
        string? json;
        try
        {
            using var res = await api.HesapAsync(kayitMi ? "/api/hesap/kayit" : "/api/hesap/giris", "POST",
                JsonSerializer.Serialize(govde), null, t);
            json = await res.Content.ReadAsStringAsync(t);
        }
        catch { return new GirisSonucu(false, null, "Sunucuya ulaşılamadı."); }
        if (string.IsNullOrEmpty(json)) return new GirisSonucu(false, null, "Sunucuya ulaşılamadı.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out _))
            {
                string? tok = r.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                return new GirisSonucu(true, tok, null);
            }
            string hata = "Giriş başarısız.";
            if (r.TryGetProperty("error", out var e) && e.GetString() is string s) hata = s;
            return new GirisSonucu(false, null, hata);
        }
        catch { return new GirisSonucu(false, null, "Sunucu yanıtı okunamadı."); }
    }

    // Orijinal DurumuYukleAsync: önce eksik oyuncu adını tamamla, sonra durum+abonelik.
    public static async Task DurumuYukleAsync(HesapOturumu o, CancellationToken t = default)
    {
        try
        {
            using var api = new ApiClient();
            // /api/hesap/ben → oyuncuAdi yoksa EkranNick ile tanımla
            try
            {
                using var benRes = await api.HesapAsync("/api/hesap/ben", "GET", null, o.Token, t);
                if (benRes.IsSuccessStatusCode)
                {
                    string ben = await benRes.Content.ReadAsStringAsync(t);
                    using var bdoc = JsonDocument.Parse(ben);
                    bool eksik = !bdoc.RootElement.TryGetProperty("oyuncuAdi", out var oa)
                        || string.IsNullOrEmpty(oa.GetString());
                    if (eksik)
                    {
                        string aday = o.EkranNicki("");
                        if (aday.Length >= 3 && aday.Length <= 16 && aday.All(c =>
                            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
                            await api.HesapAsync("/api/hesap/oyuncu-adi", "POST",
                                JsonSerializer.Serialize(new { oyuncuAdi = aday }), o.Token, t);
                    }
                }
                else if (benRes.StatusCode == System.Net.HttpStatusCode.Unauthorized) { o.Token = null; return; }
            }
            catch { }

            using var api2 = new ApiClient();
            using var res = await api2.HesapAsync("/api/hesap/durum", "GET", null, o.Token, t);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) { o.Token = null; return; }
            if (!res.IsSuccessStatusCode) return;
            string json = await res.Content.ReadAsStringAsync(t);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("oyuncuAdi", out var oad) && !string.IsNullOrEmpty(oad.GetString()))
                o.OyuncuAdi = oad.GetString();
            if (r.TryGetProperty("profil", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("motd", out var m)) o.ProfilMotd = m.GetString();
                if (p.TryGetProperty("surum", out var s)) o.ProfilSurum = s.GetString();
            }
            if (r.TryGetProperty("servers", out var sv) && sv.ValueKind == JsonValueKind.Array)
                o.SunucuSayisi = sv.GetArrayLength();
            if (r.TryGetProperty("abonelik", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                if (a.TryGetProperty("maksOyuncu", out var mo) && mo.TryGetInt32(out int m) && m is >= 1 and <= 100) o.PlanMaxOyuncu = m;
                if (a.TryGetProperty("plan", out var pl)) o.PlanAdi = pl.GetString() ?? o.PlanAdi;
                if (a.TryGetProperty("maksSunucu", out var ms) && ms.TryGetInt32(out int k) && k is >= 1 and <= 100) o.PlanMaksSunucu = k;
                if (a.TryGetProperty("bitis", out var b) && b.TryGetInt64(out long bit)) o.PlanBitis = bit;
            }
        }
        catch { }
    }

    // Orijinal BeniHatirlaKaydetAsync: sunucudan rememberToken al, "kullanici\ntoken" yaz.
    public static async Task BeniHatirlaKaydetAsync(HesapOturumu o)
    {
        try
        {
            if (string.IsNullOrEmpty(o.Token) || string.IsNullOrEmpty(o.Kullanici)) return;
            using var api = new ApiClient();
            using var res = await api.HesapAsync("/api/hesap/hatirla-olustur", "POST", "{}", o.Token);
            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("rememberToken", out var rt) && rt.GetString() is string s)
            {
                string f = LinuxStorage.BeniHatirlaDosyasi();
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                await File.WriteAllTextAsync(f, o.Kullanici + "\n" + s);
                try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
        }
        catch { }
    }

    // Orijinal OtomatikGirisDeneAsync. Başarılıysa oturumu doldurur, değilse dosyayı siler.
    public static async Task<bool> OtomatikGirisDeneAsync(HesapOturumu o)
    {
        try
        {
            string f = LinuxStorage.BeniHatirlaDosyasi();
            if (!File.Exists(f)) return false;
            string[] parca = (await File.ReadAllTextAsync(f)).Split('\n', 2);
            if (parca.Length < 2 || string.IsNullOrEmpty(parca[1])) return false;
            using var api = new ApiClient();
            using var res = await api.HesapAsync("/api/hesap/hatirla-giris", "POST",
                JsonSerializer.Serialize(new { rememberToken = parca[1].Trim(), hwid = LinuxStorage.DonanimKimligi() }), null);
            string json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out _) && r.TryGetProperty("token", out var tk) && tk.GetString() is string s)
            {
                o.Token = s; o.Kullanici = parca[0];
                if (r.TryGetProperty("rememberToken", out var rt) && rt.GetString() is string s2)
                    await File.WriteAllTextAsync(f, parca[0] + "\n" + s2);
                await DurumuYukleAsync(o);
                return true;
            }
            try { File.Delete(f); } catch { }
        }
        catch { }
        return false;
    }

    // Orijinal KatilimBildirAsync: yayın başarısının ardından bu bilgisayarı
    // "güvenli sahip" olarak bildirir. Sessizce başarısız olabilir.
    public static async Task<bool> KatilimBildirAsync(HesapOturumu o, CancellationToken t = default)
    {
        try
        {
            if (string.IsNullOrEmpty(o.Token)) return false;
            string hwid = LinuxStorage.DonanimKimligi();
            if (string.IsNullOrEmpty(hwid)) return false;
            using var api = new ApiClient();
            using var res = await api.HesapAsync("/api/hesap/katilim-bildirimi", "POST",
                JsonSerializer.Serialize(new { hwid }), o.Token, t);
            if (!res.IsSuccessStatusCode) return false;
            string json = await res.Content.ReadAsStringAsync(t);
            return json.Contains("\"ok\"");
        }
        catch { return false; }
    }
}
