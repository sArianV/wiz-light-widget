using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WizLightWidget.Models;

namespace WizLightWidget.ViewModels;

public class BulbViewModel : INotifyPropertyChanged
{
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";

    private string _name = "Bombillo";
    public string Name { get => _name; set => Set(ref _name, value); }

    private bool _isOn;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }

    private double _brightness = 100;
    public double Brightness { get => _brightness; set => Set(ref _brightness, value); }

    private Color _color = Colors.White;
    public Color Color { get => _color; set => Set(ref _color, value); }

    private bool _isOnline = true;
    public bool IsOnline { get => _isOnline; set => Set(ref _isOnline, value); }

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }

    public static BulbViewModel FromState(WizBulbState s, string name, bool isFavorite) => new()
    {
        Ip = s.Ip,
        Mac = s.Mac,
        Name = name,
        IsOn = s.IsOn,
        Brightness = s.Brightness,
        Color = s.IsColorMode ? Color.FromRgb(s.R, s.G, s.B) : Colors.White,
        IsFavorite = isFavorite
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
