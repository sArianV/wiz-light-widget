using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WizLightWidget.Models;
using WizLightWidget.Native;
using WizLightWidget.Services;
using WizLightWidget.ViewModels;
using WinForms = System.Windows.Forms;

namespace WizLightWidget;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BulbViewModel> _bulbs = new();
    private readonly WizDiscoveryService _discovery = new();
    private readonly WizControlService _control = new();
    private readonly BulbStore _store = new();
    private readonly TaskbarThumbnailManager _taskbarMgr = new();
    private readonly Dictionary<string, DispatcherTimer> _brightnessDebounce = new();

    private WinForms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _appIcon;
    private bool _cleanedUp;

    private IntPtr _iconPowerOn, _iconPowerOff, _iconBrightUp, _iconBrightDown;

    private const uint ThumbIdPower = 1;
    private const uint ThumbIdBrightDown = 2;
    private const uint ThumbIdBrightUp = 3;

    public MainWindow()
    {
        InitializeComponent();
        BulbList.ItemsSource = _bulbs;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"v{GetAppVersion()}";
        SetupTrayIcon();
        SetupThumbnailToolbar();
        await RunDiscoveryAsync();
    }

    // La X cierra la app de verdad. El botón de minimizar de Windows sigue
    // dejándola corriendo en segundo plano (con los controles del taskbar activos).
    private void MainWindow_Closing(object? sender, CancelEventArgs e) => CleanupResources();

    private void MainWindow_Closed(object? sender, EventArgs e) => Application.Current.Shutdown();

    private void CleanupResources()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _appIcon?.Dispose();

        foreach (var h in new[] { _iconPowerOn, _iconPowerOff, _iconBrightUp, _iconBrightDown })
            if (h != IntPtr.Zero) IconFactory.DestroyIcon(h);
    }

    // ----- Tray icon -----

    private void SetupTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo != null)
                _appIcon = new System.Drawing.Icon(streamInfo.Stream);
        }
        catch { /* fall back to no custom icon */ }

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Luces WiZ"
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Mostrar ventana", null, (s, e) => ShowWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Encender todas", null, async (s, e) => await SetAllPowerAsync(true));
        menu.Items.Add("Apagar todas", null, async (s, e) => await SetAllPowerAsync(false));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var startupItem = new WinForms.ToolStripMenuItem("Iniciar con Windows") { CheckOnClick = true };
        startupItem.Click += (s, e) => StartupService.SetEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        var versionItem = new WinForms.ToolStripMenuItem($"Versión {GetAppVersion()}") { Enabled = false };
        menu.Items.Add(versionItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Salir", null, (s, e) => ExitApplication());

        // Refresca el estado real cada vez que se abre, por si se cambió desde el popup de la ventana.
        menu.Opening += (s, e) => startupItem.Checked = StartupService.IsEnabled();

        _trayIcon.ContextMenuStrip = menu;

        _trayIcon.DoubleClick += (s, e) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication() => Close();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StartWithWindowsCheckBox.IsChecked = StartupService.IsEnabled();
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e) =>
        StartupService.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "desconocida" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ----- Taskbar thumbnail toolbar (hover controls, like Spotify) -----

    private void SetupThumbnailToolbar()
    {
        _taskbarMgr.Attach(this);

        _iconPowerOn = IconFactory.CreatePowerIcon(true);
        _iconPowerOff = IconFactory.CreatePowerIcon(false);
        _iconBrightDown = IconFactory.CreateBrightnessIcon(false);
        _iconBrightUp = IconFactory.CreateBrightnessIcon(true);

        _taskbarMgr.SetButtons(new List<ThumbButtonDef>
        {
            new(ThumbIdPower, _iconPowerOff, "Encender/apagar todas"),
            new(ThumbIdBrightDown, _iconBrightDown, "Bajar brillo"),
            new(ThumbIdBrightUp, _iconBrightUp, "Subir brillo"),
        });

        _taskbarMgr.ButtonClicked += async id => await OnThumbButtonClicked(id);
    }

    /// <summary>Favoritos si hay alguno marcado; si no, todos los bombillos como respaldo.</summary>
    private List<BulbViewModel> GetThumbTargets()
    {
        var favorites = _bulbs.Where(b => b.IsFavorite).ToList();
        return favorites.Count > 0 ? favorites : _bulbs.ToList();
    }

    private async Task OnThumbButtonClicked(uint id)
    {
        var targets = GetThumbTargets();
        switch (id)
        {
            case ThumbIdPower:
                bool newState = !targets.Any(b => b.IsOn);
                foreach (var b in targets) b.IsOn = newState;
                var tasks = targets.Select(b => SafeSetPower(b.Ip, newState));
                await Task.WhenAll(tasks);
                UpdateAggregatePowerIcon();
                break;
            case ThumbIdBrightDown:
                await AdjustBrightnessAsync(targets, -10);
                break;
            case ThumbIdBrightUp:
                await AdjustBrightnessAsync(targets, 10);
                break;
        }
    }

    private void UpdateAggregatePowerIcon()
    {
        var targets = GetThumbTargets();
        bool anyOn = targets.Any(b => b.IsOn);
        bool usingFavorites = _bulbs.Any(b => b.IsFavorite);
        string label = usingFavorites ? "favoritas" : "todas";

        _taskbarMgr.UpdateButtons(new List<ThumbButtonDef>
        {
            new(ThumbIdPower, anyOn ? _iconPowerOn : _iconPowerOff, anyOn ? $"Apagar {label}" : $"Encender {label}"),
            new(ThumbIdBrightDown, _iconBrightDown, $"Bajar brillo ({label})"),
            new(ThumbIdBrightUp, _iconBrightUp, $"Subir brillo ({label})"),
        });
    }

    // ----- Discovery -----

    private async void DiscoverButton_Click(object sender, RoutedEventArgs e) => await RunDiscoveryAsync();

    private async Task RunDiscoveryAsync()
    {
        StatusText.Text = "Buscando bombillos en tu red...";
        DiscoverButton.IsEnabled = false;
        try
        {
            var results = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            foreach (var state in results)
            {
                var existing = _bulbs.FirstOrDefault(b => b.Mac == state.Mac || b.Ip == state.Ip);
                var name = _store.GetName(state.Mac, DefaultName(state));
                if (existing != null)
                {
                    existing.Ip = state.Ip;
                    existing.IsOn = state.IsOn;
                    existing.Brightness = state.Brightness;
                    existing.IsOnline = true;
                }
                else
                {
                    var isFavorite = _store.GetFavorite(state.Mac);
                    _bulbs.Add(BulbViewModel.FromState(state, name, isFavorite));
                }
            }

            StatusText.Text = results.Count == 0
                ? "No se encontraron bombillos. Verifica que estén encendidos y en la misma red WiFi."
                : $"{results.Count} bombillo(s) encontrado(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error al buscar bombillos: " + ex.Message;
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
            UpdateAggregatePowerIcon();
        }
    }

    private static string DefaultName(WizBulbState s) =>
        "Bombillo " + (s.Mac.Length >= 4 ? s.Mac[^4..] : s.Ip);

    // ----- Per-bulb UI events -----

    private async void PowerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BulbViewModel bulb) return;
        try { await _control.SetPowerAsync(bulb.Ip, bulb.IsOn); }
        catch { /* offline bulb, ignore */ }
        UpdateAggregatePowerIcon();
    }

    private void NameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is BulbViewModel bulb)
            _store.SetName(bulb.Mac, bulb.Name);
    }

    private void EditName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.FindName("NameTextBox") is TextBox nameBox)
        {
            nameBox.Focus();
            nameBox.SelectAll();
        }
    }

    private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not BulbViewModel bulb) return;
        _store.SetFavorite(bulb.Mac, bulb.IsFavorite);
        UpdateAggregatePowerIcon();
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (((FrameworkElement)sender).DataContext is not BulbViewModel bulb) return;

        if (!_brightnessDebounce.TryGetValue(bulb.Ip, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            timer.Tick += async (s, args) =>
            {
                timer!.Stop();
                try { await _control.SetBrightnessAsync(bulb.Ip, (int)Math.Round(bulb.Brightness)); }
                catch { /* offline bulb, ignore */ }
            };
            _brightnessDebounce[bulb.Ip] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    // Manejo manual del arrastre: el punto sigue al cursor directamente, sin depender
    // del comportamiento por defecto (a veces inconsistente) del Track/Thumb de WPF.
    private void BrightnessSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.CaptureMouse();
        SetSliderValueFromMouse(slider, e.GetPosition(slider));
        e.Handled = true;
    }

    private void BrightnessSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (e.LeftButton == MouseButtonState.Pressed && slider.IsMouseCaptured)
            SetSliderValueFromMouse(slider, e.GetPosition(slider));
    }

    private void BrightnessSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider) slider.ReleaseMouseCapture();
    }

    private static void SetSliderValueFromMouse(Slider slider, Point pos)
    {
        if (slider.ActualWidth <= 0) return;
        double ratio = Math.Clamp(pos.X / slider.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private async void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not BulbViewModel bulb) return;
        if (btn.Tag is not string tag) return;

        bulb.IsOn = true;

        if (tag.StartsWith("TEMP:"))
        {
            // Usa el canal nativo de blanco del foco (no RGB mezclado), que sí alcanza
            // el brillo máximo real del hardware en vez del blanco "sintético" por RGB.
            var kelvin = int.Parse(tag[5..]);
            bulb.Color = kelvin <= 3500 ? Color.FromRgb(0xFF, 0xD9, 0xA6) : Color.FromRgb(0xEA, 0xF4, 0xFF);
            try { await _control.SetColorTempAsync(bulb.Ip, kelvin); }
            catch { /* offline bulb, ignore */ }
        }
        else
        {
            var color = (Color)ColorConverter.ConvertFromString(tag)!;
            bulb.Color = color;
            try { await _control.SetColorAsync(bulb.Ip, color.R, color.G, color.B); }
            catch { /* offline bulb, ignore */ }
        }

        UpdateAggregatePowerIcon();
    }

    private void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.FindName("ColorPickerPopup") is Popup popup)
            popup.IsOpen = !popup.IsOpen;
    }

    // ----- Selector de color 2D (tono horizontal, mezcla con blanco vertical, como la app oficial) -----

    private bool _colorPickerDragging;
    private readonly Dictionary<string, DispatcherTimer> _colorDebounce = new();
    private readonly Dictionary<string, (byte R, byte G, byte B)> _pendingColor = new();

    private void ColorPickerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        canvas.CaptureMouse();
        _colorPickerDragging = true;
        UpdateColorFromPicker(canvas, e.GetPosition(canvas));
    }

    private void ColorPickerCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Canvas canvas) return;
        if (_colorPickerDragging && e.LeftButton == MouseButtonState.Pressed)
            UpdateColorFromPicker(canvas, e.GetPosition(canvas));
    }

    private void ColorPickerCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Canvas canvas) canvas.ReleaseMouseCapture();
        _colorPickerDragging = false;
    }

    private void UpdateColorFromPicker(Canvas canvas, Point pos)
    {
        if (canvas.DataContext is not BulbViewModel bulb) return;
        if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;

        double x = Math.Clamp(pos.X, 0, canvas.ActualWidth);
        double y = Math.Clamp(pos.Y, 0, canvas.ActualHeight);

        double hue = x / canvas.ActualWidth * 360.0;
        double saturation = y / canvas.ActualHeight; // arriba (y=0) = blanco puro, abajo (y=alto) = color puro

        var (r, g, b) = HsvToRgb(hue, saturation, 1.0);

        bulb.Color = Color.FromRgb(r, g, b);
        bulb.IsOn = true;

        if (canvas.FindName("ColorPickerThumb") is Ellipse thumb)
        {
            Canvas.SetLeft(thumb, x - thumb.Width / 2);
            Canvas.SetTop(thumb, y - thumb.Height / 2);
        }

        DebounceColorSend(bulb, r, g, b);
    }

    private void DebounceColorSend(BulbViewModel bulb, byte r, byte g, byte b)
    {
        _pendingColor[bulb.Ip] = (r, g, b);

        if (!_colorDebounce.TryGetValue(bulb.Ip, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += async (s, args) =>
            {
                timer!.Stop();
                if (_pendingColor.TryGetValue(bulb.Ip, out var c))
                {
                    try { await _control.SetColorAsync(bulb.Ip, c.R, c.G, c.B); }
                    catch { /* offline bulb, ignore */ }
                }
            };
            _colorDebounce[bulb.Ip] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h %= 360;
        if (h < 0) h += 360;

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;

        var (r1, g1, b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return (
            (byte)Math.Round((r1 + m) * 255),
            (byte)Math.Round((g1 + m) * 255),
            (byte)Math.Round((b1 + m) * 255));
    }

    // ----- Bulk actions -----

    private async Task SetAllPowerAsync(bool on)
    {
        foreach (var b in _bulbs) b.IsOn = on;
        var tasks = _bulbs.Select(b => SafeSetPower(b.Ip, on));
        await Task.WhenAll(tasks);
        UpdateAggregatePowerIcon();
    }

    private async Task AdjustBrightnessAsync(List<BulbViewModel> candidates, int delta)
    {
        var targets = candidates.Where(b => b.IsOn).ToList();
        foreach (var b in targets)
            b.Brightness = Math.Clamp(b.Brightness + delta, 10, 100);

        var tasks = targets.Select(b => SafeSetBrightness(b.Ip, (int)b.Brightness));
        await Task.WhenAll(tasks);
    }

    private async Task SafeSetPower(string ip, bool on)
    {
        try { await _control.SetPowerAsync(ip, on); }
        catch { /* offline bulb, ignore */ }
    }

    private async Task SafeSetBrightness(string ip, int val)
    {
        try { await _control.SetBrightnessAsync(ip, val); }
        catch { /* offline bulb, ignore */ }
    }
}
