using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Bridger.Core;

namespace Bridger.Android;

// Spike ekranı: gömülü JVM'i Paper ile başlatıp konsolu akıtır.
// Başarılıysa gerçek mobil arayüz bunun üzerine kurulur.
public partial class MainView : UserControl
{
    private readonly GomuluJvmBaslatici? _sag;
    private ISunucuIslemi? _islem;
    private readonly List<string> _satirlar = new();

    public MainView()
    {
        InitializeComponent();
        _sag = Platform.SunucuBaslatici as GomuluJvmBaslatici;
        Loaded += (_, _) => _ = JreHazirlaAsync();
    }

    private string SunucuKlasoru()
        => Path.Combine(LinuxStorage.VeriKok, "sunucum", "test");

    // Gömülü JRE'yi ilk açılışta asset'lerden çıkarır (arka planda).
    private async Task JreHazirlaAsync()
    {
        try
        {
            if (JreKurucu.Hazir) { TxtDurum.Text = "JRE hazır."; return; }
            TxtDurum.Text = "JRE çıkarılıyor (ilk açılış, 1-2 dk)...";
            bool tamam = await JreKurucu.GerekirseCikarAsync(
                m => Dispatcher.UIThread.Post(() => TxtDurum.Text = m));
            Dispatcher.UIThread.Post(() => TxtDurum.Text = tamam
                ? "JRE hazır. paper.jar yükle, sonra Başlat."
                : "JRE çıkarılamadı (assets eksik olabilir).");
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => TxtDurum.Text = "JRE hatası: " + ex.Message);
        }
    }

    private async void BtnBaslat_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sag == null) { TxtDurum.Text = "Gömülü JVM sağlayıcısı yok."; return; }
        if (!JreKurucu.Hazir)
        {
            TxtDurum.Text = "JRE hazırlanıyor, bekleniyor...";
            for (int i = 0; i < 180 && !JreKurucu.Hazir; i++) await Task.Delay(1000);
            if (!JreKurucu.Hazir) { TxtDurum.Text = "JRE hazır değil (yukarıdaki mesaja bak)."; return; }
        }
        string klasor = SunucuKlasoru();
        if (!File.Exists(Path.Combine(klasor, "paper.jar")))
        {
            TxtDurum.Text = "paper.jar yok. adb ile şu klasöre yükle:\n" + klasor;
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
                TxtDurum.Text = "Başlatılamadı: " + (_sag.Hata ?? "bilinmeyen hata");
                BtnBaslat.IsEnabled = true;
                return;
            }
            BtnDurdur.IsEnabled = true;
            TxtDurum.Text = "JVM çalışıyor. Port yoklanıyor (:25565)...";
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
