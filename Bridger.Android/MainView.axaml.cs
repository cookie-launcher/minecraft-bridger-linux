using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bridger.Core;

namespace Bridger.Android;

// Spike ekranı: gömülü JVM'i Paper ile başlatıp konsolu akıtır. Uçtan uca
// dokunmasız deneme: sunucu dosyaları APK içinden çıkarılır, sunucu otomatik
// başlar, loglar host'tan okunabilsin diye izinler açılır. Başarılıysa gerçek
// mobil arayüz bunun üzerine kurulur.
public partial class MainView : UserControl
{
    private readonly GomuluJvmBaslatici? _sag;
    private ISunucuIslemi? _islem;
    private readonly List<string> _satirlar = new();

    public MainView()
    {
        InitializeComponent();
        _sag = Platform.SunucuBaslatici as GomuluJvmBaslatici;
        Loaded += (_, _) => _ = OtomatikHazirlaAsync();
    }

    private string SunucuKlasoru()
        => Path.Combine(LinuxStorage.VeriKok, "sunucum", "test");

    // Spike teşhisi: masaüstü kullanıcısı uygulama veri ağacındaki dosyaları
    // (konsol logu vb.) okuyabilsin diye "diğer" izinlerini açar. Yalnızca
    // test içindir. Android uygulamalarının umask'ı 0077 olduğundan düzenli
    // olarak yeniden uygulanır.
    private const UnixFileMode DizinAcik = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    private const UnixFileMode DizinGecis = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.UserExecute | UnixFileMode.OtherExecute;

    private const UnixFileMode DosyaAcik = UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    private static void IzinleriAc()
    {
        try
        {
            string? files = global::Android.App.Application.Context.FilesDir?.AbsolutePath;
            if (files == null) return;
            string? dataDir = Path.GetDirectoryName(files);
            foreach (string? d in new[] { dataDir, files })
            {
                if (d == null) continue;
                try { File.SetUnixFileMode(d, DizinGecis); } catch { }
            }

            // JRE: derinini açmaya gerek yok; yalnızca "release" işareti
            // host'tan görülebilsin (jre21'e geçiş izni + release okunur).
            string jre = Path.Combine(files, "jre21");
            try { if (Directory.Exists(jre)) File.SetUnixFileMode(jre, DizinGecis); } catch { }
            try
            {
                string rel = Path.Combine(jre, "release");
                if (File.Exists(rel)) File.SetUnixFileMode(rel,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            }
            catch { }

            string veri = Path.Combine(files, "veri");
            if (!Directory.Exists(veri)) return;
            try { File.SetUnixFileMode(veri, DizinAcik); } catch { }
            foreach (string yol in Directory.EnumerateFileSystemEntries(
                         veri, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (Directory.Exists(yol))
                        File.SetUnixFileMode(yol, DizinAcik);
                    else
                        File.SetUnixFileMode(yol, DosyaAcik);
                }
                catch { }
            }
        }
        catch { }
    }

    // 1) izinler (hemen — host'tan izlenebilsin) 2) JRE çıkar
    // 3) sunucu dosyaları 4) otomatik başlat (spike)
    private async Task OtomatikHazirlaAsync()
    {
        IzinleriAc();

        try
        {
            if (!JreKurucu.Hazir)
            {
                TxtDurum.Text = "JRE çıkarılıyor (ilk açılış, 1-2 dk)...";
                bool tamam = await JreKurucu.GerekirseCikarAsync(
                    m => Dispatcher.UIThread.Post(() => TxtDurum.Text = m));
                if (!tamam)
                {
                    TxtDurum.Text = "JRE çıkarılamadı (assets eksik olabilir).";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BridgerSpike", "JRE hatası: " + ex);
            TxtDurum.Text = "JRE hatası: " + ex.Message;
            return;
        }

        try
        {
            await SunucuDosyalariniHazirlaAsync();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BridgerSpike", "Dosya hazırlama hatası: " + ex);
            TxtDurum.Text = "Dosya hazırlama hatası: " + ex.Message;
            return;
        }

        await SunucuyuBaslatAsync();
    }

    // Sunucu dosyaları (paper.jar + eula + properties) APK içinde (assets/spike)
    // paketlidir; ilk açılışta veri dizinine çıkarılır.
    private async Task SunucuDosyalariniHazirlaAsync()
    {
        string klasor = SunucuKlasoru();
        Directory.CreateDirectory(klasor);
        if (!File.Exists(Path.Combine(klasor, "paper.jar")))
        {
            TxtDurum.Text = "Sunucu dosyaları APK içinden çıkarılıyor...";
            var ctx = global::Android.App.Application.Context;
            await Task.Run(() =>
            {
                foreach (string ad in new[] { "paper.jar", "eula.txt", "server.properties" })
                {
                    using var giris = ctx.Assets!.Open("spike/" + ad);
                    using var cikti = File.Create(Path.Combine(klasor, ad));
                    giris.CopyTo(cikti);
                }
            });
        }
        IzinleriAc();
    }

    private async void BtnBaslat_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
        => await SunucuyuBaslatAsync();

    private async Task SunucuyuBaslatAsync()
    {
        if (_sag == null) { TxtDurum.Text = "Gömülü JVM sağlayıcısı yok."; return; }
        if (!JreKurucu.Hazir) { TxtDurum.Text = "JRE hazır değil."; return; }

        string klasor = SunucuKlasoru();
        if (!File.Exists(Path.Combine(klasor, "paper.jar")))
        {
            TxtDurum.Text = "paper.jar yok (assets/spike eksik olabilir).";
            return;
        }

        BtnBaslat.IsEnabled = false;
        TxtDurum.Text = "JVM başlatılıyor (dlopen + JNI)...";
        try
        {
            _islem = await _sag.BaslatAsync(klasor, "paper.jar", 1024,
                satir => Dispatcher.UIThread.Post(() => KonsolEkle(satir)),
                kod => Dispatcher.UIThread.Post(() => TxtDurum.Text = $"Sunucu kapandı (kod {kod})."));
            if (_islem == null)
            {
                global::Android.Util.Log.Error("BridgerSpike",
                    "JVM başlatılamadı: " + (_sag.Hata ?? "bilinmeyen hata"));
                TxtDurum.Text = "Başlatılamadı: " + (_sag.Hata ?? "bilinmeyen hata");
                BtnBaslat.IsEnabled = true;
                return;
            }
            global::Android.Util.Log.Info("BridgerSpike", "JVM başlatıldı (pid?)");
            BtnDurdur.IsEnabled = true;
            TxtDurum.Text = "JVM çalışıyor. Port yoklanıyor (:25565)...";

            // Uygulama çalışırken ürettiği dosyaların izinlerini düzenli aç
            // (umask 0077; konsol logu + dünya kayıtları host'tan okunabilsin).
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 90; i++) { IzinleriAc(); await Task.Delay(10_000); }
            });
            _ = PortYoklaAsync();
        }
        catch (Exception ex)
        {
            TxtDurum.Text = "Hata: " + ex;
            BtnBaslat.IsEnabled = true;
        }
    }

    private async Task PortYoklaAsync()
    {
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(3000);
            var (motd, _) = await McProtocol.YerelDurumSorAsync("127.0.0.1", 25565, zamanAsimiMs: 1500);
            if (motd != null)
            {
                global::Android.Util.Log.Info("BridgerSpike", "SUNUCU AYAKTA! MOTD: " + motd);
                Dispatcher.UIThread.Post(() =>
                {
                    TxtDurum.Text = "SUNUCU AYAKTA! (:25565) MOTD: " + motd;
                    BtnBaslat.IsEnabled = false;
                });
                return;
            }
            if (_islem is { Calisiyor: false }) break;
        }
        Dispatcher.UIThread.Post(() => TxtDurum.Text = "Sunucu yanıt vermedi (konsola bak).");
    }

    private void BtnDurdur_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _islem?.KomutGonder("stop");
        TxtDurum.Text = "Kapatma komutu gönderildi...";
    }

    private void KonsolEkle(string satir)
    {
        _satirlar.Add(satir);
        if (_satirlar.Count > 400) _satirlar.RemoveRange(0, _satirlar.Count - 400);
        TxtKonsol.Text = string.Join('\n', _satirlar);
        TxtKonsol.CaretIndex = TxtKonsol.Text?.Length ?? 0;
    }
}
