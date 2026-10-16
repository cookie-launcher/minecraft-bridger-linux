using System.ComponentModel;

namespace Bridger.Core;

// Decompiled kaynaktan birebir taşınan modeller (WPF'siz).
// Kaynak: /tmp/bridger-src/MinecraftBridger/{OdaModel.cs,MainWindow.cs}
// 16.10.2026: en yeni resmi sürüm (mc.jstudio.com.tr/indir) alanları eklendi:
// Sahip, DnsAdi, OdaDurumu, YerelDogrulandi + rozet/görünüm özellikleri.
public sealed class OdaModel : INotifyPropertyChanged
{
    private string _id = "";
    private string _odaAdi = "";
    private string _kurucu = "";
    private string _kategori = "";
    private int _aktifOyuncu;
    private int _maksOyuncu;
    private string _surum = "";
    private string _maskeliIp = "";
    private bool _herkeseAcik = true;
    private bool _benimOdam;
    private int _yerelPort = 25565;
    private int _portKategorisi = 2;
    private bool _lanGoster = true;
    private string _yerelDogrulandi = "";
    private string _sahip = "";
    private string _dnsAdi = "";
    private string _odaDurumu = "acik";

    public string Id { get => _id; set { _id = value; OnPropertyChanged(nameof(Id)); } }
    public string OdaAdi { get => _odaAdi; set { _odaAdi = value; OnPropertyChanged(nameof(OdaAdi)); } }
    public string Kurucu { get => _kurucu; set { _kurucu = value; OnPropertyChanged(nameof(Kurucu)); } }
    public string Kategori { get => _kategori; set { _kategori = value; OnPropertyChanged(nameof(Kategori)); } }
    public int AktifOyuncu { get => _aktifOyuncu; set { _aktifOyuncu = value; OnPropertyChanged(nameof(AktifOyuncu)); OnPropertyChanged(nameof(OyuncuSayisiMetni)); OnPropertyChanged(nameof(DolulukOrani)); } }
    public int MaksOyuncu { get => _maksOyuncu; set { _maksOyuncu = value; OnPropertyChanged(nameof(MaksOyuncu)); OnPropertyChanged(nameof(OyuncuSayisiMetni)); OnPropertyChanged(nameof(DolulukOrani)); } }
    public string Surum { get => _surum; set { _surum = value; OnPropertyChanged(nameof(Surum)); } }
    public string MaskeliIp { get => _maskeliIp; set { _maskeliIp = value; OnPropertyChanged(nameof(MaskeliIp)); } }

    public bool HerkeseAcik
    {
        get => _herkeseAcik;
        set
        {
            _herkeseAcik = value;
            OnPropertyChanged(nameof(HerkeseAcik));
            OnPropertyChanged(nameof(KatilGorunumu));
            OnPropertyChanged(nameof(YayinlaGorunumu));
            OnPropertyChanged(nameof(DurumRozeti));
            OnPropertyChanged(nameof(RozetGorunumu));
            OnPropertyChanged(nameof(DurumGorunumu));
        }
    }

    public bool BenimOdam
    {
        get => _benimOdam;
        set
        {
            _benimOdam = value;
            OnPropertyChanged(nameof(BenimOdam));
            OnPropertyChanged(nameof(KatilGorunumu));
            OnPropertyChanged(nameof(YayinlaGorunumu));
            OnPropertyChanged(nameof(DurumRozeti));
            OnPropertyChanged(nameof(RozetGorunumu));
        }
    }

    public int YerelPort { get => _yerelPort; set { _yerelPort = value; OnPropertyChanged(nameof(YerelPort)); } }
    public int PortKategorisi { get => _portKategorisi; set { _portKategorisi = value; OnPropertyChanged(nameof(PortKategorisi)); } }
    public bool LanGoster { get => _lanGoster; set { _lanGoster = value; OnPropertyChanged(nameof(LanGoster)); } }
    public string YerelDogrulandi { get => _yerelDogrulandi; set { _yerelDogrulandi = value ?? ""; OnPropertyChanged(nameof(YerelDogrulandi)); } }
    public string Sahip { get => _sahip; set { _sahip = value ?? ""; OnPropertyChanged(nameof(Sahip)); } }
    public string DnsAdi { get => _dnsAdi; set { _dnsAdi = value ?? ""; OnPropertyChanged(nameof(DnsAdi)); } }

    public string OdaDurumu
    {
        get => _odaDurumu;
        set
        {
            _odaDurumu = string.IsNullOrEmpty(value) ? "acik" : value;
            OnPropertyChanged(nameof(OdaDurumu));
            OnPropertyChanged(nameof(DurumMetni));
            OnPropertyChanged(nameof(AcikMi));
            OnPropertyChanged(nameof(DurumArkaRengiHex));
        }
    }

    public string DurumMetni => _odaDurumu == "kapali" ? "Kapalı" : "Açık";
    public bool AcikMi => _odaDurumu != "kapali";
    public string DurumArkaRengiHex => _odaDurumu == "kapali" ? "#C62828" : "#2E7D32";

    // Kart görünümleri (WPF Visibility karşılıkları; Avalonia IsVisible bool bekler).
    public bool KatilGorunumu => HerkeseAcik;
    public bool YayinlaGorunumu => BenimOdam && !HerkeseAcik;
    public bool RozetGorunumu => BenimOdam;
    public bool DurumGorunumu => HerkeseAcik;

    // Orijinal DurumRozeti: herkese açıksa "YAYINDA", kendi özel taslağıysa "OZEL".
    public string DurumRozeti => HerkeseAcik ? (BenimOdam ? "YAYINDA" : "") : (BenimOdam ? "OZEL" : "");

    public string OyuncuSayisiMetni => $"{AktifOyuncu}/{(MaksOyuncu > 0 ? MaksOyuncu : 8)}";
    public double DolulukOrani => MaksOyuncu <= 0 ? 0.0 : Math.Min(100.0, Math.Max(0.0, (double)AktifOyuncu / MaksOyuncu * 100.0));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class WsFrame
{
    public byte Opcode;
    public byte[] Yuk = Array.Empty<byte>();
}

public sealed class TunnelOturum
{
    public string Id = "";
    public int YerelPort;
}

public sealed class SunucuKaydi
{
    public string Ad = "";
    public string Klasor = "";
    public string Surum = "";
    public string Motd = "";
    public int Port = 25565;
    public bool Calisiyor;
}
