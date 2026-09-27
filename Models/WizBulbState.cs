namespace WizLightWidget.Models;

public class WizBulbState
{
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public bool IsOn { get; set; }
    public int Brightness { get; set; } = 100;
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public bool IsColorMode { get; set; }
    public int? ColorTempKelvin { get; set; }
}
