using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Bridger.Core;

namespace Bridger.Avalonia;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<OdaModel> _tumOdalar = new();
    private readonly ObservableCollection<OdaModel> _filtreliOdalar = new();
    private string _seciliKategori = "Tüm Odalar";
    private readonly HesapOturumu _oturum = new();
    private Process? _serverProc;
    private TunnelIstemcisi? _aktifTunnel;
    private LanIlani? _lanIlani;
    private SunucuKaydi? _seciliSunucu;

    public MainWindow()
    {
        InitializeComponent();
        IcOdalar.ItemsSource = _filtreliOdalar;
        Closing += (_, _) =>
        {
            try { _aktifTunnel?.Dispose(); _aktifTunnel = null; } catch { }
            try { _lanIlani?.Dispose(); _lanIlani = null; } catch { }
        };
        _ = BaslangicAsync();
    }

    private async Task BaslangicAsync()
    {
        if (await HesapService.OtomatikGirisDeneAsync(_oturum))
        {
            TxtHesapBilgi.Text = _oturum.BilgiMetni();
            if (!string.IsNullOrEmpty(_oturum.ProfilMotd)) TxtKurMotd.Text = _oturum.ProfilMotd;
            // Orijinal DurumuYukleAsync sonu: "sunucuOtomatik" açıksa kurulu sunucuları başlat.
            SunuculariOtomatikBaslat();
        }
        await OdalariSunucudanYukleAsync();
    }

    // Orijinal: _ayarSunucuOtomatik ise portu kapalı olan kurulu sunucuları başlat.
    private void SunuculariOtomatikBaslat()
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
                        SunucuyuBaslat(sk.Klasor, SunucuYonetimi.KlasorJavaExe(sk.Klasor));
                }
                catch { }
            }
        }
        catch { }
    }

    // ---- ODA LİSTESİ ----
    private async Task OdalariSunucudanYukleAsync()
    {
        try
        {
            BtnYenile.IsEnabled = false;
            TxtSunucuDurum.Text = "Sunucudan odalar alınıyor...";
            var gelen = await OdaService.OdalariGetirAsync();
            _tumOdalar.Clear();
            foreach (var o in gelen) _tumOdalar.Add(o);
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

    private void KategoriFiltresiniUygula()
    {
        _filtreliOdalar.Clear();
        foreach (var o in _tumOdalar)
            if (_seciliKategori == "Tüm Odalar" ||
                string.Equals(o.Kategori, _seciliKategori, StringComparison.OrdinalIgnoreCase))
                _filtreliOdalar.Add(o);
        TxtOdaSayisiBadge.Text = $"{_filtreliOdalar.Count} Aktif Oda";
        TxtAktifKategoriBaslik.Text = _seciliKategori;
        BrdBosListe.IsVisible = _filtreliOdalar.Count == 0;
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
        string ip = oda.MaskeliIp;
        try
        {
            // Orijinal SunucuyaKatilmaSinyaliGonderAsync: sunucudan taze tünel IP'si iste.
            string? taze = await OdaService.OdayaKatilSinyaliAsync(oda, _oturum.EkranNicki("Oyuncu"));
            if (!string.IsNullOrEmpty(taze)) { oda.MaskeliIp = taze; ip = taze; }
        }
        catch { }
        try
        {
            var cb = TopLevel.GetTopLevel(this)?.Clipboard;
            if (cb != null)
            {
                var dt = new global::Avalonia.Input.DataTransfer();
                dt.Add(global::Avalonia.Input.DataTransferItem.CreateText(ip));
                await cb.SetDataAsync(dt);
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

    private void BtnOdaYarat_Click(object? s, RoutedEventArgs e)
    {
        GrdOdaYaratModal.IsVisible = true;
        _ = OdaYaratSurumleriniDoldurAsync();
    }

    private async Task OdaYaratSurumleriniDoldurAsync()
    {
        await SurumListesiniYukleAsync();
        string? onceki = CmbYeniOdaSurum.SelectedItem?.ToString();
        CmbYeniOdaSurum.Items.Clear();
        foreach (var v in _surumler) CmbYeniOdaSurum.Items.Add(v);
        int idx = onceki != null ? _surumler.IndexOf(onceki) : 0;
        CmbYeniOdaSurum.SelectedIndex = idx >= 0 ? idx : 0;
    }

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

    private async void BtnYayinlaOnay_Click(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtYerelPort.Text?.Trim(), out int port)) port = 25565;
        var (motd, durumJson) = await McProtocol.YerelDurumSorAsync("127.0.0.1", port, zamanAsimiMs: 3000);
        if (motd == null)
        {
            TxtPortSinaSonuc.Text = $"Yerel {port} portunda Minecraft bulunamadı. Yine de yayınlansın mı dersen portu düzelt.";
            return;
        }
        // İlan edilen sürüm, tünele bağlı GERÇEK sunucununki olmalı; yoksa
        // 26.3 istemcisi 1.20.1 sunucuya girip "outdated server" yer.
        string? gercekSurum = SurumuAlgila(durumJson);
        string secili = (CmbYeniOdaSurum.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? CmbYeniOdaSurum.SelectedItem?.ToString() ?? "1.20.1";
        if (!string.IsNullOrEmpty(gercekSurum) &&
            !string.Equals(secili, gercekSurum, StringComparison.OrdinalIgnoreCase))
        {
            SurumuAyarla(gercekSurum);
            secili = gercekSurum;
            TxtPortSinaSonuc.Text = $"Sürüm uyumsuzdu, sunucunun gerçek sürümüne ayarlandı: {gercekSurum}.";
        }
        try
        {
            TxtPortSinaSonuc.Text = "Sunucuya yayınlanıyor...";
            var oda = new OdaModel
            {
                OdaAdi = string.IsNullOrWhiteSpace(TxtYeniOdaAdi.Text) ? "Minecraft Odam" : TxtYeniOdaAdi.Text!.Trim(),
                Kurucu = _oturum.EkranNicki(),
                Kategori = (CmbYeniOdaKategori.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Survival",
                Surum = secili,
                AktifOyuncu = 1,
                MaksOyuncu = int.TryParse((CmbYeniOdaMaksOyuncu.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int m) ? m : 8,
                YerelPort = port,
            };
            string? maskeli = null;
            try { _aktifTunnel?.Dispose(); } catch { }
            var tunnel = new TunnelIstemcisi();
            tunnel.OdaKapatildi += YayindanDusur;
            maskeli = await tunnel.YayinlaAsync(oda, port,
                int.TryParse((CmbYeniOdaMaksOyuncu.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int mm) ? mm : 20,
                _oturum.Token);
            if (string.IsNullOrEmpty(maskeli))
            {
                try { tunnel.Dispose(); } catch { }
                TxtPortSinaSonuc.Text = "Sunucuya yayınlanamadı. Bağlantını kontrol edip tekrar dene.";
                return;
            }
            _aktifTunnel = tunnel;
            oda.MaskeliIp = maskeli;
            GrdOdaYaratModal.IsVisible = false;
            BrdHostBilgiBandi.IsVisible = true;
            TxtHostBilgiMetni.Text = $"Odan Yayında: '{oda.OdaAdi}' | Yerel hedef: {port} | Tünel IP: {maskeli}";
            // Orijinal: yayın sonrası LAN ilanı + katılım bildirimi.
            if (ChkLanGoster.IsChecked == true)
            {
                _lanIlani ??= new LanIlani();
                _lanIlani.Baslat(oda.OdaAdi, port);
            }
            else _lanIlani?.Durdur();
            _ = HesapService.KatilimBildirAsync(_oturum);
            await OdalariSunucudanYukleAsync();
        }
        catch { TxtPortSinaSonuc.Text = "Yayınlanamadı, tekrar dene."; }
    }

    // Orijinal YayindanDusur + OdayiVeBaglantilariKapat karşılığı.
    private void YayindanDusur(string mesaj)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { _aktifTunnel?.Dispose(); } catch { }
            _aktifTunnel = null;
            try { _lanIlani?.Durdur(); } catch { }
            BrdHostBilgiBandi.IsVisible = false;
            TxtHostBilgiMetni.Text = "";
            if (!string.IsNullOrWhiteSpace(mesaj)) TxtTunelDurum.Text = mesaj;
            _ = OdalariSunucudanYukleAsync();
        });
    }

    private void BtnOdayiKapat_Click(object? s, RoutedEventArgs e) => YayindanDusur("Yayın kapatıldı.");

    // ---- HESAP ----
    private void BtnHesap_Click(object? s, RoutedEventArgs e)
    {
        TxtHesapBilgi.Text = string.IsNullOrEmpty(_oturum.Kullanici) ? "Giriş yapmadın" : _oturum.BilgiMetni();
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
                SunucuYonetimi.PidDosyasiniTazele(klasor);
                KurDurum($"Sunucu zaten çalışıyor (pid {calisanPid}, :{mevcutPort0}). Durum kontrol ediliyor...");
                var (ms, _) = await McProtocol.YerelDurumSorAsync("127.0.0.1", mevcutPort0, zamanAsimiMs: 2000);
                if (ms != null || await SunucuCevapBekleAsync(mevcutPort0, null, calisanPid, klasor))
                {
                    GrdSunucuKurModal.IsVisible = false;
                    ServerimGorunumunuGuncelle();
                    KurDurum($"Sunucun zaten hazır ve çalışıyor! (:{mevcutPort0}) ODA YARAT ile yayınlayabilirsin.");
                    return;
                }
                KurDurum("Sunucu süreci ayakta ama yanıt vermiyor. Son log:\n" + SunucuYonetimi.LogKuyruguOku(klasor));
                return;
            }
            if (File.Exists(Path.Combine(klasor, "server.properties")) && File.Exists(Path.Combine(klasor, "paper.jar")))
            {
                KurDurum("Mevcut sunucu bulundu, başlatılıyor...");
                if (SunucuyuBaslat(klasor, SunucuYonetimi.KlasorJavaExe(klasor)) &&
                    await SunucuCevapBekleAsync(PortOku(klasor), _serverProc, null, klasor))
                {
                    int p0 = PortOku(klasor);
                    GrdSunucuKurModal.IsVisible = false;
                    ServerimGorunumunuGuncelle();
                    KurDurum($"Sunucun hazır ve çalışıyor! (:{p0}) Şimdi ODA YARAT ile yayınlayabilirsin.");
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
            if (!SunucuyuBaslat(klasor, javaExe)) { KurDurum("Başlatılamadı (klasörü açıp start.sh dosyasını el ile çalıştır)"); return; }
            KurDurum("Sunucu açılıyor, port yoklanıyor...");
            if (await SunucuCevapBekleAsync(hedefPort, _serverProc, null, klasor))
            {
                GrdSunucuKurModal.IsVisible = false;
                ServerimGorunumunuGuncelle();
                KurDurum($"Sunucun hazır ve çalışıyor! (:{hedefPort}) Şimdi ODA YARAT ile yayınlayabilirsin.");
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

    private bool SunucuyuBaslat(string klasor, string javaExe)
    {
        try
        {
            // Aynı klasörde çalışan sunucu varsa ikinci kopya açma (world/session.lock).
            if (SunucuYonetimi.SunucuCalisiyorMu(klasor, PortOku(klasor), out _))
            {
                SunucuYonetimi.PidDosyasiniTazele(klasor);
                return true;
            }
            _serverProc = Process.Start(new ProcessStartInfo(javaExe, "-Xmx2G -jar paper.jar nogui")
            {
                WorkingDirectory = klasor, UseShellExecute = false,
            });
            if (_serverProc != null)
            {
                try { File.WriteAllText(Path.Combine(klasor, "bridger.pid"), _serverProc.Id.ToString()); } catch { }
                return true;
            }
        }
        catch { }
        return false;
    }

    // MC cevabı gelene kadar bekler (40×3 sn); süreç/pid öldüyse erken çıkar.
    private async Task<bool> SunucuCevapBekleAsync(int port, Process? proc, int? pid, string klasor)
    {
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(3000);
            if (proc is { HasExited: true }) return false;
            if (pid is > 0 && !Directory.Exists($"/proc/{pid.Value}")) return false;
            var (m, _) = await McProtocol.YerelDurumSorAsync("127.0.0.1", port, zamanAsimiMs: 1000);
            if (m != null) return true;
        }
        return false;
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
                SunucuYonetimi.PidDosyasiniTazele(klasor);
                TxtServerimDurum.Text = $"Zaten çalışıyor (pid {pid0}, :{_seciliSunucu.Port}).";
                return;
            }
            if (!SunucuyuBaslat(klasor, SunucuYonetimi.KlasorJavaExe(klasor)))
            { TxtServerimDurum.Text = "Başlatılamadı. Java kurulu mu? (SUNUCU KUR Java'yı otomatik indirir)"; return; }
            TxtServerimDurum.Text = "Başlatma komutu verildi, port yoklanıyor...";
            int port = _seciliSunucu.Port;
            bool ok = await SunucuCevapBekleAsync(port, _serverProc, null, klasor);
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
            TxtServerimDurum.Text = SunucuYonetimi.SunucuyuDurdur(_seciliSunucu.Klasor);
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
