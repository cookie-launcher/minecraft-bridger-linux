using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace Bridger.Core;

// Gömülü JVM: Android'de bionic (Pojav) JRE'sini dlopen zinciriyle yükleyip
// JNI Invocation API ile AYNI süreçte Minecraft sunucusunu çalıştırır.
// Masaüstündeki "java süreci" modelinin Android karşılığıdır.
//
// Akış (tek bir "jvm" iş parçacığında):
//   1) libjli → libjvm → ... sırayla dlopen (RTLD_GLOBAL; bağımlılık çözümü için)
//   2) JNI_CreateJavaVM
//   3) System.setOut/setErr → konsol dosyası, System.setIn → piped akış
//   4) jar MANIFEST'teki Main-Class'ın main(String[]) metodu çağrılır (bloklar)
// Durdurma: piped akışa "stop\n" yazılır; sunucu kapanınca main döner.
public sealed class GomuluJvm : IDisposable
{
    public string JreKok { get; }
    public string CalismaKok { get; }
    public string KonsolDosyasi { get; }

    private IntPtr _vm;
    private IntPtr _girisRef;      // PipedOutputStream global ref
    private IntPtr _yazMid;        // OutputStream.write(int) metod kimliği
    private Thread? _jvmIsParcacigi;
    private Thread? _komutIsParcacigi;
    private readonly BlockingCollection<string> _komutlar = new();
    private readonly ManualResetEventSlim _hazir = new(false);
    private readonly ManualResetEventSlim _bitti = new(false);
    private volatile bool _calisiyor;
    private string? _hata;

    public bool Calisiyor => _calisiyor;
    public string? Hata => _hata;
    public event Action? Bitti;

    public GomuluJvm(string jreKok, string calismaKok)
    {
        JreKok = jreKok;
        CalismaKok = calismaKok;
        KonsolDosyasi = Path.Combine(calismaKok, "sunucu-konsol.log");
    }

    // JVM'i arka planda başlatır (bloklamaz). Hazır olunca HazirBekle döner.
    public void Baslat(int xmxMb, string jarYolu, string[] programArgs, string? anaSinif = null)
    {
        if (_jvmIsParcacigi != null) throw new InvalidOperationException("JVM zaten başlatıldı.");
        Directory.CreateDirectory(CalismaKok);
        Directory.CreateDirectory(Path.Combine(CalismaKok, "tmp"));
        Directory.CreateDirectory(Path.Combine(CalismaKok, "kullanici"));

        _jvmIsParcacigi = new Thread(() => JvmDongusu(xmxMb, jarYolu, programArgs, anaSinif))
        {
            IsBackground = true,
            Name = "gomulu-jvm",
        };
        _jvmIsParcacigi.Start();
    }

    // VM kuruldu ve main çağrılmak üzere mi (ya da hata mı oldu)?
    public bool HazirBekle(int zamanAsimiMs) => _hazir.Wait(zamanAsimiMs);

    public async Task<bool> KapanmasiniBekleAsync(int zamanAsimiMs)
        => await Task.Run(() => _bitti.Wait(zamanAsimiMs));

    // Sunucu konsoluna komut yaz (ör. "stop", "say merhaba").
    public void KomutGonder(string komut)
    {
        if (!_calisiyor || !_hazir.IsSet) return;
        try { _komutlar.Add(komut); } catch { }
    }

    public void Durdur() => KomutGonder("stop");

    private void JvmDongusu(int xmxMb, string jarYolu, string[] programArgs, string? anaSinif)
    {
        try
        {
            // 1) Kütüphane zinciri (Pojav sırası; olmayanlar atlanır).
            string lib = Path.Combine(JreKok, "lib");
            foreach (string goreli in new[]
            {
                "libjli.so",
                "server/libjvm.so",
                "libverify.so",
                "libjava.so",
                "libnet.so",
                "libnio.so",
                "libzip.so",
                "libjimage.so",
                "libextnet.so",
                "libmanagement.so",
                "libmanagement_ext.so",
                "libmanagement_agent.so",
                "libinstrument.so",
                "librmi.so",
                "libprefs.so",
                "libjaas.so",
                "libj2pkcs11.so",
                "libj2gss.so",
                "libsctp.so",
                "libdt_socket.so",
                "libsyslookup.so",
                "libjsig.so",
                "server/libjsig.so",
            })
            {
                string yol = Path.Combine(lib, goreli.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(yol)) Jni.Dlopen(yol);
            }

            // 2) JNI_CreateJavaVM (libjvm'den).
            string jvmYolu = Path.Combine(lib, "server", "libjvm.so");
            if (!File.Exists(jvmYolu))
                throw new FileNotFoundException("libjvm.so bulunamadı: " + jvmYolu);
            IntPtr jvmHandle = Jni.Dlopen(jvmYolu);
            if (jvmHandle == IntPtr.Zero)
                throw new InvalidOperationException("libjvm.so yüklenemedi.");
            IntPtr createFn = System.Runtime.InteropServices.NativeLibrary.GetExport(jvmHandle, "JNI_CreateJavaVM");

            string ana = anaSinif ?? AnaSinifBul(jarYolu)
                ?? throw new InvalidOperationException("MANIFEST'te Main-Class yok: " + jarYolu);

            // Paperclip/vanilla göreli yolları CWD'ye çözer; masaüstüyle aynı düzen
            // için CWD = sunucu klasörü (world/, plugins/, cache/ orada oluşsun).
            string sunucuKok = Path.GetDirectoryName(Path.GetFullPath(jarYolu))!;

            var secenekler = new List<string>
            {
                $"-Xmx{xmxMb}M",
                "-Xrs",
                "-XX:+UseSerialGC",
                "-XX:-UsePerfData",
                "-Djava.home=" + JreKok,
                "-Djava.class.path=" + jarYolu,
                "-Djava.awt.headless=true",
                "-Dfile.encoding=UTF-8",
                "-Dstdout.encoding=UTF-8",
                "-Dstderr.encoding=UTF-8",
                "-Dsun.jnu.encoding=UTF-8",
                "-Duser.home=" + Path.Combine(CalismaKok, "kullanici"),
                "-Duser.dir=" + sunucuKok,
                "-Djava.io.tmpdir=" + Path.Combine(CalismaKok, "tmp"),
                "-Duser.language=tr",
                "-Duser.country=TR",
                "-Dterminal.jline=false",
                "-Dterminal.ansi=false",
                // Pojav JRE'sinin sürüm dizesi "21.0.1-internal" gibi GA olmayan bir
                // dizedir; Paper bunu reddediyor. Paper'ın kendi resmi atlama anahtarı
                // (Main.java: Boolean.getBoolean("Paper.IgnoreJavaVersion")).
                "-DPaper.IgnoreJavaVersion=true",
            };

            // Paperclip göreli yolları işletim sistemi CWD'sine göre çözer (user.dir
            // özelliği syscall'ları etkilemez); sunucu klasörüne geçmezsek "cache/..."
            // Android'in yazılamayan /cache dizinine çözülür. CWD = sunucu klasörü.
            try { Jni.Chdir(sunucuKok); } catch { }

            var vmSonuc = Jni.CreateJavaVm(createFn, secenekler.ToArray());
            _vm = vmSonuc.vm;
            IntPtr env = vmSonuc.env;

            // 3) Konsol yönlendirme: System.out/err → konsol dosyası.
            IntPtr fosCls = Jni.FindClass(env, "java/io/FileOutputStream");
            IntPtr fosKurucu = Jni.GetMethodID(env, fosCls, "<init>", "(Ljava/lang/String;Z)V");
            IntPtr fos = Jni.NewObject(env, fosCls, fosKurucu, Jni.NewStringUTF(env, KonsolDosyasi), (IntPtr)1);

            IntPtr psCls = Jni.FindClass(env, "java/io/PrintStream");
            IntPtr psKurucu = Jni.GetMethodID(env, psCls, "<init>", "(Ljava/io/OutputStream;)V");
            IntPtr ps = Jni.NewObject(env, psCls, psKurucu, fos);

            IntPtr sysCls = Jni.FindClass(env, "java/lang/System");
            Jni.CallStaticVoidMethodA(env, sysCls,
                Jni.GetStaticMethodID(env, sysCls, "setOut", "(Ljava/io/PrintStream;)V"), ps);
            Jni.CallStaticVoidMethodA(env, sysCls,
                Jni.GetStaticMethodID(env, sysCls, "setErr", "(Ljava/io/PrintStream;)V"), ps);

            // Giriş: PipedInputStream ↔ PipedOutputStream; System.in = piped giriş.
            IntPtr pinCls = Jni.FindClass(env, "java/io/PipedInputStream");
            IntPtr pin = Jni.NewObject(env, pinCls, Jni.GetMethodID(env, pinCls, "<init>", "()V"));
            IntPtr poutCls = Jni.FindClass(env, "java/io/PipedOutputStream");
            IntPtr pout = Jni.NewObject(env, poutCls, Jni.GetMethodID(env, poutCls, "<init>", "()V"));
            IntPtr baglaMid = Jni.GetMethodID(env, poutCls, "connect", "(Ljava/io/PipedInputStream;)V");
            Jni.CallVoidMethodRef(env, pout, baglaMid, pin);
            Jni.CallStaticVoidMethodA(env, sysCls,
                Jni.GetStaticMethodID(env, sysCls, "setIn", "(Ljava/io/InputStream;)V"), pin);

            _yazMid = Jni.GetMethodID(env, poutCls, "write", "(I)V");
            _girisRef = Jni.NewGlobalRef(env, pout);

            // 4) main(String[]) — bloklar; sunucu bu çağrı boyunca çalışır.
            IntPtr anaCls = Jni.FindClass(env, ana.Replace('.', '/'));
            IntPtr mainMid = Jni.GetStaticMethodID(env, anaCls, "main", "([Ljava/lang/String;)V");
            IntPtr strCls = Jni.FindClass(env, "java/lang/String");
            IntPtr argsDizi = Jni.NewObjectArray(env, programArgs.Length, strCls, IntPtr.Zero);
            for (int i = 0; i < programArgs.Length; i++)
                Jni.SetObjectArrayElement(env, argsDizi, i, Jni.NewStringUTF(env, programArgs[i]));

            _calisiyor = true;
            KomutIsParcacigiBaslat();
            _hazir.Set();

            Jni.CallStaticVoidMethodA(env, anaCls, mainMid, argsDizi);
        }
        catch (Exception ex)
        {
            _hata = ex.Message;
        }
        finally
        {
            _calisiyor = false;
            _hazir.Set();
            try { _komutlar.CompleteAdding(); } catch { }
            _bitti.Set();
            try { Bitti?.Invoke(); } catch { }
        }
    }

    // Konsol komutlarını tek bir iliştirilmiş iş parçacığından yazar.
    private void KomutIsParcacigiBaslat()
    {
        _komutIsParcacigi = new Thread(() =>
        {
            try
            {
                IntPtr env = Jni.AttachThread(_vm);
                foreach (string komut in _komutlar.GetConsumingEnumerable())
                {
                    foreach (char c in komut + "\n")
                        Jni.CallVoidMethodInt(env, _girisRef, _yazMid, c);
                }
            }
            catch { }
        })
        {
            IsBackground = true,
            Name = "gomulu-jvm-komut",
        };
        _komutIsParcacigi.Start();
    }

    // Boot jar'ın MANIFEST.MF dosyasından Main-Class okur (72 bayt satır katlaması destekli).
    public static string? AnaSinifBul(string jarYolu)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarYolu);
            var giris = zip.GetEntry("META-INF/MANIFEST.MF");
            if (giris == null) return null;
            using var akim = giris.Open();
            using var okuyucu = new StreamReader(akim, Encoding.UTF8);
            string? satir;
            string? sonAnahtar = null;
            var degerler = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while ((satir = okuyucu.ReadLine()) != null)
            {
                if (satir.Length == 0) { sonAnahtar = null; continue; }
                if (satir[0] == ' ' && sonAnahtar != null) { degerler[sonAnahtar] += satir[1..]; continue; }
                int iki = satir.IndexOf(':');
                if (iki <= 0) continue;
                sonAnahtar = satir[..iki];
                degerler[sonAnahtar] = satir[(iki + 1)..].TrimStart();
            }
            return degerler.TryGetValue("Main-Class", out string? mc) && mc.Length > 0 ? mc : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _komutlar.CompleteAdding(); } catch { }
        // DestroyJavaVM bilinçli olarak çağrılmaz (asılı kalabilir); süreç yaşamı yeterli.
    }
}
