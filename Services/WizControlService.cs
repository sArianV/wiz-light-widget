using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WizLightWidget.Models;

namespace WizLightWidget.Services;

public class WizControlService
{
    private const int Port = WizDiscoveryService.WizPort;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task SendAsync(string ip, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var udp = new UdpClient();
        await udp.SendAsync(bytes, new IPEndPoint(IPAddress.Parse(ip), Port));
    }

    public Task SetPowerAsync(string ip, bool on) =>
        SendAsync(ip, new { method = "setPilot", @params = new { state = on } });

    public Task SetBrightnessAsync(string ip, int percent) =>
        SendAsync(ip, new { method = "setPilot", @params = new { state = true, dimming = Math.Clamp(percent, 10, 100) } });

    public Task SetColorAsync(string ip, byte r, byte g, byte b) =>
        SendAsync(ip, new { method = "setPilot", @params = new { state = true, r, g, b } });

    public Task SetColorAndBrightnessAsync(string ip, byte r, byte g, byte b, int percent) =>
        SendAsync(ip, new { method = "setPilot", @params = new { state = true, r, g, b, dimming = Math.Clamp(percent, 10, 100) } });

    public Task SetColorTempAsync(string ip, int kelvin) =>
        SendAsync(ip, new { method = "setPilot", @params = new { state = true, temp = Math.Clamp(kelvin, 2200, 6500) } });

    public async Task<WizBulbState?> GetPilotAsync(string ip, int timeoutMs = 1500)
    {
        using var udp = new UdpClient();
        var payload = Encoding.UTF8.GetBytes("{\"method\":\"getPilot\",\"params\":{}}");
        await udp.SendAsync(payload, new IPEndPoint(IPAddress.Parse(ip), Port));

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var result = await udp.ReceiveAsync(cts.Token);
            return WizJson.ParsePilot(ip, Encoding.UTF8.GetString(result.Buffer));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
