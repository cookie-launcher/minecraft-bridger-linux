# Minecraft Bridger — Linux Portu

Windows/WPF uygulaması **MinecraftBridger.exe**'nin Linux için topluluk portu.
Wine'sız, doğrudan .NET 8 üzerinde çalışır. Arayüz Avalonia ile, WPF orijinaline
mümkün olduğunca yakın sadelikte yeniden yazılmıştır.

## Özellikler

- Oda listesi + oda yayınlama (WebSocket tüneli: `CREATE_ROOM`, oyuncu trafiği pompası, `DURUM_SOR` canlı durum)
- Native **Wayland** desteği (Avalonia 12.1 Wayland backend'i; Wayland yoksa otomatik X11'e düşer, `BRIDGER_X11=1` ile X11 zorlanır)
- LAN ilanı (224.0.2.60:4445 çoklu yayını; Minecraft "LAN Dünyaları" listesinde görünür)
- Odaya katılma sinyali (`JOIN_ROOM`) ve panoya tünel IP'si kopyalama
- Sunucu kurulumu: Paper indirme (katalog + `fill.papermc.io`), eula, `server.properties`, ops/whitelist (çevrimdışı UUID), `start.sh`
- Serverim ekranı: durum listesi, server.properties düzenleme, Başlat/Durdur (pid denetimli), Klasörü Aç
- Hesap sistemi: giriş/kayıt, beni hatırla (kalıcı oturum), plan/kota bilgisi
- Ayarlar: sistem başlangıcı, sunucuları otomatik başlatma

## Gereksinimler

- Linux x64
- [.NET 8 SDK](https://dotnet.microsoft.com/) (derlemek için; yayınlanmış ikili self-contained'dır)
- Java 17+ (Paper sunucusu çalıştırmak için)

## Derleme ve Çalıştırma

```bash
# UI
dotnet publish Bridger.Avalonia -c Release -r linux-x64 --self-contained -o yayin-ui
./yayin-ui/Bridger.Avalonia

# Konsol (test araçları: odalar, mc-status, yayinla, katil, java, kimlik, yollar)
dotnet publish Bridger.Linux -c Release -r linux-x64 --self-contained -o yayin
./yayin/Bridger.Linux odalar
```

## Proje Yapısı

| Klasör | İçerik |
|---|---|
| `Bridger.Core` | Protokol ve iş mantığı: `McProtocol`, `WsTransport`, `TunnelIstemcisi`, `OdaService`, `HesapService`, `SunucuYonetimi`, `LanIlani`, `LinuxStorage` |
| `Bridger.Avalonia` | Masaüstü arayüz (orijinal WPF düzenine yakın) |
| `Bridger.Linux` | Konsol aracı (CLI/test) |

## Notlar

- Windows'a özgü DPAPI, Registry autostart ve WMI donanım kimliği; sırasıyla `0600` izinli dosya,
  XDG autostart `.desktop` ve `/etc/machine-id` ile değiştirilmiştir.
- Bu depo yalnızca kişisel kullanım için bir port çalışmasıdır; orijinal uygulamanın
  ve Paper/Minecraft'ın telif hakları sahiplerine aittir.
