using System.Diagnostics;

namespace Bridger.Core;

// Sunucu başlatma/durdurma/bağlanma akışının ortak noktası — hem masaüstü
// arayüzü hem (gelecekte) Android başlığı bunu kullanır:
//   BaslaAsync : çalışan sunucu varsa ona bağlanır (attach), yoksa başlatır.
//   Durdur     : çalışan paper sürecini keser (orphan'lar dahil).
//   CevapBekleAsync: MC durum yanıtı gelene kadar bekler; süreç/JVM öldüyse erken çıkar.
// Android'de süreç yoktur: Platform.SunucuBaslatici (gömülü JVM) devralır.
public static class SunucuCalistirici
{
    public enum BaslaSonucu { Baslatildi, ZatenCalisiyor, Hata }

    private static Process? _proc;        // masaüstü: bizim başlattığımız süreç
    private static ISunucuIslemi? _islem; // android: gömülü JVM işlemi
    private static int? _bagliPid;        // masaüstü: başka oturumdan bağlanılan pid

    public static bool Masaustu => !Platform.GomuluJvm;

    // Çalışan sunucuya bağlan (ikinci kopya world/session.lock dosyalarını bozar).
    public static void Baglan(string klasor, int port, int pid)
    {
        _bagliPid = pid;
        SunucuYonetimi.PidDosyasiniTazele(klasor);
    }

    public static async Task<BaslaSonucu> BaslaAsync(string klasor, int xmxMb = 2048,
        Action<string>? cikti = null, Action<int>? bitti = null)
    {
        // 1) Zaten çalışan sunucuya bağlan.
        if (SunucuYonetimi.SunucuCalisiyorMu(klasor, SunucuPortu(klasor), out int pid))
        {
            Baglan(klasor, SunucuPortu(klasor), pid);
            return BaslaSonucu.ZatenCalisiyor;
        }

        // 2) Android: gömülü JVM sağlayıcısı.
        var sag = Platform.SunucuBaslatici;
        if (sag != null)
        {
            _islem = await sag.BaslatAsync(klasor, "paper.jar", xmxMb, cikti, bitti);
            return _islem != null ? BaslaSonucu.Baslatildi : BaslaSonucu.Hata;
        }

        // 3) Masaüstü: java süreci.
        try
        {
            string javaExe = SunucuYonetimi.KlasorJavaExe(klasor);
            var psi = new ProcessStartInfo(javaExe, $"-Xmx{xmxMb}M -jar paper.jar nogui")
            {
                WorkingDirectory = klasor,
                UseShellExecute = false,
            };
            bool pompala = cikti != null;
            if (pompala)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardInput = true;
            }
            _proc = Process.Start(psi);
            if (_proc == null) return BaslaSonucu.Hata;
            if (pompala) CiktiPompala(_proc, cikti!, bitti);
            try { File.WriteAllText(Path.Combine(klasor, "bridger.pid"), _proc.Id.ToString()); } catch { }
            return BaslaSonucu.Baslatildi;
        }
        catch { return BaslaSonucu.Hata; }
    }

    private static void CiktiPompala(Process p, Action<string> cikti, Action<int>? bitti)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? satir = await p.StandardOutput.ReadLineAsync();
                    if (satir == null) break;
                    try { cikti(satir); } catch { }
                }
            }
            catch { }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? satir = await p.StandardError.ReadLineAsync();
                    if (satir == null) break;
                    try { cikti(satir); } catch { }
                }
            }
            catch { }
        });
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => { try { bitti?.Invoke(p.ExitCode); } catch { } };
        }
        catch { }
    }

    // Çalışan sunucuyu durdur (Android: nazik "stop"; masaüstü: süreç kesme).
    public static string Durdur(string klasor)
    {
        string mesaj = SunucuYonetimi.SunucuyuDurdur(klasor);
        _proc = null;
        _bagliPid = null;
        _islem = null;
        return mesaj;
    }

    // Sunucu konsoluna komut yaz (Android "stop" dahil; masaüstünde yalnız
    // çıktı pompası açıkken çalışır).
    public static bool KomutGonder(string satir)
    {
        try
        {
            if (_islem != null) { _islem.KomutGonder(satir); return true; }
            if (_proc is { HasExited: false, StartInfo.RedirectStandardInput: true })
            {
                _proc.StandardInput.WriteLine(satir);
                return true;
            }
        }
        catch { }
        return false;
    }

    // MC cevabı gelene kadar bekler (varsayılan 40×3 sn); süreç/JVM öldüyse erken çıkar.
    public static async Task<bool> CevapBekleAsync(int port, int adim = 40, int adimMs = 3000)
    {
        for (int i = 0; i < adim; i++)
        {
            await Task.Delay(adimMs);
            if (_proc is { HasExited: true }) return false;
            if (_bagliPid is > 0 && Masaustu && !Directory.Exists($"/proc/{_bagliPid.Value}")) return false;
            if (_islem is { Calisiyor: false }) return false;
            var (m, _) = await McProtocol.YerelDurumSorAsync("127.0.0.1", port, zamanAsimiMs: 1000);
            if (m != null) return true;
        }
        return false;
    }

    private static int SunucuPortu(string klasor)
    {
        try
        {
            foreach (string satir in File.ReadAllLines(Path.Combine(klasor, "server.properties")))
                if (satir.StartsWith("server-port=") && int.TryParse(satir[12..], out int p)) return p;
        }
        catch { }
        return 25565;
    }
}
