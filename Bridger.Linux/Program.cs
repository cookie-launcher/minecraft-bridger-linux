using Bridger.Core;

// Wine'sız Linux istemcisi — ilk sürüm: konsol.
// WPF UI (MainWindow 5499 satır + BAML) Avalonia'ya taşınana kadar
// aynı protokolleri konsoldan çalıştırır.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        bridger: MinecraftBridger Linux portu
          odalar [--host H] [--port P] [--ssl]   oda listesini çek
          mc-status <host> [port]                yerel/uzak MC sunucuya status sor
          katil <odaId> [oyuncuAdi]              odaya katıl sinyali (taze tünel IP'si)
          yayinla <yerelPort> [ad] [surum] [maks] oda yayınla (Ctrl+C'ye kadar açık kalır)
          java                                  sistem javasını ve sürümünü göster
          kimlik                                donanım kimliği + örnek offline UUID
          yollar                                linux depolama yollarını göster
        """);
    return 0;
}

string host = "mc.jstudio.com.tr"; int port = 80; bool ssl = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
    if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
    if (args[i] == "--ssl") ssl = true;
}

switch (args[0])
{
    case "odalar":
    {
        using var api = new ApiClient(host, port, ssl);
        string json = await api.OdalariGetirAsync();
        Console.WriteLine(json.Length > 4000 ? json[..4000] + "\n...[kısaltıldı]" : json);
        return 0;
    }
    case "mc-status":
    {
        if (args.Length < 2) { Console.Error.WriteLine("kullanım: mc-status <host> [port]"); return 2; }
        string h = args[1]; int p = args.Length > 2 ? int.Parse(args[2]) : 25565;
        var (motd, json) = await McProtocol.YerelDurumSorAsync(h, p);
        Console.WriteLine("MOTD: " + (motd ?? "(alınamadı)"));
        if (json != null) Console.WriteLine(json.Length > 2000 ? json[..2000] + "\n...[kısaltıldı]" : json);
        return motd == null ? 1 : 0;
    }
    case "katil":
    {
        if (args.Length < 2) { Console.Error.WriteLine("kullanım: katil <odaId> [oyuncuAdi]"); return 2; }
        var oda = new OdaModel { Id = args[1] };
        string ad2 = args.Length > 2 ? args[2] : "Oyuncu";
        string? ip = await OdaService.OdayaKatilSinyaliAsync(oda, ad2);
        Console.WriteLine(ip == null ? "yanıt yok (oda kapalı olabilir)" : "taze IP: " + ip);
        return ip == null ? 1 : 0;
    }
    case "yayinla":
    {
        if (args.Length < 2) { Console.Error.WriteLine("kullanım: yayinla <yerelPort> [ad] [surum] [maks]"); return 2; }
        int yp = int.Parse(args[1]);
        string ad = args.Length > 2 ? args[2] : "Konsol Odasi";
        string sv = args.Length > 3 ? args[3] : "1.20.1";
        int maks = args.Length > 4 ? int.Parse(args[4]) : 8;
        var ot = new HesapOturumu();
        await HesapService.OtomatikGirisDeneAsync(ot);
        var tunnel = new TunnelIstemcisi();
        var oda = new OdaModel
        {
            OdaAdi = ad, Kurucu = ot.EkranNicki("Konsol"), Kategori = "Survival",
            Surum = sv, MaksOyuncu = maks, AktifOyuncu = 1, YerelPort = yp,
        };
        string? maskeli = await tunnel.YayinlaAsync(oda, yp, maks, ot.Token);
        if (maskeli == null) { Console.Error.WriteLine("yayınlanamadı (sunucuya ulaşılamadı ya da oda reddedildi)"); return 1; }
        Console.WriteLine("TÜNEL IP: " + maskeli);
        Console.WriteLine("odaId: " + oda.Id);
        Console.WriteLine("Oda canlı — kapatmak için Ctrl+C.");
        var kapanma = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; kapanma.TrySetResult(); };
        await kapanma.Task;
        tunnel.Dispose();
        Console.WriteLine("kapatıldı.");
        return 0;
    }
    case "java":
    {
        string? j = JavaRuntime.SistemJavasiniBul();
        Console.WriteLine("java: " + (j ?? "(bulunamadı)"));
        if (j != null) Console.WriteLine("sürüm: " + (JavaRuntime.SurumuOku(j)?.ToString() ?? "?"));
        return j == null ? 1 : 0;
    }
    case "kimlik":
        Console.WriteLine("donanım: " + LinuxStorage.DonanimKimligi());
        Console.WriteLine("örnek uuid: " + LinuxStorage.CevrimdisiUuidUret("OrnekOyuncu").ToString("D"));
        return 0;
    case "yollar":
        Console.WriteLine("config: " + LinuxStorage.Kok);
        Console.WriteLine("data:   " + LinuxStorage.VeriKok);
        Console.WriteLine("sunucum:" + LinuxStorage.VarsayilanSunucumKlasoru());
        return 0;
    default:
        Console.Error.WriteLine("bilinmeyen komut: " + args[0]);
        return 2;
}
