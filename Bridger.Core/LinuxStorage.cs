namespace Bridger.Core;

// Windows yollarının Linux karşılıkları.
// Orijinal: %AppData%/MinecraftBridger, Run\ registry autostart,
// tepsi simgesi, MSIX kontrolü (hepsi Windows-only, burada XDG + .desktop).
public static class LinuxStorage
{
    public static string Kok =>
        Platform.KokGecersizKilma ??
        (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? Path.Combine(xdg, "minecraft-bridger")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "minecraft-bridger"));

    public static string VeriKok =>
        Platform.VeriKokGecersizKilma ??
        (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? Path.Combine(xdg, "minecraft-bridger")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "minecraft-bridger"));

    public static string VarsayilanSunucumKlasoru()
    {
        string p = Path.Combine(VeriKok, "sunucum");
        Directory.CreateDirectory(p);
        return p;
    }

    public static string AyarDosyasi() { Directory.CreateDirectory(Kok); return Path.Combine(Kok, "ayarlar.json"); }
    public static string BeniHatirlaDosyasi() => Path.Combine(Kok, "beni-hatirla.json");

    // Registry Run yerine: ~/.config/autostart/minecraft-bridger.desktop
    public static void OtomatikBaslat(bool acik, string execPath)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
        Directory.CreateDirectory(dir);
        string desktop = Path.Combine(dir, "minecraft-bridger.desktop");
        if (!acik) { if (File.Exists(desktop)) File.Delete(desktop); return; }
        File.WriteAllText(desktop,
            $"[Desktop Entry]\nType=Application\nName=MinecraftBridger\nExec={execPath}\nX-GNOME-Autostart-enabled=true\n");
    }

    // Orijinal AyarlariYukle/AyarlariKaydet (ayarlar.json: sistemBaslangici, sunucuOtomatik).
    public static (bool SistemBaslangici, bool SunucuOtomatik) AyarlariYukle()
    {
        try
        {
            string f = AyarDosyasi();
            if (File.Exists(f))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                bool s = r.TryGetProperty("sistemBaslangici", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True;
                bool o = r.TryGetProperty("sunucuOtomatik", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.True;
                return (s, o);
            }
        }
        catch { }
        return (false, false);
    }

    public static void AyarlariKaydet(bool sistemBaslangici, bool sunucuOtomatik)
    {
        try
        {
            File.WriteAllText(AyarDosyasi(),
                System.Text.Json.JsonSerializer.Serialize(new { sistemBaslangici, sunucuOtomatik }));
        }
        catch { }
    }

    // Orijinal DonanimKimligi (yeni sürüm): parçaları topla → süz →
    // sha256("bridger-hwid-v1|" + metin) = 64 haneli küçük harf hex.
    // Sunucu artık yalnız 64-hex kabul ediyor ve cihaz-bağlama yapıyor.
    // Linux'ta kararlı taban: /etc/machine-id (systemd'nin MachineGuid'u).
    // Yedek: ilk MAC adresi, sonra kalıcı makine.id dosyası (orijinaldeki gibi).
    private static string? _makineKimligi;

    public static string DonanimKimligi()
    {
        if (!string.IsNullOrEmpty(_makineKimligi)) return _makineKimligi;
        // Android: uygulama ANDROID_ID'yi taban olarak verir (makine-id okunamaz).
        if (Platform.KimlikTabanGecersizKilma is { Length: > 0 } taban)
        {
            string t = taban.Trim().Replace(" ", "").ToLowerInvariant();
            if (t.Length >= 16) return _makineKimligi = Sha256Hex("bridger-hwid-v1|" + t);
        }
        try
        {
            string metin = File.ReadAllText("/etc/machine-id").Trim().Replace(" ", "").ToLowerInvariant();
            foreach (string cop in new[] { "none", "00000000", "default", "systemserialnumber", "tobefilled", "notspecified", "unknown", "vmware", "null" })
                metin = metin.Replace(cop, "");
            if (metin.Replace("|", "").Replace(":", "").Length >= 16)
                return _makineKimligi = Sha256Hex("bridger-hwid-v1|" + metin);
        }
        catch { }
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var mac = nic.GetPhysicalAddress()?.ToString();
                if (!string.IsNullOrEmpty(mac) && mac.Length >= 8)
                    return _makineKimligi = Sha256Hex("bridger-hwid-v1|" + mac.ToLowerInvariant());
            }
        }
        catch { }
        try
        {
            string f = Path.Combine(Kok, "makine.id");
            if (File.Exists(f))
            {
                string t = File.ReadAllText(f).Trim().ToLowerInvariant();
                if (t.Length == 64) return _makineKimligi = t;
            }
            _makineKimligi = Sha256Hex(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Kok);
            File.WriteAllText(f, _makineKimligi);
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            return _makineKimligi;
        }
        catch { }
        return _makineKimligi = Sha256Hex(Environment.MachineName + Guid.NewGuid().ToString("N"));
    }

    private static string Sha256Hex(string giris)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(giris));
        var sb = new System.Text.StringBuilder(64);
        foreach (byte b in h) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // Orijinal CevrimdisiUuidUret(nick): MD5("OfflinePlayer:"+nick), v3 UUID.
    public static Guid CevrimdisiUuidUret(string nick)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        byte[] h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + nick));
        h[6] = (byte)((h[6] & 0x0F) | 0x30);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return new Guid(h);
    }
}
