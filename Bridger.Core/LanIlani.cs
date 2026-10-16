using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bridger.Core;

// Orijinal LanIlaniBaslat/LanIlaniDurdur birebir portu:
// 224.0.2.60:4445 çoklu yayın adresine 1,5 saniyede bir
// "[MOTD]<oda adı>[/MOTD][AD]<port>[/AD]" paketi gönderilir; aynı ağdaki
// Minecraft istemcileri dünyayı "LAN Dünyaları" listesinde görür.
public sealed class LanIlani : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Baslat(string odaAdi, int port)
    {
        Durdur();
        string motd = (odaAdi ?? "Bridger Odasi").Replace("[", "").Replace("]", "");
        if (motd.Length > 60) motd = motd[..60];
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var udp = new UdpClient();
                udp.JoinMulticastGroup(IPAddress.Parse("224.0.2.60"));
                byte[] veri = Encoding.UTF8.GetBytes("[MOTD]" + motd + "[/MOTD][AD]" + port + "[/AD]");
                var hedef = new IPEndPoint(IPAddress.Parse("224.0.2.60"), 4445);
                while (!token.IsCancellationRequested)
                {
                    try { await udp.SendAsync(veri, veri.Length, hedef); }
                    catch { break; }
                    try { await Task.Delay(1500, token); }
                    catch (OperationCanceledException) { break; }
                }
                try { udp.DropMulticastGroup(IPAddress.Parse("224.0.2.60")); } catch { }
            }
            catch { }
        }, token);
    }

    public void Durdur()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public void Dispose() => Durdur();
}
