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

    /// <summary>
    /// Igual que <see cref="SetColorAsync"/> pero además inyecta blanco frío (c) o cálido
    /// (w) según el matiz, para acercar el brillo percibido al de la app oficial de WiZ
    /// (los focos son RGBCW: mandar solo r/g/b deja apagados los LEDs blancos dedicados).
    /// Solo la usa el selector de color personalizado (rueda), no los colores fijos ni el
    /// modo rítmico.
    /// </summary>
    public Task SetColorBoostedAsync(string ip, byte r, byte g, byte b)
    {
        var (cw, ww) = ComputeWhiteBoost(r, g, b);
        return SendAsync(ip, new { method = "setPilot", @params = new { state = true, r, g, b, c = cw, w = ww } });
    }

    private static (byte Cw, byte Ww) ComputeWhiteBoost(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b));
        if (max == 0) return (0, 0);

        int min = Math.Min(r, Math.Min(g, b));
        double saturation = (max - min) / (double)max; // 0 = gris/blanco, 1 = color puro
        double boost = max * saturation * 0.45;
        byte amount = (byte)Math.Clamp(Math.Round(boost), 0, 255);

        // Rojos/naranjas/amarillos -> blanco cálido; verdes/azules/violetas -> blanco frío.
        bool warm = r > b;
        return warm ? ((byte)0, amount) : (amount, (byte)0);
    }

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
