using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Bridger.Android;

// Avalonia penceresi bu aktivitede yaşar; uygulama özel dizinleri ve
// gömülü JVM App.OnFrameworkInitializationCompleted içinde kurulur.
// Not: Avalonia.Android'in aktivitesi AppCompatActivity tabanlıdır; bu
// yüzden tema mutlaka bir AppCompat teması olmalı.
[Activity(
    Label = "Minecraft Bridger",
    Theme = "@style/Theme.AppCompat.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges =
        ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Density |
        ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder);
}
