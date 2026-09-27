using System.Text.Json;
using WizLightWidget.Models;

namespace WizLightWidget.Services;

internal static class WizJson
{
    public static WizBulbState? ParsePilot(string ip, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return null;

            var bulb = new WizBulbState { Ip = ip };

            bulb.Mac = result.TryGetProperty("mac", out var mac) ? (mac.GetString() ?? ip) : ip;

            if (result.TryGetProperty("state", out var state))
                bulb.IsOn = state.GetBoolean();

            bulb.Brightness = result.TryGetProperty("dimming", out var dimming) ? dimming.GetInt32() : 100;

            if (result.TryGetProperty("r", out var r) &&
                result.TryGetProperty("g", out var g) &&
                result.TryGetProperty("b", out var b))
            {
                bulb.R = (byte)r.GetInt32();
                bulb.G = (byte)g.GetInt32();
                bulb.B = (byte)b.GetInt32();
                bulb.IsColorMode = true;
            }

            if (result.TryGetProperty("temp", out var temp))
                bulb.ColorTempKelvin = temp.GetInt32();

            return bulb;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
