namespace Bridger.Core;

// Platform kancaları: masaüstü varsayılanlarla çalışır, Android başlığı
// uygulama açılışında buradaki alanları doldurur:
//   - KokGecersizKilma / VeriKokGecersizKilma: uygulama özel klasörleri
//   - KimlikTabanGecersizKilma: ANDROID_ID (cihaz kimliği taban metni)
//   - SunucuBaslatici: gömülü JVM ile sunucu çalıştıran sağlayıcı
// Masaüstünde hiçbiri doldurulmaz; mevcut davranış aynen korunur.
public static class Platform
{
    public static string? KokGecersizKilma { get; set; }
    public static string? VeriKokGecersizKilma { get; set; }
    public static string? KimlikTabanGecersizKilma { get; set; }
    public static ISunucuBaslatici? SunucuBaslatici { get; set; }

    // Android sağlayıcısı takılı mı (süreç modeli yerine gömülü JVM)?
    public static bool GomuluJvm => SunucuBaslatici != null;
}

// Sunucu başlatma sağlayıcısı.
// Masaüstü: null — SunucuCalistirici java sürecini kendisi başlatır.
// Android : gömülü JVM'i başlatan uygulama başlığı doldurur.
public interface ISunucuBaslatici
{
    Task<ISunucuIslemi?> BaslatAsync(string klasor, string jarAdi, int xmxMb,
        Action<string>? cikti, Action<int>? bitti);

    bool CalisiyorMu(string klasor, int port, out int pid);

    // Sunucuyu nazikçe durdur (Android: konsola "stop" yazar).
    string Durdur(string klasor);
}

// Çalışan sunucu işleminin ortak tutamacı (masaüstü süreç / Android JVM).
public interface ISunucuIslemi : IDisposable
{
    bool Calisiyor { get; }
    void KomutGonder(string satir);
    Task<bool> KapanmasiniBekleAsync(int zamanAsimiMs = 15000);
}
