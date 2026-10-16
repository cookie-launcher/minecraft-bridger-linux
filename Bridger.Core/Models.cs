using System.ComponentModel;

namespace Bridger.Core;

// Decompiled kaynaktan birebir taşınan modeller (WPF'siz).
// Kaynak: /tmp/bridger-src/MinecraftBridger/{OdaModel.cs,MainWindow.cs}

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

    public string Id { get => _id; set { _id = value; OnPropertyChanged(nameof(Id)); } }
    public string OdaAdi { get => _odaAdi; set { _odaAdi = value; OnPropertyChanged(nameof(OdaAdi)); } }
    public string Kurucu { get => _kurucu; set { _kurucu = value; OnPropertyChanged(nameof(Kurucu)); } }
    public string Kategori { get => _kategori; set { _kategori = value; OnPropertyChanged(nameof(Kategori)); } }
    public int AktifOyuncu { get => _aktifOyuncu; set { _aktifOyuncu = value; OnPropertyChanged(nameof(AktifOyuncu)); OnPropertyChanged(nameof(OyuncuSayisiMetni)); OnPropertyChanged(nameof(DolulukOrani)); } }
    public int MaksOyuncu { get => _maksOyuncu; set { _maksOyuncu = value; OnPropertyChanged(nameof(MaksOyuncu)); OnPropertyChanged(nameof(OyuncuSayisiMetni)); OnPropertyChanged(nameof(DolulukOrani)); } }
    public string Surum { get => _surum; set { _surum = value; OnPropertyChanged(nameof(Surum)); } }
    public string MaskeliIp { get => _maskeliIp; set { _maskeliIp = value; OnPropertyChanged(nameof(MaskeliIp)); } }
    public bool HerkeseAcik { get => _herkeseAcik; set { _herkeseAcik = value; OnPropertyChanged(nameof(HerkeseAcik)); } }
    public bool BenimOdam { get => _benimOdam; set { _benimOdam = value; OnPropertyChanged(nameof(BenimOdam)); } }
    public int YerelPort { get => _yerelPort; set { _yerelPort = value; OnPropertyChanged(nameof(YerelPort)); } }
    public int PortKategorisi { get => _portKategorisi; set { _portKategorisi = value; OnPropertyChanged(nameof(PortKategorisi)); } }
    public bool LanGoster { get => _lanGoster; set { _lanGoster = value; OnPropertyChanged(nameof(LanGoster)); } }

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
