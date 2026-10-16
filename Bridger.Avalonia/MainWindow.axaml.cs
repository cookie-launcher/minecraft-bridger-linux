using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Bridger.Core;

namespace Bridger.Avalonia;

// Linux portu — en yeni resmi istemci (mc.jstudio.com.tr/indir) akışıyla eşitlendi:
// 2 sekme (Sunucular / Yerel Dünyalar) + 6 port kategorisi (1xxxx..6xxxx) +
// özel taslak → HERKESE AÇIK YAP akışı + sahip eşlemesi + otomatik taslak.
public partial class MainWindow : Window
{
    private readonly ObservableCollection<OdaModel> _tumOdalar = new();
    private readonly ObservableCollection<OdaModel> _filtreliOdalar = new();
    private string _sekme = "yerel";
    private string _seciliKategori = "Tüm Odalar";
    private readonly HesapOturumu _oturum = new();
    private TunnelIstemcisi? _aktifTunnel;
    private LanIlani? _lanIlani;
    private SunucuKaydi? _seciliSunucu;

    // Oda akışı durumu (orijinal MainWindow alanları):
    private OdaModel? _aktifKurulanOda;
    private readonly List<int> _odaSunucuPortlari = new();
    private string _sunucuAltyapiModu = "port";
    private bool _otomatikTaslakOlusturuldu;
    private bool _yerelMcBulundu;
    private bool _tespitDevamEdiyor;
    private DispatcherTimer? _tespitZamanlayici;
    private DispatcherTimer? _odaListesiYenilemeZamanlayici;
    private TaskCompletionSource<bool>? _mesajTcs;

    public MainWindow()
    {
        InitializeComponent();
        IcOdalar.ItemsSource = _filtreliOdalar;
        Closing += (_, _) =>
        {
            try { _tespitZamanlayici?.Stop(); _odaListesiYenilemeZamanlayici?.Stop(); } catch { }
            try { _aktifTunnel?.Dispose(); _aktifTunnel = null; } catch { }
            try { _lanIlani?.Dispose(); _lanIlani = null; } catch { }
        };
        _ = BaslangicAsync();
    }

    private async Task BaslangicAsync()
    {
        SekmeSec("yerel");
        if (await HesapService.OtomatikGirisDeneAsync(_oturum))
        {
            TxtHesapBilgi.Text = _oturum.BilgiMetni();
            if (!string.IsNullOrEmpty(_oturum.ProfilMotd)) TxtKurMotd.Text = _oturum.ProfilMotd;
            // Orijinal DurumuYukleAsync sonu: "sunucuOtomatik" açıksa kurulu sunucuları başlat.
            SunuculariOtomatikBaslat();
        }
        HesapRozetGuncelle();
        ServerimButonunuGuncelle();
        YerelMcTespitiniBaslat();
        await OdalariSunucudanYukleAsync();
    }

    // Üst bardaki giriş rozeti: yeşil = oturum açık, amber = giriş yapılmadı.
    private void HesapRozetGuncelle()
    {
        try
        {
            bool girisli = !string.IsNullOrEmpty(_oturum.Token) && !string.IsNullOrEmpty(_oturum.Kullanici);
            ElpHesapDurum.Fill = new SolidColorBrush(girisli
                ? Color.FromRgb(0, 230, 118)
                : Color.FromRgb(255, 171, 0));
            TxtHesapRozet.Text = girisli ? _oturum.EkranNicki("Hesap") : "Giriş yapılmadı";
            ToolTip.SetTip(BtnHesap, girisli
                ? $"Hesap: {_oturum.EkranNicki()} | {_oturum.PlanAdi} — pencereyi açmak için tıkla"
                : "Giriş yapmak için tıkla");
        }
        catch { }
    }

    // Orijinal: _ayarSunucuOtomatik ise portu kapalı olan kurulu sunucuları başlat.
    private async void SunuculariOtomatikBaslat()
    {
        try
        {
            var (_, sunucuOtomatik) = LinuxStorage.AyarlariYukle();
            if (!sunucuOtomatik) return;
            foreach (var sk in SunucuYonetimi.YerelSunuculariTara())
            {
                try
                {
                    if (!SunucuYonetimi.PortDinliyorMu(sk.Port, 400))
                        await SunucuCalistirici.BaslaAsync(sk.Klasor, 2048);
                }
                catch { }
            }
        }
        catch { }
    }

    // Orijinal ServerimGorunumunuGuncelle'nin bir parçası: sunucu varsa "Serverim" görünür.
    private void ServerimButonunuGuncelle()
    {
        try
        {
            int yerel = 0;
            try { yerel = SunucuYonetimi.YerelSunuculariTara().Count; } catch { }
            BtnServerim.IsVisible = _oturum.SunucuSayisi > 0 || yerel > 0;
        }
        catch { }
    }

    // ---- SEKMELER (GÖRÜNÜM) ----
    // Orijinal SekmeSec birebir: sekme durumu + butonların görünürlüğü + başlık.
    private void SekmeSec(string sekme)
    {
        _sekme = sekme == "sunucu" ? "sunucu" : "yerel";
        bool sunucu = _sekme == "sunucu";
        BtnSekmeSunucular.Classes.Set("secil", sunucu);
        BtnSekmeYerel.Classes.Set("secil", !sunucu);
        BtnOdaSunucuKur.IsVisible = sunucu;
        BtnSunucumuYonet.IsVisible = sunucu;
        BtnOdaKur.IsVisible = !sunucu;
        BtnYerelOdamYonet.IsVisible = !sunucu;
        TxtAktifKategoriBaslik.Text = sunucu ? "Sunucular" : "Yerel Dünyalar";
        KategoriFiltresiniUygula();
    }

    private void BtnSekmeSunucular_Click(object? s, RoutedEventArgs e) => SekmeSec("sunucu");

    private void BtnSekmeYerel_Click(object? s, RoutedEventArgs e) => SekmeSec("yerel");

    // ---- ODA LİSTESİ ----
    private async Task OdalariSunucudanYukleAsync()
    {
        try
        {
            BtnYenile.IsEnabled = false;
            TxtSunucuDurum.Text = "Sunucudan odalar alınıyor...";
            var gelen = await OdaService.OdalariGetirAsync();
            _tumOdalar.Clear();
            foreach (var o in gelen)
            {
                // Orijinal: sunucudan gelen "sahip" hesabınla eşleşirse oda senin sayılır.
                if (!string.IsNullOrEmpty(_oturum.Kullanici) && !string.IsNullOrEmpty(o.Sahip) &&
                    string.Equals(o.Sahip, _oturum.Kullanici, StringComparison.OrdinalIgnoreCase))
                    o.BenimOdam = true;
                _tumOdalar.Add(o);
            }
            // Yerel taslak listede kalmalı (sunucu listesinde yoksa geri ekle).
            if (_aktifKurulanOda != null && !_tumOdalar.Any(o => o.Id == _aktifKurulanOda.Id))
                _tumOdalar.Insert(0, _aktifKurulanOda);
            ElpSunucuDurum.Fill = new SolidColorBrush(Color.FromRgb(0, 230, 118));
            TxtSunucuDurum.Text = "Bağlı: mc.jstudio.com.tr:80";
        }
        catch
        {
            ElpSunucuDurum.Fill = new SolidColorBrush(Color.FromRgb(255, 171, 0));
            TxtSunucuDurum.Text = "Çevrimdışı Mod (mc.jstudio.com.tr)";
        }
        finally
        {
            KategoriFiltresiniUygula();
            BtnYenile.IsEnabled = true;
        }
    }

    // Orijinal KategoriFiltresiniUygula: sekme (1xxxx / 2xxxx+) + kategori filtresi.
    private void KategoriFiltresiniUygula()
    {
        _filtreliOdalar.Clear();
        IEnumerable<OdaModel> kaynak = _tumOdalar;
        kaynak = _sekme == "yerel"
            ? kaynak.Where(o => o.PortKategorisi == 1)
            : kaynak.Where(o => o.PortKategorisi != 1);
        if (_seciliKategori != "Tüm Odalar")
            kaynak = kaynak.Where(o => string.Equals(o.Kategori, _seciliKategori, StringComparison.OrdinalIgnoreCase));
        foreach (var o in kaynak) _filtreliOdalar.Add(o);
        TxtOdaSayisiBadge.Text = $"{_filtreliOdalar.Count} Aktif Oda";
        BrdBosListe.IsVisible = _filtreliOdalar.Count == 0;
        TxtBosListeIpucu.Text = _sekme == "yerel"
            ? "Soldaki 'Oda Kur' butonuna tıklayarak dünyanı paylaşabilirsin!"
            : "Soldaki 'Sunucu Kur' butonuna tıklayarak sunucunu yayınlayabilirsin!";
    }

    private void Kategori_Click(object? s, RoutedEventArgs e)
    {
        BtnKategoriTum.Classes.Set("secil", s == BtnKategoriTum);
        BtnKategoriSurvival.Classes.Set("secil", s == BtnKategoriSurvival);
        BtnKategoriMiniOyunlar.Classes.Set("secil", s == BtnKategoriMiniOyunlar);
        _seciliKategori = s == BtnKategoriSurvival ? "Survival"
            : s == BtnKategoriMiniOyunlar ? "Mini Oyunlar" : "Tüm Odalar";
        KategoriFiltresiniUygula();
    }

    private async void BtnYenile_Click(object? s, RoutedEventArgs e) => await OdalariSunucudanYukleAsync();

    private async void BtnKatil_Click(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not OdaModel oda || string.IsNullOrWhiteSpace(oda.MaskeliIp)) return;
        if (!oda.HerkeseAcik)
        {
            TxtTunelDurum.Text = "Bu oda yayında değil (özel taslak).";
            return;
        }
        string ip = oda.MaskeliIp;
        try
        {
            // Orijinal SunucuyaKatilmaSinyaliGonderAsync: sunucudan taze tünel IP'si iste.
            string? taze = await OdaService.OdayaKatilSinyaliAsync(oda, _oturum.EkranNicki("Oyuncu"));
            if (!string.IsNullOrEmpty(taze)) { oda.MaskeliIp = taze; ip = taze; }
        }
        catch { }
        KopyalaPanoya(ip);
    }

    private void KopyalaPanoya(string ip)
    {
        try
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null)
            {
                var dt = new global::Avalonia.Input.DataTransfer();
                dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(ip));
                _ = cb.SetDataAsync(dt);
            }
            TxtTunelDurum.Text = $"IP kopyalandı ({ip}) — Minecraft'ta Doğrudan Bağlan'a yapıştır.";
        }
        catch { TxtTunelDurum.Text = "IP kopyalanamadı: " + ip; }
    }

    // ---- MODALLAR ----
    private void ModalKapat_Click(object? s, RoutedEventArgs e)
    {
        GrdOdaYaratModal.IsVisible = false;
        GrdHesapGiris.IsVisible = false;
        GrdSunucuKurModal.IsVisible = false;
        GrdKurPluginModal.IsVisible = false;
        GrdServerimModal.IsVisible = false;
        GrdAyarlarModal.IsVisible = false;
    }

    // ---- MESAJ KUTUSU (orijinal MessageBox karşılığı) ----
    private void MesajGoster(string metin, string baslik = "")
    {
        TxtMesajBaslik.Text = baslik;
        TxtMesajBaslik.IsVisible = !string.IsNullOrEmpty(baslik);
        TxtMesajMetni.Text = metin;
        BtnMesajIptal.IsVisible = false;
        BtnMesajTamam.Content = "Tamam";
        GrdMesaj.IsVisible = true;
    }

    private Task<bool> OnaySorAsync(string metin)
    {
        _mesajTcs = new TaskCompletionSource<bool>();
        TxtMesajBaslik.Text = "Onay";
        TxtMesajBaslik.IsVisible = true;
        TxtMesajMetni.Text = metin;
        BtnMesajIptal.IsVisible = true;
        BtnMesajTamam.Content = "Devam";
        GrdMesaj.IsVisible = true;
        return _mesajTcs.Task;
    }

    private void BtnMesajTamam_Click(object? s, RoutedEventArgs e)
    {
        GrdMesaj.IsVisible = false;
        _mesajTcs?.TrySetResult(true);
        _mesajTcs = null;
    }

    private void BtnMesajIptal_Click(object? s, RoutedEventArgs e)
    {
        GrdMesaj.IsVisible = false;
        _mesajTcs?.TrySetResult(false);
        _mesajTcs = null;
    }

    private readonly List<string> _surumler = new();

    private async Task SurumListesiniYukleAsync()
    {
        if (_surumler.Count > 0) return;
        try
        {
            using var api = new ApiClient();
            string json = await api.SurumleriGetirAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    string? sv = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()
                        : v.TryGetProperty("mc", out var m) ? m.GetString()
                        : v.TryGetProperty("surum", out var x) ? x.GetString() : null;
                    if (!string.IsNullOrEmpty(sv) && !_surumler.Contains(sv)) _surumler.Add(sv);
                }
        }
        catch { }
        if (_surumler.Count == 0)
            _surumler.AddRange(new[] { "1.20.1", "1.19.4", "1.18.2", "1.16.5" });
    }

    // ---- ODA YARAT (özel taslak) ----
    // Orijinal OdaYaratModalAc: portKategoriNo → CmbPortKategorisi seçimi.
    private async void OdaYaratModalAc(int portKategoriNo)
    {
        if (_aktifKurulanOda != null)
        {
            MesajGoster("Zaten aktif bir odanız yayında! Yeni oda açmak için önce mevcut odanızı kapatabilirsiniz.", "Oda Zaten Açık");
            return;
        }
        await SunucuModunuYukleAsync();
        try
        {
            foreach (var oge in CmbPortKategorisi.Items.OfType<ComboBoxItem>())
            {
                if (oge.Tag != null && oge.Tag.ToString() == portKategoriNo.ToString())
                {
                    CmbPortKategorisi.SelectedItem = oge;
                    break;
                }
            }
        }
        catch { }
        MaksOyuncuSecenekleriniUygula();
        OdaSunucuListesiniYukle();
        try
        {
            bool dnsAd = _sunucuAltyapiModu == "dns-ad";
            BrdDnsAdiSatir.IsVisible = dnsAd;
            if (dnsAd)
            {
                string oneri = DnsSlugUret(TxtYeniOdaAdi.Text ?? "");
                if (!string.IsNullOrEmpty(oneri)) TxtDnsAdi.Text = oneri;
                DnsOrnekYaz();
            }
        }
        catch { }
        GrdOdaYaratModal.IsVisible = true;
        _ = OdaYaratSurumleriniDoldurAsync();
    }

    private void BtnOdaSunucuKur_Click(object? s, RoutedEventArgs e) => OdaYaratModalAc(2);

    private void BtnOdaKur_Click(object? s, RoutedEventArgs e) => OdaYaratModalAc(1);

    private async Task OdaYaratSurumleriniDoldurAsync()
    {
        await SurumListesiniYukleAsync();
        string? onceki = CmbYeniOdaSurum.SelectedItem?.ToString();
        CmbYeniOdaSurum.Items.Clear();
        foreach (var v in _surumler) CmbYeniOdaSurum.Items.Add(v);
        int idx = onceki != null ? _surumler.IndexOf(onceki) : 0;
        CmbYeniOdaSurum.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private static string SeciliMetin(ComboBox c) =>
        (c.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? c.SelectedItem?.ToString() ?? "";

    private static string? SurumuAlgila(string? statusJson)
    {
        try
        {
            if (string.IsNullOrEmpty(statusJson)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(statusJson);
            if (doc.RootElement.TryGetProperty("version", out var v)
                && v.TryGetProperty("name", out var n) && n.GetString() is string s && !string.IsNullOrEmpty(s))
            {
                // Paper status'u "Paper 1.20.1" der; oda sürümü "1.20.1" olmalı.
                foreach (var onek in new[] { "Paper ", "Purpur ", "Spigot ", "Vanilla " })
                    if (s.StartsWith(onek, StringComparison.OrdinalIgnoreCase)) { s = s[onek.Length..]; break; }
                return s.Trim();
            }
        }
        catch { }
        return null;
    }

    private void SurumuAyarla(string? algilanan)
    {
        if (string.IsNullOrEmpty(algilanan)) return;
        for (int i = 0; i < CmbYeniOdaSurum.Items.Count; i++)
            if (string.Equals(CmbYeniOdaSurum.Items[i]?.ToString(), algilanan, StringComparison.OrdinalIgnoreCase))
            { CmbYeniOdaSurum.SelectedIndex = i; return; }
        CmbYeniOdaSurum.Items.Add(algilanan);
        CmbYeniOdaSurum.SelectedIndex = CmbYeniOdaSurum.Items.Count - 1;
    }

    private async void BtnModalSina_Click(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtYerelPort.Text?.Trim(), out int port)) port = 25565;
        TxtPortSinaSonuc.Text = "Sınanıyor...";
        var (motd, json) = await McProtocol.YerelDurumSorAsync("127.0.0.1", port, zamanAsimiMs: 1500);
        TxtTunelDurum.Text = motd != null
            ? $"Yerel Port ({port}): Bulundu"
            : $"Yerel Port ({port}): Bulunamadı";
        TxtPortSinaSonuc.Text = motd != null
            ? $"Yerel {port} portunda Minecraft bulundu."
            : $"Yerel {port} portuna ulaşılamadı. Minecraft dünyanı açtığından emin ol.";
        string? surum = SurumuAlgila(json);
        if (surum != null)
        {
            SurumuAyarla(surum);
            TxtPortSinaSonuc.Text += $" Sunucu sürümü: {surum}.";
        }
    }

    // Orijinal BtnModalOnayla_Click: özel taslak oluştur (yayınlanmaz).
    private void BtnModalOnayla_Click(object? s, RoutedEventArgs e)
    {
        string odaAdi = string.IsNullOrWhiteSpace(TxtYeniOdaAdi.Text) ? "Minecraft Lobi Odası" : TxtYeniOdaAdi.Text!.Trim();
        string kurucu = _oturum.EkranNicki("SteveTR");
        string kategori = SeciliMetin(CmbYeniOdaKategori);
        if (string.IsNullOrEmpty(kategori)) kategori = "Survival";
        string surum = CmbYeniOdaSurum.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrEmpty(surum)) surum = "1.20.1";
        int maks = 8;
        int.TryParse(SeciliMetin(CmbYeniOdaMaksOyuncu), out maks);
        if (maks <= 0) maks = 8;
        int port = 25565;
        if (!int.TryParse(TxtYerelPort.Text?.Trim(), out port) || port <= 0 || port > 65535) port = 25565;

        GrdOdaYaratModal.IsVisible = false;
        var oda = new OdaModel
        {
            Id = "oda-" + Guid.NewGuid().ToString("N")[..6],
            OdaAdi = odaAdi,
            Kurucu = kurucu,
            Kategori = kategori,
            AktifOyuncu = 1,
            MaksOyuncu = maks,
            Surum = surum,
            YerelPort = port,
            PortKategorisi = SeciliPortKategorisi(),
            DnsAdi = _sunucuAltyapiModu == "dns-ad" ? (TxtDnsAdi.Text ?? "").Trim().ToLowerInvariant() : "",
            MaskeliIp = "Yayınlanmadı (özel taslak)",
            HerkeseAcik = false,
            BenimOdam = true,
            LanGoster = ChkLanGoster.IsChecked == true,
        };
        if (_aktifKurulanOda != null) _tumOdalar.Remove(_aktifKurulanOda);
        _aktifKurulanOda = oda;
        _tumOdalar.Insert(0, oda);
        KategoriFiltresiniUygula();
        BrdHostBilgiBandi.IsVisible = true;
        TxtHostBilgiMetni.Text = $"Taslak Oda: '{oda.OdaAdi}' (özel) | Yerel hedef port: {port} | Yayınlamak için karttaki HERKESE AÇIK YAP'a bas.";
        MesajGoster("Oda özel taslak olarak oluşturuldu.\n\nBaşkalarının görebilmesi için oda kartındaki HERKESE AÇIK YAP butonuna bas.", "Özel Taslak Hazır");
    }

    private int SeciliPortKategorisi()
    {
        try
        {
            if (CmbPortKategorisi.SelectedItem is ComboBoxItem oge &&
                int.TryParse(oge.Tag as string, out int sonuc) && sonuc >= 1 && sonuc <= 6)
                return sonuc;
        }
        catch { }
        return 2;
    }

    // ---- YAYINLA (karttaki HERKESE AÇIK YAP) ----
    // Orijinal BtnYayinla_Click birebir + port iyileştirmesi (gerçek sürümü algıla).
    private async void BtnYayinla_Click(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not OdaModel taslak) return;
        if (taslak.HerkeseAcik) return;
        var btn = s as Button;
        if (btn != null) btn.IsEnabled = false;
        try
        {
            var (motd, durumJson) = await McProtocol.YerelDurumSorAsync("127.0.0.1", taslak.YerelPort, zamanAsimiMs: 3000);
            bool yerelOk = motd != null;
            if (!yerelOk)
            {
                bool devam = await OnaySorAsync($"Yerel {taslak.YerelPort} portunda Minecraft bulunamadı.\n\nYine de yayınlansın mı? (Katılımcılar bağlanamaz.)");
                if (!devam) return;
            }
            // İlan edilen sürüm, tünele bağlı GERÇEK sunucununki olmalı; yoksa
            // istemci "outdated server" yer. (Port iyileştirmesi.)
            string? gercekSurum = SurumuAlgila(durumJson);
            if (!string.IsNullOrEmpty(gercekSurum) &&
                !string.Equals(taslak.Surum, gercekSurum, StringComparison.OrdinalIgnoreCase))
                taslak.Surum = gercekSurum;

            taslak.YerelDogrulandi = yerelOk ? "probed" : "onay";
            if (_aktifKurulanOda != null && _aktifKurulanOda != taslak)
                _tumOdalar.Remove(_aktifKurulanOda);
            _aktifKurulanOda = taslak;

            try { _aktifTunnel?.Dispose(); } catch { }
            var tunnel = new TunnelIstemcisi();
            tunnel.OdaKapatildi += YayindanDusur;
            string? atanmis = await tunnel.YayinlaAsync(taslak, taslak.YerelPort,
                taslak.MaksOyuncu > 0 ? taslak.MaksOyuncu : 20, _oturum.Token);
            if (string.IsNullOrEmpty(atanmis))
            {
                try { tunnel.Dispose(); } catch { }
                _aktifTunnel = null;
                MesajGoster("Sunucuya yayınlanamadı. Bağlantını kontrol edip tekrar dene.", "Yayın Başarısız");
                return;
            }
            _aktifTunnel = tunnel;
            taslak.MaskeliIp = atanmis;
            taslak.HerkeseAcik = true;
            KategoriFiltresiniUygula();
            _ = HesapService.KatilimBildirAsync(_oturum);
            await OdalariSunucudanYukleAsync();
            BrdHostBilgiBandi.IsVisible = true;
            TxtHostBilgiMetni.Text = $"Odan Yayında: '{taslak.OdaAdi}' | Yerel hedef: {taslak.YerelPort} | Tünel IP: {taslak.MaskeliIp}";
            if (taslak.LanGoster)
            {
                _lanIlani ??= new LanIlani();
                _lanIlani.Baslat(taslak.OdaAdi, taslak.YerelPort);
            }
            else
            {
                try { _lanIlani?.Durdur(); } catch { }
            }
            MesajGoster($"Odan herkese açık yayınlandı!\n\nTünel IP: {taslak.MaskeliIp}\nKatılımcılar lobide odayı görecek.", "Yayında");
        }
        catch (Exception ex)
        {
            MesajGoster("Yayınlanırken hata oluştu:\n\n" + ex.Message, "Yayın Hatası");
        }
        finally
        {
            if (btn != null) btn.IsEnabled = true;
        }
    }

    // Orijinal YayindanDusur: özel taslağa geri döndür + bandı göster.
    private void YayindanDusur(string mesaj)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { _aktifTunnel?.Dispose(); } catch { }
            _aktifTunnel = null;
            try { _lanIlani?.Durdur(); } catch { }
            if (_aktifKurulanOda != null)
            {
                _aktifKurulanOda.HerkeseAcik = false;
                _aktifKurulanOda.MaskeliIp = "Yayınlanmadı (özel taslak)";
                KategoriFiltresiniUygula();
            }
            BrdHostBilgiBandi.IsVisible = true;
            TxtHostBilgiMetni.Text = "Oda yayından alındı (özel taslak).";
            MesajGoster(mesaj, "Oda Kapatıldı");
        });
    }

    // Orijinal OdayiVeBaglantilariKapat: taslağı listeden kaldır, tüneli kapat.
    private void OdayiVeBaglantilariKapat()
    {
        BrdHostBilgiBandi.IsVisible = false;
        if (_aktifKurulanOda != null)
        {
            _tumOdalar.Remove(_aktifKurulanOda);
            _aktifKurulanOda = null;
        }
        try { _aktifTunnel?.Dispose(); } catch { }
        _aktifTunnel = null;
        try { _lanIlani?.Durdur(); } catch { }
    }

    private void BtnOdayiKapat_Click(object? s, RoutedEventArgs e)
    {
        OdayiVeBaglantilariKapat();
        KategoriFiltresiniUygula();
    }

    // ---- SUNUCU ALTYAPI MODU + DNS ADI ----
    private async Task SunucuModunuYukleAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string json;
            try { json = await http.GetStringAsync("http://mc.jstudio.com.tr:80/api/info"); }
            catch { json = await http.GetStringAsync("https://mc.jstudio.com.tr/api/info"); }
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string mod = doc.RootElement.TryGetProperty("altyapiModu", out var m) ? (m.GetString() ?? "").ToLowerInvariant() : "";
            _sunucuAltyapiModu = mod == "dns-ad" ? "dns-ad" : mod == "dns-sayi" ? "dns-sayi" : "port";
        }
        catch { }
    }

    // Orijinal DnsSlugUret birebir.
    private static string DnsSlugUret(string ham)
    {
        try
        {
            string text = (ham ?? "").ToLowerInvariant().Replace('ı', 'i').Replace('ş', 's')
                .Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');
            var sb = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            string slug = sb.ToString().Trim('-');
            if (slug.Length > 24) slug = slug[..24].Trim('-');
            return slug.Length >= 3 ? slug : "";
        }
        catch { return ""; }
    }

    private void TxtDnsAdi_Degisti(object? s, TextChangedEventArgs e) => DnsOrnekYaz();

    private void DnsOrnekYaz()
    {
        try
        {
            string text = (TxtDnsAdi.Text ?? "").Trim().ToLowerInvariant();
            TxtDnsAdiOrnek.Text = string.IsNullOrEmpty(text) ? ".mc.jstudio.com.tr" : text + ".mc.jstudio.com.tr";
        }
        catch { }
    }

    // Orijinal OdaSunucuListesiniYukle: yerel sunucuları listele, açık olanı seç.
    private async void OdaSunucuListesiniYukle()
    {
        try
        {
            var liste = SunucuYonetimi.YerelSunuculariTara();
            var acikliklar = await Task.Run(() => liste
                .Select(sk => { try { return SunucuYonetimi.PortDinliyorMu(sk.Port, 300); } catch { return false; } })
                .ToList());
            _odaSunucuPortlari.Clear();
            CmbOdaSunucu.Items.Clear();
            int sec = 0;
            bool ilkAcik = false;
            for (int i = 0; i < liste.Count; i++)
            {
                var sk = liste[i];
                string etiket = $"{sk.Ad} (:{sk.Port}){(acikliklar[i] ? " • açık" : "")}";
                CmbOdaSunucu.Items.Add(etiket);
                _odaSunucuPortlari.Add(sk.Port);
                if (acikliklar[i] && !ilkAcik) { sec = i; ilkAcik = true; }
            }
            if (liste.Count == 0)
            {
                CmbOdaSunucu.Items.Add("Yerel 25565 (sabit)");
                _odaSunucuPortlari.Add(25565);
            }
            CmbOdaSunucu.SelectedIndex = sec;
            if (sec < _odaSunucuPortlari.Count) TxtYerelPort.Text = _odaSunucuPortlari[sec].ToString();
        }
        catch { }
    }

    private void CmbOdaSunucu_Degisti(object? s, SelectionChangedEventArgs e)
    {
        try
        {
            int i = CmbOdaSunucu.SelectedIndex;
            if (i >= 0 && i < _odaSunucuPortlari.Count) TxtYerelPort.Text = _odaSunucuPortlari[i].ToString();
        }
        catch { }
    }

    // Orijinal MaksOyuncuSecenekleriniUygula birebir (plan sınırına göre).
    private void MaksOyuncuSecenekleriniUygula()
    {
        try
        {
            try
            {
                if (CmbPortKategorisi.Items.Count > 0 && CmbPortKategorisi.SelectedIndex < 0)
                    CmbPortKategorisi.SelectedIndex = 1;
            }
            catch { }
            int[] secenekler = { 2, 4, 6, 8, 12, 15, 20 };
            CmbYeniOdaMaksOyuncu.Items.Clear();
            int secili = 0;
            for (int i = 0; i < secenekler.Length && secenekler[i] <= _oturum.PlanMaxOyuncu; i++)
            {
                CmbYeniOdaMaksOyuncu.Items.Add(new ComboBoxItem { Content = secenekler[i].ToString() });
                secili = i;
            }
            if (CmbYeniOdaMaksOyuncu.Items.Count == 0)
                CmbYeniOdaMaksOyuncu.Items.Add(new ComboBoxItem { Content = _oturum.PlanMaxOyuncu.ToString() });
            else
                CmbYeniOdaMaksOyuncu.SelectedIndex = secili;
        }
        catch { }
    }

    // ---- KENDİ ODAM (sahip eşleşmesi) ----
    private OdaModel? KendiOdamBul(bool haritaSekmesi)
    {
        try
        {
            foreach (var o in _tumOdalar)
                if (o != null && o.BenimOdam && (o.PortKategorisi == 1) == haritaSekmesi) return o;
            if (_aktifKurulanOda != null && (_aktifKurulanOda.PortKategorisi == 1) == haritaSekmesi)
                return _aktifKurulanOda;
        }
        catch { }
        return null;
    }

    private void BtnSunucumuYonet_Click(object? s, RoutedEventArgs e)
    {
        try { ServerimGorunumunuGuncelle(); } catch { }
        try
        {
            var oda = KendiOdamBul(haritaSekmesi: false);
            if (oda != null)
            {
                BrdHostBilgiBandi.IsVisible = true;
                TxtHostBilgiMetni.Text = $"Sunucun: '{oda.OdaAdi}' | Adres: {oda.MaskeliIp} | Durum: {oda.DurumMetni}";
            }
        }
        catch { }
        GrdServerimModal.IsVisible = true;
    }

    private void BtnYerelOdamYonet_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            var oda = KendiOdamBul(haritaSekmesi: true) ?? _aktifKurulanOda;
            if (oda == null)
            {
                MesajGoster("Henüz paylaştığın bir dünya yok.\n\nÖnce 'Oda Kur' ile dünyanı paylaş.", "Yerel Odam");
                return;
            }
            string adres = oda.MaskeliIp ?? "";
            if (!string.IsNullOrEmpty(adres))
            {
                try { KopyalaPanoya(adres); } catch { }
            }
            BrdHostBilgiBandi.IsVisible = true;
            TxtHostBilgiMetni.Text = $"Yerel odan: '{oda.OdaAdi}' | Adres: {adres} (panoya kopyalandı)";
        }
        catch { }
    }

    // ---- YEREL MC TESPİTİ (5 sn) + OTOMATİK TASLAK ----
    private static async Task<bool> YerelMcPortSinaAsync(int port, int zamanAsimiMs)
    {
        try
        {
            using var yoklama = new TcpClient();
            var baglanti = yoklama.ConnectAsync("127.0.0.1", port);
            return await Task.WhenAny(baglanti, Task.Delay(zamanAsimiMs)) == baglanti && yoklama.Connected;
        }
        catch { return false; }
    }

    private void YerelMcTespitiniBaslat()
    {
        _tespitZamanlayici = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tespitZamanlayici.Tick += async (_, _) => await YerelMcTespitTurAsync();
        _tespitZamanlayici.Start();
        _ = YerelMcTespitTurAsync();

        _odaListesiYenilemeZamanlayici = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _odaListesiYenilemeZamanlayici.Tick += async (_, _) =>
        {
            if (!_tespitDevamEdiyor) await OdalariSunucudanYukleAsync();
        };
        _odaListesiYenilemeZamanlayici.Start();
    }

    private async Task YerelMcTespitTurAsync()
    {
        if (_tespitDevamEdiyor) return;
        _tespitDevamEdiyor = true;
        try
        {
            bool bulundu = false;
            int bulunanPort = 25565;
            try
            {
                var portlar = new HashSet<int> { 25565 };
                foreach (var sk in SunucuYonetimi.YerelSunuculariTara()) portlar.Add(sk.Port);
                foreach (int p in portlar)
                {
                    if (await YerelMcPortSinaAsync(p, 300)) { bulundu = true; bulunanPort = p; break; }
                }
            }
            catch { }
            if (bulundu != _yerelMcBulundu)
            {
                _yerelMcBulundu = bulundu;
                if (bulundu)
                {
                    TxtTunelDurum.Text = "Yerel MC bulundu (:" + bulunanPort + ")";
                    OtomatikTaslakOlustur(bulunanPort);
                }
                else
                {
                    TxtTunelDurum.Text = "Yerel MC (25565): Bulunamadı";
                    _otomatikTaslakOlusturuldu = false;
                }
            }
        }
        catch { }
        finally { _tespitDevamEdiyor = false; }
    }

    // Orijinal OtomatikTaslakOlustur: yerel MC bulununca 1xxxx taslağı aç.
    private void OtomatikTaslakOlustur(int port)
    {
        try
        {
            if (!_otomatikTaslakOlusturuldu && _aktifKurulanOda == null)
            {
                _otomatikTaslakOlusturuldu = true;
                _aktifKurulanOda = new OdaModel
                {
                    Id = "oda-" + Guid.NewGuid().ToString("N")[..6],
                    OdaAdi = "Yerel Minecraft Dünyam",
                    Kurucu = _oturum.EkranNicki("SteveTR"),
                    Kategori = "Survival",
                    AktifOyuncu = 1,
                    MaksOyuncu = 8,
                    Surum = "1.20.1",
                    YerelPort = port,
                    PortKategorisi = 1,
                    MaskeliIp = "Yayınlanmadı (özel taslak)",
                    HerkeseAcik = false,
                    BenimOdam = true,
                    LanGoster = true,
                };
                _tumOdalar.Insert(0, _aktifKurulanOda);
                KategoriFiltresiniUygula();
                BrdHostBilgiBandi.IsVisible = true;
                TxtHostBilgiMetni.Text = "Yerel Minecraft bulundu, özel taslak oluşturuldu. Yayınlamak için karttaki HERKESE AÇIK YAP'a bas.";
            }
        }
        catch { }
    }

    // ---- HESAP ----
    private void BtnHesap_Click(object? s, RoutedEventArgs e)
    {
        HesapRozetGuncelle();
        bool girisli = !string.IsNullOrEmpty(_oturum.Token) && !string.IsNullOrEmpty(_oturum.Kullanici);
        TxtHesapBilgi.Text = girisli ? _oturum.BilgiMetni() : "Giriş yapmadın";
        StkHesapForm.IsVisible = !girisli;
        BtnHesapKayitOl.IsVisible = !girisli;
        BtnHesapGirisYap.IsVisible = !girisli;
        TxtHesapOturumNotu.IsVisible = girisli;
        if (girisli)
            TxtHesapOturumNotu.Text = File.Exists(LinuxStorage.BeniHatirlaDosyasi())
                ? "Oturum açık — bu cihazda açılışta otomatik giriş yapılır."
                : "Oturum açık.";
        TxtHesapDurum.Text = "";
        GrdHesapGiris.IsVisible = true;
    }

    private async void BtnHesapGiris_Click(object? s, RoutedEventArgs e) =>
        await HesapIslemiAsync(false);

    private async void BtnHesapKayit_Click(object? s, RoutedEventArgs e) =>
        await HesapIslemiAsync(true);

    // Orijinal HesapIslemiAsync birebir: doğrulama → {kullanici,sifre[,oyuncuAdi]} →
    // "ok" kontrolü → alanları temizle → kapat → beni-hatırla → DurumuYukle.
    private async Task HesapIslemiAsync(bool kayitMi)
    {
        string ad = TxtHesapAd.Text ?? "";
        string sifre = TxtHesapSifre.Text ?? "";
        string mc = TxtHesapMc.Text ?? "";
        string? hata = HesapService.Dogrula(ad, sifre, mc, kayitMi);
        if (hata != null) { TxtHesapDurum.Text = hata; return; }
        ad = ad.Trim().ToLowerInvariant();
        TxtHesapDurum.Text = kayitMi ? "Hesap oluşturuluyor..." : "Giriş yapılıyor...";
        try
        {
            var sonuc = await HesapService.GirisVeyaKayitAsync(ad, sifre, mc, kayitMi);
            if (!sonuc.Ok)
            {
                TxtHesapDurum.Text = sonuc.Hata ?? "Giriş başarısız.";
                return;
            }
            _oturum.Token = sonuc.Token;
            _oturum.Kullanici = ad;
            TxtHesapSifre.Text = "";
            TxtHesapAd.Text = "";
            TxtHesapMc.Text = "";
            GrdHesapGiris.IsVisible = false;
            TxtHesapDurum.Text = "";
            try
            {
                if (ChkBeniHatirla.IsChecked == true) await HesapService.BeniHatirlaKaydetAsync(_oturum);
            }
            catch { }
            await HesapService.DurumuYukleAsync(_oturum);
            HesapRozetGuncelle();
            ServerimButonunuGuncelle();
            if (_oturum.Token == null)
            {
                TxtHesapDurum.Text = "Oturum süresi doldu, tekrar giriş yap.";
                GrdHesapGiris.IsVisible = true;
                return;
            }
            TxtHesapBilgi.Text = _oturum.BilgiMetni();
            if (!string.IsNullOrEmpty(_oturum.ProfilMotd)) TxtKurMotd.Text = _oturum.ProfilMotd;
            SunuculariOtomatikBaslat();
        }
        catch (Exception ex) { TxtHesapDurum.Text = "Hata: " + ex.Message; }
    }

    // ---- SUNUCU KUR ----
    private readonly List<string> _kurSeciliPluginler = new();
    private readonly List<Dictionary<string, string>> _paketKatalog = new();

    private async void BtnSunucuKur_Click(object? s, RoutedEventArgs e)
    {
        GrdSunucuKurModal.IsVisible = true;
        if (CmbKurSurum.Items.Count == 0)
        {
            await SurumListesiniYukleAsync();
            foreach (var v in _surumler) CmbKurSurum.Items.Add(v);
            CmbKurSurum.SelectedIndex = 0;
        }
        if (CmbKurPaket.Items.Count == 0)
        {
            CmbKurPaket.Items.Add("(yok)");
            try
            {
                using var api = new ApiClient();
                string json = await api.PaketleriGetirAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var p in doc.RootElement.EnumerateArray())
                    {
                        string? ad = p.TryGetProperty("ad", out var a) ? a.GetString() : null;
                        if (string.IsNullOrEmpty(ad)) continue;
                        var pls = new List<string>();
                        if (p.TryGetProperty("pluginler", out var pl) && pl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            foreach (var x in pl.EnumerateArray())
                                if (x.GetString() is string ps) pls.Add(ps);
                        _paketKatalog.Add(new Dictionary<string, string> { ["ad"] = ad });
                        foreach (var x in pls) _paketKatalog[^1][x] = x;
                        CmbKurPaket.Items.Add(ad);
                    }
            }
            catch { }
            CmbKurPaket.SelectedIndex = 0;
        }
    }

    private void KurDurum(string m) => TxtKurDurum.Text = m;

    private async void BtnKurBaslat_Click(object? s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_oturum.Token)) { KurDurum("Önce giriş yapmalısın."); return; }
        await HesapService.DurumuYukleAsync(_oturum);
        HesapRozetGuncelle();
        if (string.IsNullOrEmpty(_oturum.Token)) { KurDurum("Oturum süresi doldu, tekrar giriş yap."); return; }
        TxtHesapBilgi.Text = _oturum.BilgiMetni();
        if (_oturum.SunucuSayisi >= _oturum.PlanMaksSunucu)
        { KurDurum($"Planın en fazla {_oturum.PlanMaksSunucu} sunucu açar ({_oturum.PlanAdi})."); return; }

        string surum = (CmbKurSurum.SelectedItem?.ToString() ?? "").Trim();
        if (string.IsNullOrEmpty(surum)) surum = "1.20.1";
        string nick = _oturum.EkranNicki("SteveTR");
        if (nick.Length > 16) nick = nick[..16];
        string motd = (TxtKurMotd.Text ?? "").Trim();
        if (string.IsNullOrEmpty(motd)) motd = "Minecraft Bridger Sunucusu";
        motd = motd.Replace("\r", " ").Replace("\n", " ");
        if (motd.Length > 60) motd = motd[..60];
        string adHam = (TxtKurAd.Text ?? "").Trim();
        if (string.IsNullOrEmpty(adHam)) adHam = "AnaSunucu";
        foreach (char c in Path.GetInvalidFileNameChars()) adHam = adHam.Replace(c.ToString(), "");
        adHam = adHam.Replace(".", "");
        if (string.IsNullOrEmpty(adHam)) adHam = "AnaSunucu";
        if (adHam.Length > 32) adHam = adHam[..32];

        string klasor = Path.Combine(SunucuYonetimi.SunucumKok(), adHam);
        if (s is Button btn) btn.IsEnabled = false;
        try
        {
            try
            {
            KurDurum("Klasör hazırlanıyor: " + klasor);
            Directory.CreateDirectory(klasor);
            // Zaten çalışan sunucuya ikinci kopya açma (world/session.lock çakışır) — ona bağlan.
            int mevcutPort0 = PortOku(klasor);
            if (SunucuYonetimi.SunucuCalisiyorMu(klasor, mevcutPort0, out int calisanPid))
            {
                SunucuCalistirici.Baglan(klasor, mevcutPort0, calisanPid);
                KurDurum($"Sunucu zaten çalışıyor (pid {calisanPid}, :{mevcutPort0}). Durum kontrol ediliyor...");
                var (ms, _) = await McProtocol.YerelDurumSorAsync("127.0.0.1", mevcutPort0, zamanAsimiMs: 2000);
                if (ms != null || await SunucuCalistirici.CevapBekleAsync(mevcutPort0))
                {
                    GrdSunucuKurModal.IsVisible = false;
                    ServerimButonunuGuncelle();
                    MesajGoster($"Sunucun zaten hazır ve çalışıyor! (:{mevcutPort0})\n\nSunucular sekmesinde 'Sunucu Kur' ile taslak açıp HERKESE AÇIK YAP'a basabilirsin.", "Sunucum Hazır");
                    return;
                }
                KurDurum("Sunucu süreci ayakta ama yanıt vermiyor. Son log:\n" + SunucuYonetimi.LogKuyruguOku(klasor));
                return;
            }
            if (File.Exists(Path.Combine(klasor, "server.properties")) && File.Exists(Path.Combine(klasor, "paper.jar")))
            {
                KurDurum("Mevcut sunucu bulundu, başlatılıyor...");
                if (await SunucuCalistirici.BaslaAsync(klasor, 2048) != SunucuCalistirici.BaslaSonucu.Hata &&
                    await SunucuCalistirici.CevapBekleAsync(PortOku(klasor)))
                {
                    int p0 = PortOku(klasor);
                    GrdSunucuKurModal.IsVisible = false;
                    ServerimButonunuGuncelle();
                    MesajGoster($"Sunucun hazır ve çalışıyor! (:{p0})\n\nSunucular sekmesinde 'Sunucu Kur' ile taslak açıp HERKESE AÇIK YAP'a basabilirsin.", "Sunucum Hazır");
                    return;
                }
                KurDurum("Başlatılamadı. Son log:\n" + SunucuYonetimi.LogKuyruguOku(klasor) + "\nTam kurulum deneniyor...");
            }
            KurDurum("Boş port aranıyor (20000-65535, 25565 hariç)...");
            int hedefPort = await Task.Run(SunucuYonetimi.BosPortBul);
            KurDurum("Seçilen port: " + hedefPort);
            string jarYolu = Path.Combine(klasor, "paper.jar");
            if (!File.Exists(jarYolu))
            {
                KurDurum($"Paper {surum} katalogda aranıyor...");
                bool katalogdan = await SunucuJariniKatalogdanIndirAsync(surum, jarYolu);
                if (!katalogdan)
                {
                    KurDurum($"Paper {surum} indiriliyor (biraz sürebilir)...");
                    using var http0 = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    string? jarUrl = await ApiClient.PaperJarAdresiAsync(http0, "http://mc.jstudio.com.tr:80", surum, CancellationToken.None);
                    if (string.IsNullOrEmpty(jarUrl)) { KurDurum($"Sürüm bulunamadı: {surum} (Yerel Jar Seç ile kendi jarını gösterebilirsin)"); return; }
                    try { await JavaRuntime.JarIndirAsync(http0, jarUrl, jarYolu, new Progress<double>(p => KurDurum($"İndiriliyor %{p:F0}...")), CancellationToken.None); }
                    catch (Exception ex) { KurDurum("İndirme başarısız: " + ex.Message); return; }
                }
            }
            else KurDurum("Mevcut paper.jar kullanılıyor...");
            await File.WriteAllTextAsync(Path.Combine(klasor, "eula.txt"), "eula=true\n");
            await File.WriteAllLinesAsync(Path.Combine(klasor, "server.properties"), new[]
            {
                "server-port=" + hedefPort, "online-mode=false", "motd=" + motd,
                "max-players=" + _oturum.PlanMaxOyuncu, "view-distance=6", "simulation-distance=4",
                "network-compression-threshold=64", "white-list=false", "spawn-protection=0",
            });
            string uuid = LinuxStorage.CevrimdisiUuidUret(nick).ToString("D");
            await File.WriteAllTextAsync(Path.Combine(klasor, "ops.json"),
                "[{\"uuid\":\"" + uuid + "\",\"name\":\"" + nick + "\",\"level\":4,\"bypassesPlayerLimit\":true}]");
            await File.WriteAllTextAsync(Path.Combine(klasor, "whitelist.json"),
                "[{\"uuid\":\"" + uuid + "\",\"name\":\"" + nick + "\"}]");
            await File.WriteAllTextAsync(Path.Combine(klasor, "bridger.json"),
                System.Text.Json.JsonSerializer.Serialize(new { ad = adHam, surum, motd, port = hedefPort }));
            KurDurum("Plugin paketi denetleniyor...");
            string pluginsDir = Path.Combine(klasor, "plugins");
            if (_kurSeciliPluginler.Count > 0)
            {
                await SeciliPluginleriIndirAsync(surum, pluginsDir, _kurSeciliPluginler);
                _kurSeciliPluginler.Clear();
            }
            else if (!await PaketPluginleriniIndirAsync(surum, pluginsDir))
            {
                KurDurum("Katalog pluginleri denetleniyor...");
                await KatalogPluginleriniIndirAsync(surum, pluginsDir);
            }
            KurDurum("Sunucu başlatılıyor...");
            string javaExe = SunucuYonetimi.KlasorJavaExe(klasor);
            await File.WriteAllTextAsync(Path.Combine(klasor, "start.sh"),
                "#!/bin/sh\ncd \"$(dirname \"$0\")\"\nexec \"" + javaExe + "\" -Xmx2G -jar paper.jar nogui\n");
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(klasor, "start.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            if (await SunucuCalistirici.BaslaAsync(klasor, 2048) == SunucuCalistirici.BaslaSonucu.Hata) { KurDurum("Başlatılamadı (klasörü açıp start.sh dosyasını el ile çalıştır)"); return; }
            KurDurum("Sunucu açılıyor, port yoklanıyor...");
            if (await SunucuCalistirici.CevapBekleAsync(hedefPort))
            {
                GrdSunucuKurModal.IsVisible = false;
                ServerimButonunuGuncelle();
                MesajGoster($"Sunucun hazır ve çalışıyor! (:{hedefPort})\n\nSunucular sekmesinde 'Sunucu Kur' ile taslak açıp HERKESE AÇIK YAP'a basabilirsin.", "Sunucum Hazır");
            }
            else KurDurum(hedefPort + " portu açılmadı. Son log:\n" + SunucuYonetimi.LogKuyruguOku(klasor));
            }
            catch (Exception ex) { KurDurum("Kurulum hatası: " + ex.Message); }
        }
        finally { if (s is Button b2) b2.IsEnabled = true; }
    }

    private static int PortOku(string klasor)
    {
        try
        {
            foreach (string satir in File.ReadAllLines(Path.Combine(klasor, "server.properties")))
                if (satir.StartsWith("server-port=") && int.TryParse(satir[12..], out int p)) return p;
        }
        catch { }
        return 25565;
    }

    private static async Task<bool> SunucuJariniKatalogdanIndirAsync(string surum, string jarYolu)
    {
        try
        {
            using var api = new ApiClient();
            string json = await api.HesapAsync($"/api/serverjar/{Uri.EscapeDataString(surum)}", "GET", null, null)
                .ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("url", out var u)) return false;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await JavaRuntime.JarIndirAsync(http, u.GetString()!, jarYolu, null, CancellationToken.None);
            return true;
        }
        catch { return false; }
    }

    private static async Task KatalogPluginleriniIndirAsync(string surum, string pluginsDir)
    {
        try
        {
            Directory.CreateDirectory(pluginsDir);
            using var api = new ApiClient();
            string json = await api.HesapAsync($"/api/plugins/{Uri.EscapeDataString(surum)}", "GET", null, null)
                .ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                try
                {
                    if (!o.TryGetProperty("name", out var n)) continue;
                    string ad = n.GetString() ?? "";
                    if (ad.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !ad.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                    string hedef = Path.Combine(pluginsDir, ad);
                    if (File.Exists(hedef)) continue;
                    await JavaRuntime.JarIndirAsync(http,
                        $"http://mc.jstudio.com.tr:80/download/plugins/{Uri.EscapeDataString(surum)}/{Uri.EscapeDataString(ad)}",
                        hedef, null, CancellationToken.None);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task<bool> PaketPluginleriniIndirAsync(string surum, string pluginsDir)
    {
        try
        {
            string secili = CmbKurPaket.SelectedItem?.ToString() ?? "(yok)";
            if (secili == "(yok)") return false;
            var liste = _paketKatalog.Where(k => k.TryGetValue("ad", out var a) && a == secili)
                .SelectMany(k => k.Where(kv => kv.Key != "ad").Select(kv => kv.Key)).ToList();
            if (liste.Count == 0) return false;
            await SeciliPluginleriIndirAsync(surum, pluginsDir, liste);
            return true;
        }
        catch { return false; }
    }

    private static async Task SeciliPluginleriIndirAsync(string surum, string pluginsDir, List<string> adlar)
    {
        Directory.CreateDirectory(pluginsDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (string ad in adlar)
        {
            try
            {
                string hedef = Path.Combine(pluginsDir, ad);
                if (File.Exists(hedef)) continue;
                await JavaRuntime.JarIndirAsync(http,
                    $"http://mc.jstudio.com.tr:80/download/plugins/{Uri.EscapeDataString(surum)}/{Uri.EscapeDataString(ad)}",
                    hedef, null, CancellationToken.None);
            }
            catch { }
        }
    }

    // ---- PLUGIN SEÇ MODAL ----
    private async void BtnKurPlugin_Click(object? s, RoutedEventArgs e)
    {
        string surum = (CmbKurSurum.SelectedItem?.ToString() ?? "").Trim();
        if (string.IsNullOrEmpty(surum)) surum = "1.20.1";
        GrdKurPluginModal.IsVisible = true;
        StkKurPluginListe.Children.Clear();
        TxtKurPluginAciklama.Text = surum + " kataloğu yükleniyor...";
        try
        {
            using var api = new ApiClient();
            string json = await api.HesapAsync($"/api/plugins/{Uri.EscapeDataString(surum)}", "GET", null, null)
                .ContinueWith(t => t.Result.Content.ReadAsStringAsync()).Unwrap();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            StkKurPluginListe.Children.Clear();
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var o in doc.RootElement.EnumerateArray())
                {
                    if (!o.TryGetProperty("name", out var n)) continue;
                    string ad = n.GetString() ?? "";
                    if (string.IsNullOrEmpty(ad)) continue;
                    var cb = new CheckBox { Content = ad, IsChecked = _kurSeciliPluginler.Contains(ad) };
                    StkKurPluginListe.Children.Add(cb);
                }
            TxtKurPluginAciklama.Text = $"{surum} için {StkKurPluginListe.Children.Count} plugin bulundu.";
        }
        catch { TxtKurPluginAciklama.Text = "Katalog yüklenemedi."; }
    }

    private void BtnKurPluginTamam_Click(object? s, RoutedEventArgs e)
    {
        _kurSeciliPluginler.Clear();
        foreach (var c in StkKurPluginListe.Children)
            if (c is CheckBox cb && cb.IsChecked == true && cb.Content?.ToString() is string ad)
                _kurSeciliPluginler.Add(ad);
        GrdKurPluginModal.IsVisible = false;
        KurDurum($"{_kurSeciliPluginler.Count} plugin seçildi.");
    }

    private void BtnKurPluginVazgec_Click(object? s, RoutedEventArgs e) => GrdKurPluginModal.IsVisible = false;

    // ---- YEREL JAR SEÇ ----
    private async void BtnKurJarSec_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            var tl = TopLevel.GetTopLevel(this);
            if (tl?.StorageProvider == null) return;
            var dosya = await tl.StorageProvider.OpenFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Sunucu jar dosyasını seç",
                AllowMultiple = false,
            });
            if (dosya.Count == 0) return;
            string kok = SunucuYonetimi.SunucumKok();
            Directory.CreateDirectory(kok);
            string src = dosya[0].Path.LocalPath;
            File.Copy(src, Path.Combine(kok, "paper.jar"), overwrite: true);
            KurDurum("Yerel jar kopyalandı: " + Path.GetFileName(src) + " — Kur ve Başlat'a basabilirsin.");
        }
        catch (Exception ex) { KurDurum("Seçilemedi: " + ex.Message); }
    }

    // ---- SERVERİM ----
    private void BtnServerim_Click(object? s, RoutedEventArgs e)
    {
        GrdServerimModal.IsVisible = true;
        ServerimGorunumunuGuncelle();
    }

    // Orijinal ServerimYenile portu.
    private void ServerimGorunumunuGuncelle()
    {
        string? onceki = _seciliSunucu?.Ad;
        _seciliSunucu = null;
        LstSunucular.Items.Clear();
        var liste = SunucuYonetimi.YerelSunuculariTara();
        foreach (var k in liste)
            LstSunucular.Items.Add($"{k.Ad}  [:{k.Port}]  {k.Surum}{(k.Calisiyor ? "  • açık" : "")}");
        if (liste.Count == 0)
        {
            TxtServerProps.Text = "";
            TxtServerimDurum.Text = "Kayıtlı sunucu yok. 'Yeni Sunucu Kur' ile kur.";
            return;
        }
        int sec = Math.Max(0, liste.FindIndex(k => k.Ad == onceki));
        LstSunucular.SelectedIndex = sec;
    }

    private void LstSunucular_Secim(object? s, SelectionChangedEventArgs e) => SeciliSunucuYukle();

    // Orijinal SeciliSunucuYukle portu: seçili sunucunun server.properties'ini yükle.
    private void SeciliSunucuYukle()
    {
        try
        {
            int i = LstSunucular.SelectedIndex;
            var liste = SunucuYonetimi.YerelSunuculariTara();
            if (i < 0 || i >= liste.Count) { _seciliSunucu = null; return; }
            _seciliSunucu = liste[i];
            string props = Path.Combine(_seciliSunucu.Klasor, "server.properties");
            TxtServerProps.Text = File.Exists(props) ? File.ReadAllText(props) : "(server.properties yok)";
            TxtServerimDurum.Text = $"{_seciliSunucu.Ad} — {(_seciliSunucu.Calisiyor ? "çalışıyor" : "duruyor")} (:{_seciliSunucu.Port})";
        }
        catch { }
    }

    private void BtnServerListeYenile_Click(object? s, RoutedEventArgs e) => ServerimGorunumunuGuncelle();

    private void BtnServerYeniKur_Click(object? s, RoutedEventArgs e)
    {
        GrdServerimModal.IsVisible = false;
        BtnSunucuKur_Click(s, e);
    }

    private void BtnServerKlasor_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            if (_seciliSunucu == null) { TxtServerimDurum.Text = "Önce listeden sunucu seç."; return; }
            Process.Start(new ProcessStartInfo(_seciliSunucu.Klasor) { UseShellExecute = true });
        }
        catch (Exception ex) { TxtServerimDurum.Text = "Klasör açılamadı: " + ex.Message; }
    }

    private async void BtnPropsKaydet_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            if (_seciliSunucu == null) { TxtServerimDurum.Text = "Önce listeden sunucu seç."; return; }
            await File.WriteAllTextAsync(Path.Combine(_seciliSunucu.Klasor, "server.properties"), TxtServerProps.Text ?? "");
            TxtServerimDurum.Text = "server.properties kaydedildi (değişiklik için sunucuyu yeniden başlat).";
        }
        catch (Exception ex) { TxtServerimDurum.Text = "Kaydedilemedi: " + ex.Message; }
    }

    private async void BtnServerBaslat_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            if (_seciliSunucu == null) { TxtServerimDurum.Text = "Önce listeden sunucu seç."; return; }
            string klasor = _seciliSunucu.Klasor;
            string jar = Path.Combine(klasor, "paper.jar");
            if (!File.Exists(jar)) { TxtServerimDurum.Text = "paper.jar yok: " + klasor; return; }
            if (SunucuYonetimi.SunucuCalisiyorMu(klasor, _seciliSunucu.Port, out int pid0))
            {
                SunucuCalistirici.Baglan(klasor, _seciliSunucu.Port, pid0);
                TxtServerimDurum.Text = $"Zaten çalışıyor (pid {pid0}, :{_seciliSunucu.Port}).";
                return;
            }
            if (await SunucuCalistirici.BaslaAsync(klasor, 2048) == SunucuCalistirici.BaslaSonucu.Hata)
            { TxtServerimDurum.Text = "Başlatılamadı. Java kurulu mu? (Paper Kur Java'yı otomatik indirir)"; return; }
            TxtServerimDurum.Text = "Başlatma komutu verildi, port yoklanıyor...";
            int port = _seciliSunucu.Port;
            bool ok = await SunucuCalistirici.CevapBekleAsync(port);
            TxtServerimDurum.Text = ok
                ? $"Sunucu açık (:{port})."
                : "Sunucu açılmadı. Son log:\n" + SunucuYonetimi.LogKuyruguOku(klasor);
            ServerimGorunumunuGuncelle();
        }
        catch (Exception ex) { TxtServerimDurum.Text = "Başlatılamadı: " + ex.Message; }
    }

    private void BtnServerDurdur_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            if (_seciliSunucu == null) { TxtServerimDurum.Text = "Önce listeden sunucu seç."; return; }
            // Orijinal: bridger.pid'deki süreci durdur — orphan sunucuları da keser.
            TxtServerimDurum.Text = SunucuCalistirici.Durdur(_seciliSunucu.Klasor);
            ServerimGorunumunuGuncelle();
        }
        catch (Exception ex) { TxtServerimDurum.Text = "Durdurulamadı: " + ex.Message; }
    }

    // ---- AYARLAR ----
    private void BtnAyarlar_Click(object? s, RoutedEventArgs e)
    {
        var (sistem, oto) = LinuxStorage.AyarlariYukle();
        ChkSistemBaslangici.IsChecked = sistem;
        ChkSunucuOtomatik.IsChecked = oto;
        GrdAyarlarModal.IsVisible = true;
    }

    private void BtnAyarKaydet_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool sistem = ChkSistemBaslangici.IsChecked == true;
            bool oto = ChkSunucuOtomatik.IsChecked == true;
            LinuxStorage.OtomatikBaslat(sistem, exe);
            LinuxStorage.AyarlariKaydet(sistem, oto);
            GrdAyarlarModal.IsVisible = false;
        }
        catch { GrdAyarlarModal.IsVisible = false; }
    }
}
