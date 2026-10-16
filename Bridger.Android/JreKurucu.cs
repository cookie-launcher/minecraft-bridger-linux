using System.Formats.Tar;
using System.Runtime.InteropServices;
using SharpCompress.Compressors.Xz;

namespace Bridger.Android;

// Gömülü JRE: APK içindeki (assets/components/jre21) tar.xz paketlerini ilk
// açılışta uygulama veri dizinine çıkarır. Sıra önemli: önce universal, sonra
// bin-<abi> (mimariye özel dosyalar üzerine yazılır) — PojavLauncher ile aynı.
public static class JreKurucu
{
    public static string HedefKok =>
        Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath, "jre21");

    public static bool Hazir => File.Exists(Path.Combine(HedefKok, "release"));

    private static readonly SemaphoreSlim _kilit = new(1, 1);

    public static async Task<bool> GerekirseCikarAsync(Action<string>? durum = null,
        CancellationToken ct = default)
    {
        if (Hazir) return true;
        await _kilit.WaitAsync(ct);
        try
        {
            if (Hazir) return true;
            return await CikarAsync(durum, ct);
        }
        finally { _kilit.Release(); }
    }

    private static async Task<bool> CikarAsync(Action<string>? durum, CancellationToken ct)
    {
        var ctx = global::Android.App.Application.Context;
        string abi = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x86_64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException("Desteklenmeyen mimari: " +
                RuntimeInformation.ProcessArchitecture),
        };

        var mevcut = ctx.Assets!.List("components/jre21") ?? Array.Empty<string>();
        Directory.CreateDirectory(HedefKok);

        foreach (string varlik in new[] { "universal.tar.xz", $"bin-{abi}.tar.xz" })
        {
            if (Array.IndexOf(mevcut, varlik) < 0)
            {
                durum?.Invoke($"JRE paketi bulunamadı: {varlik} (assets eksik?).");
                return false;
            }
            durum?.Invoke("JRE çıkarılıyor: " + varlik);
            string assetYolu = "components/jre21/" + varlik;
            await Task.Run(() => PaketCikar(ctx, assetYolu, ct), ct);
        }

        durum?.Invoke("JRE hazır.");
        return true;
    }

    private static void PaketCikar(global::Android.Content.Context ctx, string assetYolu,
        CancellationToken ct)
    {
        string hedefTam = Path.GetFullPath(HedefKok);
        using var asset = ctx.Assets!.Open(assetYolu);
        using var xz = new XZStream(asset);
        using var tar = new TarReader(xz);

        TarEntry? giris;
        while ((giris = tar.GetNextEntry()) != null)
        {
            ct.ThrowIfCancellationRequested();

            string ad = giris.Name.Replace('\\', '/').TrimStart('/');
            if (ad.Length == 0) continue;
            string yol = Path.GetFullPath(Path.Combine(hedefTam, ad));
            if (!yol.StartsWith(hedefTam, StringComparison.Ordinal)) continue; // zip-slip koruması

            if (giris.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(yol);
                continue;
            }
            if (giris.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(yol)!);
            using (var cikti = File.Create(yol))
                giris.DataStream!.CopyTo(cikti);
            try { File.SetUnixFileMode(yol, giris.Mode); } catch { }
        }
    }
}
