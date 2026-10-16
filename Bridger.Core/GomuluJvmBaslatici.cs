using System.Text;

namespace Bridger.Core;

// Android sunucu sağlayıcısı: gömülü JVM (Pojav jre21) ile Paper'ı uygulama
// sürecinin içinde çalıştırır. Platform.SunucuBaslatici olarak takılır;
// SunucuCalistirici ve SunucuYonetimi otomatik olarak bu sağlayıcıyı kullanır.
public sealed class GomuluJvmBaslatici : ISunucuBaslatici
{
    public string JreKok { get; }

    private GomuluJvm? _jvm;
    private CancellationTokenSource? _kuyrukCts;

    public GomuluJvmBaslatici(string jreKok) { JreKok = jreKok; }

    // Son başlatma hatası (JVM kurulamasaydı vb.).
    public string? Hata => _jvm?.Hata;

    public async Task<ISunucuIslemi?> BaslatAsync(string klasor, string jarAdi, int xmxMb,
        Action<string>? cikti, Action<int>? bitti)
    {
        string jarYolu = Path.Combine(klasor, jarAdi);
        if (!File.Exists(jarYolu)) return null;
        if (_jvm?.Calisiyor == true) return new GomuluIslem(this);

        var jvm = new GomuluJvm(JreKok, Path.Combine(klasor, ".bridger-jvm"));
        jvm.Baslat(xmxMb, jarYolu, new[] { "--nogui" });

        // VM kurulumu 1-5 sn sürer; "hazır" işareti main() ÇAĞRILMADAN hemen
        // önce gelir; dolayısıyla burada bloklamak kabul edilebilir.
        bool hazir = await Task.Run(() => jvm.HazirBekle(600_000));
        _jvm = jvm;
        if (!hazir || jvm.Hata != null) return null;

        if (cikti != null) KuyrukBaslat(jvm, cikti);
        if (bitti != null) jvm.Bitti += () => bitti(jvm.Hata == null ? 0 : 1);
        return new GomuluIslem(this);
    }

    public bool CalisiyorMu(string klasor, int port, out int pid)
    {
        pid = 0;
        return _jvm?.Calisiyor == true || SunucuYonetimi.PortDinliyorMu(port);
    }

    public string Durdur(string klasor)
    {
        if (_jvm?.Calisiyor != true) return "Çalışan sunucu yok.";
        _jvm.Durdur();
        return "Sunucuya kapatma (stop) komutu gönderildi.";
    }

    // Konsol dosyasını kuyruktan okur ve satır satır cikti'ya akar.
    private void KuyrukBaslat(GomuluJvm jvm, Action<string> cikti)
    {
        _kuyrukCts = new CancellationTokenSource();
        var token = _kuyrukCts.Token;
        string yol = jvm.KonsolDosyasi;
        _ = Task.Run(async () =>
        {
            long konum = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(yol))
                    {
                        using var fs = new FileStream(yol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (fs.Length < konum) konum = 0;
                        fs.Seek(konum, SeekOrigin.Begin);
                        using var okuyucu = new StreamReader(fs, Encoding.UTF8);
                        string? satir;
                        while ((satir = await okuyucu.ReadLineAsync(token)) != null)
                        {
                            konum = fs.Position;
                            try { cikti(satir); } catch { }
                        }
                        konum = fs.Position;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
                try { await Task.Delay(400, token); } catch { break; }
            }
        }, token);
    }

    private sealed class GomuluIslem : ISunucuIslemi
    {
        private readonly GomuluJvmBaslatici _sag;
        public GomuluIslem(GomuluJvmBaslatici sag) { _sag = sag; }

        public bool Calisiyor => _sag._jvm?.Calisiyor == true;

        public void KomutGonder(string satir) => _sag._jvm?.KomutGonder(satir);

        public async Task<bool> KapanmasiniBekleAsync(int zamanAsimiMs = 15000)
            => _sag._jvm == null || await _sag._jvm.KapanmasiniBekleAsync(zamanAsimiMs);

        // Sunucu UI'dan bağımsız çalışmaya devam eder; tutamaç bırakmak yeterli.
        public void Dispose() { }
    }
}
