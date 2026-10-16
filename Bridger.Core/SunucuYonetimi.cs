using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Bridger.Core;

// Orijinal MainWindow sunucu-kur yardımcılarının Linux portu:
// YerelSunuculariTara, BosYerelPortBul, KlasorJavaExe, SunucuKaydiniOku.
public static class SunucuYonetimi
{
    // Orijinal: Masaüstü/Sunucum/<Ad>. Linux: ~/Masaüstü|~/Desktop/Sunucum yoksa veri dizini.
    public static string SunucumKok()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string d in new[] { Path.Combine(home, "Masaüstü", "Sunucum"), Path.Combine(home, "Desktop", "Sunucum") })
        {
            try { if (Directory.Exists(Path.GetDirectoryName(d))) return d; } catch { }
        }
        return Path.Combine(LinuxStorage.VeriKok, "sunucum");
    }

    public static List<SunucuKaydi> YerelSunuculariTara()
    {
        var liste = new List<SunucuKaydi>();
        string kok;
        try { kok = SunucumKok(); if (!Directory.Exists(kok)) return liste; } catch { return liste; }
        foreach (string dir in Directory.GetDirectories(kok))
        {
            try
            {
                var k = new SunucuKaydi { Ad = Path.GetFileName(dir), Klasor = dir, Port = 25565 };
                string bj = Path.Combine(dir, "bridger.json");
                if (File.Exists(bj))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(bj));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("surum", out var s)) k.Surum = s.GetString() ?? "";
                    if (r.TryGetProperty("motd", out var m)) k.Motd = m.GetString() ?? "";
                    if (r.TryGetProperty("port", out var p)) k.Port = p.GetInt32();
                }
                else
                {
                    string props = Path.Combine(dir, "server.properties");
                    if (File.Exists(props))
                        foreach (string satir in File.ReadAllLines(props))
                        {
                            if (satir.StartsWith("server-port=")) int.TryParse(satir[12..], out k.Port);
                            else if (satir.StartsWith("motd=")) k.Motd = satir[5..];
                        }
                }
                k.Calisiyor = CalisiyorMu(k.Port);
                liste.Add(k);
            }
            catch { }
        }
        return liste;
    }

    private static bool CalisiyorMu(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var t = tcp.ConnectAsync("127.0.0.1", port);
            return t.Wait(800) && tcp.Connected;
        }
        catch { return false; }
    }

    // Orijinal: 20000-65535 arası, 25565 hariç, dinlenen portlar hariç ilk boş.
    public static int BosPortBul()
    {
        var dolu = new HashSet<int> { 25565 };
        foreach (var s in YerelSunuculariTara()) dolu.Add(s.Port);
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                dolu.Add(ep.Port);
        }
        catch { }
        for (int p = 20000; p <= 65535; p++)
        {
            if (dolu.Contains(p)) continue;
            try { using var l = new TcpListener(System.Net.IPAddress.Loopback, p); l.Start(); l.Stop(); return p; }
            catch { }
        }
        throw new InvalidOperationException("Boş port bulunamadı.");
    }

    // Orijinal KlasorJavaExe: klasördeki gömülü java, yoksa "java".
    public static string KlasorJavaExe(string klasor)
    {
        try
        {
            foreach (string aday in new[]
            {
                Path.Combine(klasor, "java", "bin", "java"),
                Path.Combine(klasor, "java", "bin", "java.exe"),
            }) if (File.Exists(aday)) return aday;
        }
        catch { }
        return JavaRuntime.SistemJavasiniBul() ?? "java";
    }

    // --- Linux süreç yardımcıları (orijinal Process.GetProcessById portu) ---

    // bridger.pid dosyasındaki pid (yoksa null).
    public static int? PidOku(string klasor)
    {
        try
        {
            string f = Path.Combine(klasor, "bridger.pid");
            if (!File.Exists(f)) return null;
            return int.TryParse(File.ReadAllText(f).Trim(), out int p) && p > 0 ? p : null;
        }
        catch { return null; }
    }

    // PID canlı mı ve gerçekten o klasördeki paper sunucusu mu? (/proc denetimi)
    public static bool PidCanliJavaMi(int pid, string? beklenenKlasor)
    {
        try
        {
            if (!Directory.Exists($"/proc/{pid}")) return false;
            string cmd = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            if (!cmd.Contains("paper.jar", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(beklenenKlasor))
            {
                var link = new DirectoryInfo($"/proc/{pid}/cwd").ResolveLinkTarget(true);
                if (link != null)
                {
                    string cwd = Path.TrimEndingDirectorySeparator(link.FullName);
                    string istenen = Path.TrimEndingDirectorySeparator(Path.GetFullPath(beklenenKlasor));
                    if (!string.Equals(cwd, istenen, StringComparison.Ordinal)) return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    // Kısa TCP yoklaması (MC durumu değil, sadece dinleme kontrolü).
    public static bool PortDinliyorMu(int port, int zamanAsimiMs = 600)
    {
        try
        {
            using var tcp = new TcpClient();
            var t = tcp.ConnectAsync("127.0.0.1", port);
            return t.Wait(zamanAsimiMs) && tcp.Connected;
        }
        catch { return false; }
    }

    // Klasörde çalışan paper sürecinin pid'i: önce pid dosyası, sonra /proc taraması.
    // (Pid dosyası eskimiş olsa bile gerçek süreç bulunur.)
    public static int? CalisanPidiBul(string klasor)
    {
        int? p = PidOku(klasor);
        if (p.HasValue && PidCanliJavaMi(p.Value, klasor)) return p;
        try
        {
            foreach (string d in Directory.GetDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(d), out int pid)) continue;
                if (PidCanliJavaMi(pid, klasor)) return pid;
            }
        }
        catch { }
        return null;
    }

    // Çalışan sürecin pid'ini bridger.pid'e tazele (attach durumunda da doğru pid kalsın).
    public static void PidDosyasiniTazele(string klasor)
    {
        try
        {
            int? p = CalisanPidiBul(klasor);
            if (p.HasValue) File.WriteAllText(Path.Combine(klasor, "bridger.pid"), p.Value.ToString());
        }
        catch { }
    }

    // Klasördeki sunucu çalışıyor mu? Önce gerçek süreç, sonra port.
    public static bool SunucuCalisiyorMu(string klasor, int port, out int pid)
    {
        pid = 0;
        int? p = CalisanPidiBul(klasor);
        if (p.HasValue) { pid = p.Value; return true; }
        return PortDinliyorMu(port);
    }

    // Orijinal BtnServerDurdur_Click portu: çalışan paper sürecini durdurur,
    // pid dosyasını temizler. Orphan (başka oturumdan kalmış) sunucuları da keser.
    public static string SunucuyuDurdur(string klasor)
    {
        bool oldu = false;
        int? p = CalisanPidiBul(klasor);
        if (p.HasValue)
        {
            try { System.Diagnostics.Process.GetProcessById(p.Value).Kill(true); oldu = true; }
            catch { }
        }
        try { File.Delete(Path.Combine(klasor, "bridger.pid")); } catch { }
        return oldu ? "Durdurma komutu verildi." : "Kayıtlı çalışan işlem bulunamadı (konsol penceresini el ile kapat).";
    }

    // Başlatma hatası sebebini göstermek için logs/latest.log kuyruğu.
    public static string LogKuyruguOku(string klasor, int satirSayisi = 6)
    {
        try
        {
            string loglar = Path.Combine(klasor, "logs");
            string? dosya = null;
            try { dosya = Directory.GetFiles(loglar, "latest.log").FirstOrDefault(); } catch { }
            if (dosya == null)
                try { dosya = Directory.GetFiles(loglar, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(); } catch { }
            if (dosya == null) return "";
            var satirlar = File.ReadLines(dosya).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return string.Join("\n", satirlar.Skip(Math.Max(0, satirlar.Count - satirSayisi)));
        }
        catch { return ""; }
    }
}
