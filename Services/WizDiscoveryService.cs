using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using WizLightWidget.Models;

namespace WizLightWidget.Services;

public class WizDiscoveryService
{
    public const int WizPort = 38899;

    public async Task<List<WizBulbState>> DiscoverAsync(TimeSpan timeout)
    {
        var found = new ConcurrentDictionary<string, WizBulbState>();
        using var udp = new UdpClient(0) { EnableBroadcast = true };

        var payload = Encoding.UTF8.GetBytes("{\"method\":\"getPilot\",\"params\":{}}");

        var targets = GetBroadcastTargets();
        targets.Add(IPAddress.Broadcast);

        foreach (var addr in targets.Distinct())
        {
            try { await udp.SendAsync(payload, new IPEndPoint(addr, WizPort)); }
            catch { /* ignore unreachable adapters */ }
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                var bulb = WizJson.ParsePilot(result.RemoteEndPoint.Address.ToString(),
                    Encoding.UTF8.GetString(result.Buffer));
                if (bulb != null)
                    found[bulb.Ip] = bulb;
            }
        }
        catch (OperationCanceledException) { }

        return found.Values.OrderBy(b => b.Ip).ToList();
    }

    private static List<IPAddress> GetBroadcastTargets()
    {
        var list = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var props = nic.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask == null) continue;

                var ipBytes = ua.Address.GetAddressBytes();
                var maskBytes = ua.IPv4Mask.GetAddressBytes();
                var broadcastBytes = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcastBytes[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);

                list.Add(new IPAddress(broadcastBytes));
            }
        }
        return list;
    }
}
