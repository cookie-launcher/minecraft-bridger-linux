using Android.Provider;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bridger.Core;

namespace Bridger.Android;

// Not: "Application" adı Avalonia ve Android'de çakıştığı için tam nitelik
// kullanılır (global::Avalonia.Application).
public class App : global::Avalonia.Application
{
    private static bool _platformKuruldu;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        PlatformKur();

        if (ApplicationLifetime is ISingleViewApplicationLifetime tek)
            tek.MainView = new MainView();

        base.OnFrameworkInitializationCompleted();
    }

    // Core'un platform kancalarını Android'e bağlar (bir kez).
    public static void PlatformKur()
    {
        if (_platformKuruldu) return;
        _platformKuruldu = true;

        var ctx = global::Android.App.Application.Context;
        string files = ctx.FilesDir!.AbsolutePath;

        Platform.KokGecersizKilma = Path.Combine(files, "ayarlar");
        Platform.VeriKokGecersizKilma = Path.Combine(files, "veri");
        Directory.CreateDirectory(Platform.KokGecersizKilma);
        Directory.CreateDirectory(Platform.VeriKokGecersizKilma);

        // Cihaz kimliği tabanı: ANDROID_ID (makine-id Android'de okunamaz).
        Platform.KimlikTabanGecersizKilma =
            Settings.Secure.GetString(ctx.ContentResolver, Settings.Secure.AndroidId);

        // Gömülü JVM sağlayıcısı (JRE ilk açılışta çıkarılır).
        Platform.SunucuBaslatici = new GomuluJvmBaslatici(Path.Combine(files, "jre21"));

        // JRE çıkarımı arka planda (assets varsa; hata olsa da uygulama açılır).
        _ = Task.Run(async () =>
        {
            try { await JreKurucu.GerekirseCikarAsync(); } catch { }
        });
    }
}
