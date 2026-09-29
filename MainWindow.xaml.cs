using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WizLightWidget.Controls;
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
    private readonly SceneStore _sceneStore = new();
    private readonly ObservableCollection<Scene> _scenes = new();
    private readonly TaskbarThumbnailManager _taskbarMgr = new();
    private readonly AudioReactiveService _audioReactive = new();
    private readonly Dictionary<string, DispatcherTimer> _brightnessDebounce = new();

    // Re-escanea la red para detectar focos que se conectaron o desconectaron.
    private readonly DispatcherTimer _periodicScanTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private WinForms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _appIcon;
    private bool _cleanedUp;

    private IntPtr _iconPowerOn, _iconPowerOff, _iconBrightUp, _iconBrightDown, _iconWarmWhite, _iconCoolWhite, _iconMoveMonitor;

    private const uint ThumbIdPower = 1;
    private const uint ThumbIdBrightDown = 2;
    private const uint ThumbIdBrightUp = 3;
    private const uint ThumbIdWarmWhite = 4;
    private const uint ThumbIdCoolWhite = 5;
    private const uint ThumbIdMoveMonitor = 6;

    public MainWindow()
    {
        InitializeComponent();
        BulbList.ItemsSource = _bulbs;
        ScenesList.ItemsSource = _scenes;

        foreach (var s in _sceneStore.Scenes) _scenes.Add(s);
        UpdateScenesEmptyState();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        _rhythmDecayTimer.Tick += RhythmDecayTick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        VersionText.Text = $"v{GetAppVersion()}";
        WindowCornerHelper.ApplyRoundedCorners(new WindowInteropHelper(this).Handle);
        SetupTrayIcon();
        SetupThumbnailToolbar();
        await RunDiscoveryAsync();

        _periodicScanTimer.Tick += async (_, _) => await RunDiscoveryAsync(silent: true);
        _periodicScanTimer.Start();
    }

    // La X cierra la app de verdad. El botón de minimizar de Windows sigue
    // dejándola corriendo en segundo plano (con los controles del taskbar activos).
    private void MainWindow_Closing(object? sender, CancelEventArgs e) => CleanupResources();

    private void MainWindow_Closed(object? sender, EventArgs e) => Application.Current.Shutdown();

    private void CleanupResources()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _periodicScanTimer.Stop();
        _rhythmDecayTimer.Stop();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _appIcon?.Dispose();
        _audioReactive.Dispose();

        foreach (var h in new[] { _iconPowerOn, _iconPowerOff, _iconBrightUp, _iconBrightDown, _iconWarmWhite, _iconCoolWhite, _iconMoveMonitor })
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

    // ----- Barra de título estilo macOS (semáforos + arrastre) -----

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (WindowState != WindowState.Maximized)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            var workArea = SystemParameters.WorkArea;
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
        }
        else
        {
            ClearValue(MaxWidthProperty);
            ClearValue(MaxHeightProperty);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StartWithWindowsCheckBox.IsChecked = StartupService.IsEnabled();
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e) =>
        StartupService.SetEnabled(StartWithWindowsCheckBox.IsChecked == true);

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdatesButton.IsEnabled = false;
        CheckForUpdatesButton.Content = "Buscando...";

        try
        {
            var updateService = new UpdateService();
            var update = await updateService.CheckForUpdateAsync();

            if (update == null)
            {
                CheckForUpdatesButton.Content = "Ya tienes la última versión";
            }
            else
            {
                var progress = new Progress<double>(p =>
                    CheckForUpdatesButton.Content = $"Descargando {(int)(p * 100)}%...");
                var tempExe = await updateService.DownloadUpdateAsync(update.DownloadUrl, progress);

                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    CheckForUpdatesButton.Content = "Instalando...";
                    UpdateService.LaunchUpdateAndExit(tempExe, currentExe);
                    return; // El proceso se cierra desde LaunchUpdateAndExit.
                }
            }
        }
        catch
        {
            CheckForUpdatesButton.Content = "Error al buscar actualizaciones";
        }

        await Task.Delay(2500);
        CheckForUpdatesButton.Content = "Buscar actualizaciones";
        CheckForUpdatesButton.IsEnabled = true;
    }

    // ----- Modo rítmico (pulsa las luces al ritmo del audio que suena en la PC) -----

    private enum RhythmColorMode { Rainbow, RainbowRandom, FixedList, PaletteSunset, PaletteOcean, PaletteNeon, PaletteFire }

    private static readonly Color WarmWhite = (Color)ColorConverter.ConvertFromString("#FFD9A6")!;
    private static readonly Color CoolWhite = (Color)ColorConverter.ConvertFromString("#EAF4FF")!;

    // Paletas para los modos que "fluyen" como el arcoíris pero limitados a una gama de colores.
    // Los 4 colores de cada paleta están pensados en pares opuestos (cálido/frío) para que el
    // cambio se note bien, en vez de tonos vecinos que casi no se distinguen entre sí.
    private static readonly Color[] PaletteSunset = // Trópico intenso: magenta/amarillo/rojo vs. verde/azul/cian, todos saturados a full
    {
        (Color)ColorConverter.ConvertFromString("#FF0066")!,
        (Color)ColorConverter.ConvertFromString("#00C853")!,
        (Color)ColorConverter.ConvertFromString("#FFD700")!,
        (Color)ColorConverter.ConvertFromString("#2962FF")!,
        (Color)ColorConverter.ConvertFromString("#FF3D00")!,
        (Color)ColorConverter.ConvertFromString("#00E5FF")!,
    };
    private static readonly Color[] PaletteOcean = // Aurora intensa: cian/violeta/azul vs. rojo-naranja/amarillo/magenta
    {
        (Color)ColorConverter.ConvertFromString("#00FFFF")!,
        (Color)ColorConverter.ConvertFromString("#FF3D00")!,
        (Color)ColorConverter.ConvertFromString("#651FFF")!,
        (Color)ColorConverter.ConvertFromString("#FFEA00")!,
        (Color)ColorConverter.ConvertFromString("#2962FF")!,
        (Color)ColorConverter.ConvertFromString("#FF0066")!,
    };
    private static readonly Color[] PaletteNeon = // Neón ácido: magenta/violeta/naranja vs. verde/amarillo/cian
    {
        (Color)ColorConverter.ConvertFromString("#FF00C8")!,
        (Color)ColorConverter.ConvertFromString("#39FF14")!,
        (Color)ColorConverter.ConvertFromString("#7B2FFF")!,
        (Color)ColorConverter.ConvertFromString("#FFEA00")!,
        (Color)ColorConverter.ConvertFromString("#00E5FF")!,
        (Color)ColorConverter.ConvertFromString("#FF3D00")!,
    };
    private static readonly Color[] PaletteFire = // Fuego y hielo: rojo/naranja/amarillo puros vs. cian/azul/violeta puros
    {
        (Color)ColorConverter.ConvertFromString("#FF0000")!,
        (Color)ColorConverter.ConvertFromString("#00E5FF")!,
        (Color)ColorConverter.ConvertFromString("#FF6D00")!,
        (Color)ColorConverter.ConvertFromString("#2962FF")!,
        (Color)ColorConverter.ConvertFromString("#FFD700")!,
        (Color)ColorConverter.ConvertFromString("#651FFF")!,
    };

    // Colores activos para el modo "Colores fijos"; se actualiza al tocar los círculos del popup.
    private List<Color> _fixedRhythmColors = new()
    {
        WarmWhite, CoolWhite,
        (Color)ColorConverter.ConvertFromString("#FF6B6B")!,
        (Color)ColorConverter.ConvertFromString("#FFA500")!,
        (Color)ColorConverter.ConvertFromString("#FFD93D")!,
        (Color)ColorConverter.ConvertFromString("#6BCB77")!,
        (Color)ColorConverter.ConvertFromString("#4D96FF")!,
        (Color)ColorConverter.ConvertFromString("#9B5DE5")!,
    };

    private RhythmColorMode _rhythmMode = RhythmColorMode.Rainbow;
    private double _rhythmHue;
    private double _rhythmPalettePos;
    private int _fixedColorIndex;
    private bool _rhythmRunning;

    // Entre golpe y golpe, el brillo va decayendo hacia el piso en vez de
    // quedarse pegado en el pico del último golpe (efecto "release").
    private readonly DispatcherTimer _rhythmDecayTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private double _rhythmCurrentBrightness;
    private byte _rhythmLastR, _rhythmLastG, _rhythmLastB;
    private DateTime _rhythmLastBeatTime;
    private const double RhythmMinBrightness = 25;
    private const double RhythmDecayFactor = 0.80; // por tick de _rhythmDecayTimer
    private static readonly TimeSpan RhythmDecayHold = TimeSpan.FromMilliseconds(500);
    private readonly Random _rhythmRandom = new();

    private void RhythmButton_Click(object sender, RoutedEventArgs e)
    {
        // Solo mostrar un modo tildado si el rítmico está realmente activo;
        // si está detenido, ninguno debe aparecer seleccionado.
        RainbowModeRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.Rainbow;
        RainbowRandomModeRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.RainbowRandom;
        FixedModeRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.FixedList;
        PaletteSunsetRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.PaletteSunset;
        PaletteOceanRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.PaletteOcean;
        PaletteNeonRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.PaletteNeon;
        PaletteFireRadio.IsChecked = _rhythmRunning && _rhythmMode == RhythmColorMode.PaletteFire;
        RhythmStopButton.IsEnabled = _rhythmRunning;
        RhythmPopup.IsOpen = !RhythmPopup.IsOpen;
    }

    private void RhythmModeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        _rhythmMode = tag switch
        {
            "FixedList" => RhythmColorMode.FixedList,
            "RainbowRandom" => RhythmColorMode.RainbowRandom,
            "PaletteSunset" => RhythmColorMode.PaletteSunset,
            "PaletteOcean" => RhythmColorMode.PaletteOcean,
            "PaletteNeon" => RhythmColorMode.PaletteNeon,
            "PaletteFire" => RhythmColorMode.PaletteFire,
            _ => RhythmColorMode.Rainbow
        };
        _rhythmHue = 0;
        _rhythmPalettePos = 0;
        _fixedColorIndex = 0;

        StartRhythmMode();
        RhythmPopup.IsOpen = false;
    }

    private void FixedColorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        var toggles = FixedColorsPanel.Children.OfType<ToggleButton>().ToList();
        if (toggles.All(t => t.IsChecked != true))
            clicked.IsChecked = true; // no dejar la rotación vacía

        _fixedRhythmColors = toggles
            .Where(t => t.IsChecked == true && t.Tag is string)
            .Select(t => (Color)ColorConverter.ConvertFromString((string)t.Tag)!)
            .ToList();
        _fixedColorIndex = 0;
    }

    private void RhythmStopButton_Click(object sender, RoutedEventArgs e)
    {
        StopRhythmMode();
        RhythmPopup.IsOpen = false;
    }

    private void StartRhythmMode()
    {
        if (_rhythmRunning) return;
        try
        {
            _audioReactive.BeatDetected += OnBeatDetected;
            _audioReactive.Start();
            _rhythmRunning = true;
            _rhythmCurrentBrightness = RhythmMinBrightness;
            _rhythmDecayTimer.Start();
            RhythmButton.Background = (Brush)FindResource("AccentBrush");
            StatusText.Text = "Modo rítmico";
        }
        catch
        {
            _audioReactive.BeatDetected -= OnBeatDetected;
            _rhythmRunning = false;
            StatusText.Text = "No se pudo iniciar la captura de audio.";
        }
    }

    private void StopRhythmMode()
    {
        if (!_rhythmRunning) return;
        _audioReactive.BeatDetected -= OnBeatDetected;
        _audioReactive.Stop();
        _rhythmDecayTimer.Stop();
        _rhythmRunning = false;
        RhythmButton.Background = Brushes.Transparent;
        StatusText.Text = "Modo rítmico detenido.";
    }

    private void OnBeatDetected(double strength)
    {
        // Llega desde el hilo de captura de audio del servicio.
        Dispatcher.BeginInvoke(() =>
        {
            var targets = GetThumbTargets().Where(b => b.IsOn).ToList();
            if (targets.Count == 0) return;

            byte r, g, b;
            switch (_rhythmMode)
            {
                case RhythmColorMode.FixedList when _fixedRhythmColors.Count > 0:
                    _fixedColorIndex = (_fixedColorIndex + 1) % _fixedRhythmColors.Count;
                    var c = _fixedRhythmColors[_fixedColorIndex];
                    (r, g, b) = (c.R, c.G, c.B);
                    break;
                case RhythmColorMode.RainbowRandom:
                    // A diferencia del arcoíris normal, cada golpe salta a un tono al azar.
                    _rhythmHue = _rhythmRandom.NextDouble() * 360;
                    (r, g, b) = HsvToRgb(_rhythmHue, 1.0, 1.0);
                    break;
                case RhythmColorMode.PaletteSunset:
                case RhythmColorMode.PaletteOcean:
                case RhythmColorMode.PaletteNeon:
                case RhythmColorMode.PaletteFire:
                    // Igual que el arcoíris (avanza y mezcla en cada golpe) pero recorriendo
                    // solo los colores de la paleta elegida en vez de todo el espectro.
                    var palette = GetPalette(_rhythmMode);
                    _rhythmPalettePos = (_rhythmPalettePos + 0.6 + strength * 0.8) % palette.Length;
                    (r, g, b) = LerpPalette(palette, _rhythmPalettePos);
                    break;
                default:
                    // Cada golpe avanza el tono; su "fuerza" acelera el salto de color.
                    _rhythmHue = (_rhythmHue + 35 + strength * 40) % 360;
                    (r, g, b) = HsvToRgb(_rhythmHue, 1.0, 1.0);
                    break;
            }

            int brightness = 35 + (int)Math.Round(Math.Clamp(strength, 0, 1) * 65);

            // El decay timer toma desde acá y va bajando el brillo hasta el próximo golpe.
            _rhythmCurrentBrightness = brightness;
            _rhythmLastBeatTime = DateTime.UtcNow;
            (_rhythmLastR, _rhythmLastG, _rhythmLastB) = (r, g, b);

            foreach (var bulb in targets)
            {
                bulb.Color = Color.FromRgb(r, g, b);
                bulb.Brightness = brightness;
                _ = SafeSetColorAndBrightness(bulb.Ip, r, g, b, brightness);
            }
        });
    }

    private void RhythmDecayTick(object? sender, EventArgs e)
    {
        if (!_rhythmRunning) return;
        if (DateTime.UtcNow - _rhythmLastBeatTime < RhythmDecayHold) return; // mantiene el pico un ratito antes de bajar

        double next = RhythmMinBrightness + (_rhythmCurrentBrightness - RhythmMinBrightness) * RhythmDecayFactor;
        if (Math.Abs(next - _rhythmCurrentBrightness) < 0.5) return; // ya llegó al piso, no hay nada que enviar
        _rhythmCurrentBrightness = next;

        var targets = GetThumbTargets().Where(b => b.IsOn).ToList();
        if (targets.Count == 0) return;

        int brightness = (int)Math.Round(_rhythmCurrentBrightness);
        foreach (var bulb in targets)
        {
            bulb.Brightness = brightness;
            _ = SafeSetColorAndBrightness(bulb.Ip, _rhythmLastR, _rhythmLastG, _rhythmLastB, brightness);
        }
    }

    private async Task SafeSetColorAndBrightness(string ip, byte r, byte g, byte b, int brightness)
    {
        try { await _control.SetColorAndBrightnessAsync(ip, r, g, b, brightness); }
        catch { /* offline bulb, ignore */ }
    }

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
        _iconWarmWhite = IconFactory.CreateWhiteTempIcon(warm: true);
        _iconCoolWhite = IconFactory.CreateWhiteTempIcon(warm: false);
        _iconMoveMonitor = IconFactory.CreateMoveMonitorIcon();

        _taskbarMgr.SetButtons(new List<ThumbButtonDef>
        {
            new(ThumbIdPower, _iconPowerOff, "Encender/apagar todas"),
            new(ThumbIdBrightDown, _iconBrightDown, "Bajar brillo"),
            new(ThumbIdBrightUp, _iconBrightUp, "Subir brillo"),
            new(ThumbIdWarmWhite, _iconWarmWhite, "Blanco cálido"),
            new(ThumbIdCoolWhite, _iconCoolWhite, "Blanco frío"),
            new(ThumbIdMoveMonitor, _iconMoveMonitor, "Mover a otro monitor"),
        });

        _taskbarMgr.ButtonClicked += async id => await OnThumbButtonClicked(id);
    }

    /// <summary>Favoritos si hay alguno marcado; si no, todos los bombillos como respaldo.</summary>
    private List<BulbViewModel> GetThumbTargets()
    {
        var online = _bulbs.Where(b => b.IsOnline).ToList();
        var favorites = online.Where(b => b.IsFavorite).ToList();
        return favorites.Count > 0 ? favorites : online;
    }

    // Nombre del monitor donde quedó el widget la última vez que se lo movió con el botón de la
    // barra de tareas; sirve de respaldo cuando la ventana está oculta y no se puede medir.
    private string? _lastMonitorDevice;

    private void MoveWidgetToNextMonitor()
    {
        var screens = WindowMonitorHelper.GetOrderedScreens();
        var hwnd = new WindowInteropHelper(this).Handle;

        // Se restaura primero para medir y mover una ventana normal y visible.
        var currentDevice = IsVisible && WindowState != WindowState.Minimized
            ? WinForms.Screen.FromHandle(hwnd).DeviceName
            : _lastMonitorDevice;
        if (!IsVisible) Show();
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        currentDevice ??= WinForms.Screen.FromHandle(hwnd).DeviceName;

        int currentIndex = screens.FindIndex(s => s.DeviceName == currentDevice);
        var target = screens[(currentIndex + 1) % screens.Count];

        WindowMonitorHelper.MoveToScreenAndActivate(hwnd, target);
        _lastMonitorDevice = target.DeviceName;
        StatusText.Text = screens.Count > 1
            ? $"Widget movido al monitor {screens.IndexOf(target) + 1} de {screens.Count}."
            : "Solo hay un monitor conectado.";
    }

    private async Task OnThumbButtonClicked(uint id)
    {
        // Mover el widget de monitor no es un control de luces: no debe cortar el modo rítmico.
        if (id == ThumbIdMoveMonitor)
        {
            MoveWidgetToNextMonitor();
            return;
        }

        // Los controles de la barra de tareas tienen prioridad: cualquier click ahí
        // cancela el modo rítmico para que no compita enviando colores/brillo distintos.
        StopRhythmMode();

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
            case ThumbIdWarmWhite:
                await SetWhiteTempAsync(targets, kelvin: 2700);
                break;
            case ThumbIdCoolWhite:
                await SetWhiteTempAsync(targets, kelvin: 6500);
                break;
        }
    }

    private async Task SetWhiteTempAsync(List<BulbViewModel> targets, int kelvin)
    {
        var color = kelvin <= 3500 ? Color.FromRgb(0xFF, 0xD9, 0xA6) : Color.FromRgb(0xEA, 0xF4, 0xFF);
        foreach (var b in targets)
        {
            b.IsOn = true;
            b.Color = color;
        }

        var tasks = targets.Select(b => SafeSetColorTemp(b.Ip, kelvin));
        await Task.WhenAll(tasks);
        UpdateAggregatePowerIcon();
    }

    private async Task SafeSetColorTemp(string ip, int kelvin)
    {
        try { await _control.SetColorTempAsync(ip, kelvin); }
        catch { /* offline bulb, ignore */ }
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
            new(ThumbIdWarmWhite, _iconWarmWhite, $"Blanco cálido ({label})"),
            new(ThumbIdCoolWhite, _iconCoolWhite, $"Blanco frío ({label})"),
            new(ThumbIdMoveMonitor, _iconMoveMonitor, "Mover a otro monitor"),
        });
    }

    // ----- Discovery -----

    private async void DiscoverButton_Click(object sender, RoutedEventArgs e) => await RunDiscoveryAsync();

    /// <summary>
    /// Busca bombillos en la red. Actualiza los ya conocidos, agrega los nuevos y marca
    /// como desconectado (IsOnline=false, se grisa en la tarjeta) a cualquiera que no
    /// haya respondido esta vez. <paramref name="silent"/> evita tocar el texto de estado
    /// y el botón "Buscar", para los re-escaneos automáticos en segundo plano.
    /// </summary>
    private async Task RunDiscoveryAsync(bool silent = false)
    {
        if (!silent)
        {
            StatusText.Text = "Buscando bombillos en tu red...";
            DiscoverButton.IsEnabled = false;
        }
        try
        {
            var results = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3));
            var foundMacs = new HashSet<string>(results.Select(r => r.Mac));

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

            foreach (var bulb in _bulbs)
                if (!foundMacs.Contains(bulb.Mac))
                    bulb.IsOnline = false;

            if (!silent)
            {
                StatusText.Text = results.Count == 0
                    ? "No se encontraron bombillos. Verifica que estén encendidos y en la misma red WiFi."
                    : $"{results.Count} bombillo(s) encontrado(s).";
            }
        }
        catch (Exception ex)
        {
            if (!silent) StatusText.Text = "Error al buscar bombillos: " + ex.Message;
        }
        finally
        {
            ApplyStoredBulbOrder();
            if (!silent) DiscoverButton.IsEnabled = true;
            UpdateAggregatePowerIcon();
        }
    }

    private static string DefaultName(WizBulbState s) =>
        "Bombillo " + (s.Mac.Length >= 4 ? s.Mac[^4..] : s.Ip);

    // ----- Escenas (foto del estado de todos los focos, aplicable con un click) -----

    // Nombre original de la escena que se está renombrando, o null si el panel de
    // nombre se está usando para crear una escena nueva (mismo panel para ambos casos).
    private string? _renamingSceneName;

    private void ScenesButton_Click(object sender, RoutedEventArgs e)
    {
        _renamingSceneName = null;
        NewSceneNamePanel.Visibility = Visibility.Collapsed;
        NewSceneButton.Visibility = Visibility.Visible;
        ScenesPopup.IsOpen = !ScenesPopup.IsOpen;
    }

    private void NewSceneButton_Click(object sender, RoutedEventArgs e)
    {
        _renamingSceneName = null;
        NewSceneNameBox.Text = $"Escena {_scenes.Count + 1}";
        NewSceneNamePanel.Visibility = Visibility.Visible;
        NewSceneButton.Visibility = Visibility.Collapsed;
        NewSceneNameBox.Focus();
        NewSceneNameBox.SelectAll();
    }

    private void RenameSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;

        _renamingSceneName = name;
        NewSceneNameBox.Text = name;
        NewSceneNamePanel.Visibility = Visibility.Visible;
        NewSceneButton.Visibility = Visibility.Collapsed;
        NewSceneNameBox.Focus();
        NewSceneNameBox.SelectAll();
    }

    private void SaveSceneCancel_Click(object sender, RoutedEventArgs e)
    {
        _renamingSceneName = null;
        NewSceneNamePanel.Visibility = Visibility.Collapsed;
        NewSceneButton.Visibility = Visibility.Visible;
    }

    private void NewSceneNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveSceneConfirm_Click(sender, e);
        else if (e.Key == Key.Escape) SaveSceneCancel_Click(sender, e);
    }

    private void SaveSceneConfirm_Click(object sender, RoutedEventArgs e)
    {
        var name = NewSceneNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_renamingSceneName != null) RenameScene(_renamingSceneName, name);
        else SaveCurrentStateAsScene(name);

        _renamingSceneName = null;
        NewSceneNamePanel.Visibility = Visibility.Collapsed;
        NewSceneButton.Visibility = Visibility.Visible;
        ScenesPopup.IsOpen = false;
        SceneSelectorLabel.Text = name;
    }

    private void SaveCurrentStateAsScene(string name)
    {
        var scene = new Scene
        {
            Name = name,
            Bulbs = _bulbs.Select(b => new SceneBulbState
            {
                Mac = b.Mac,
                IsOn = b.IsOn,
                Brightness = (int)Math.Round(b.Brightness),
                R = b.Color.R,
                G = b.Color.G,
                B = b.Color.B
            }).ToList()
        };

        _sceneStore.AddOrUpdate(scene);

        var existing = _scenes.FirstOrDefault(s => s.Name == name);
        if (existing != null) _scenes[_scenes.IndexOf(existing)] = scene;
        else _scenes.Add(scene);
        UpdateScenesEmptyState();
        StatusText.Text = $"Escena \"{name}\" guardada.";
    }

    /// <summary>Conserva los focos guardados, solo cambia el nombre. Si el nuevo nombre
    /// coincide con otra escena existente, esa otra queda reemplazada (mismo criterio
    /// que guardar una escena nueva con un nombre repetido).</summary>
    private void RenameScene(string oldName, string newName)
    {
        var original = _scenes.FirstOrDefault(s => s.Name == oldName);
        if (original == null) return;

        var renamed = new Scene { Name = newName, Bulbs = original.Bulbs };

        if (oldName != newName)
        {
            _sceneStore.Remove(oldName);
            var collision = _scenes.FirstOrDefault(s => s.Name == newName && !ReferenceEquals(s, original));
            if (collision != null) _scenes.Remove(collision);
        }

        _sceneStore.AddOrUpdate(renamed);
        _scenes[_scenes.IndexOf(original)] = renamed;
        StatusText.Text = $"Escena renombrada a \"{newName}\".";
    }

    private async void ApplySceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;
        var scene = _scenes.FirstOrDefault(s => s.Name == name);
        if (scene == null) return;

        ScenesPopup.IsOpen = false;
        StopRhythmMode(); // aplicar una escena tiene la misma prioridad que los controles manuales

        var tasks = new List<Task>();
        foreach (var entry in scene.Bulbs)
        {
            var bulb = _bulbs.FirstOrDefault(b => b.Mac == entry.Mac);
            if (bulb == null || !bulb.IsOnline) continue;

            bulb.IsOn = entry.IsOn;
            if (entry.IsOn)
            {
                bulb.Brightness = entry.Brightness;
                bulb.Color = Color.FromRgb(entry.R, entry.G, entry.B);
                tasks.Add(SafeSetColorAndBrightness(bulb.Ip, entry.R, entry.G, entry.B, entry.Brightness));
            }
            else
            {
                tasks.Add(SafeSetPower(bulb.Ip, false));
            }
        }

        await Task.WhenAll(tasks);
        UpdateAggregatePowerIcon();
        SceneSelectorLabel.Text = name;
        StatusText.Text = $"Escena \"{name}\" aplicada.";
    }

    private void DeleteSceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string name) return;
        _sceneStore.Remove(name);
        var scene = _scenes.FirstOrDefault(s => s.Name == name);
        if (scene != null) _scenes.Remove(scene);
        UpdateScenesEmptyState();
    }

    private void UpdateScenesEmptyState() =>
        NoScenesText.Visibility = _scenes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    // ----- Reordenar la lista arrastrando desde la manija de cada tarjeta -----
    // La manija funciona incluso con el foco desconectado (vive fuera del StackPanel
    // que se deshabilita con IsOnline). El orden se persiste por MAC en bulbs.json.

    private Point _dragStartPoint;
    private DragAdorner? _dragAdorner;

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStartPoint = e.GetPosition(null);

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not BulbViewModel bulb) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (fe.FindName("BulbCard") is not Border card) return;

        var grabOffset = Mouse.GetPosition(card);
        var layer = AdornerLayer.GetAdornerLayer(BulbList);
        if (layer != null)
        {
            _dragAdorner = new DragAdorner(BulbList, card, grabOffset);
            _dragAdorner.UpdatePosition(Mouse.GetPosition(BulbList));
            layer.Add(_dragAdorner);
        }

        try
        {
            DragDrop.DoDragDrop(fe, bulb, DragDropEffects.Move);
        }
        finally
        {
            if (_dragAdorner != null)
            {
                layer?.Remove(_dragAdorner);
                _dragAdorner = null;
            }
        }
    }

    private void BulbList_DragOver(object sender, DragEventArgs e) =>
        _dragAdorner?.UpdatePosition(e.GetPosition(BulbList));

    private void BulbCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(BulbViewModel))) return;
        if (e.Data.GetData(typeof(BulbViewModel)) is not BulbViewModel dragged) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not BulbViewModel target) return;
        if (ReferenceEquals(dragged, target)) return;

        int oldIndex = _bulbs.IndexOf(dragged);
        int newIndex = _bulbs.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;

        _bulbs.Move(oldIndex, newIndex);
        PersistBulbOrder();
    }

    private void PersistBulbOrder() =>
        _store.SetOrders(_bulbs.Select((b, i) => (b.Mac, i)));

    /// <summary>Ordena la lista según lo guardado en bulbs.json; los focos nuevos van al final.</summary>
    private void ApplyStoredBulbOrder()
    {
        var sorted = _bulbs.OrderBy(b => _store.GetOrder(b.Mac, int.MaxValue)).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int currentIndex = _bulbs.IndexOf(sorted[i]);
            if (currentIndex != i) _bulbs.Move(currentIndex, i);
        }
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
        if (fe.FindName("ColorPickerPopup") is not Popup popup) return;

        bool opening = !popup.IsOpen;
        popup.IsOpen = opening;
        if (!opening) return;

        // Al abrir, ubicar el circulito en el color que el foco tiene ahora en vez de
        // dejarlo siempre en la esquina superior izquierda.
        if (fe.DataContext is not BulbViewModel bulb) return;
        if (fe.FindName("ColorPickerCanvas") is not Canvas canvas) return;
        if (fe.FindName("ColorPickerThumb") is not Ellipse thumb) return;

        var (hue, saturation) = RgbToHueSaturation(bulb.Color.R, bulb.Color.G, bulb.Color.B);
        double x = hue / 360.0 * canvas.Width;
        double y = saturation * canvas.Height;
        Canvas.SetLeft(thumb, x - thumb.Width / 2);
        Canvas.SetTop(thumb, y - thumb.Height / 2);
    }

    // ----- Selector de color 2D (tono horizontal, mezcla con blanco vertical, como la app oficial) -----

    private bool _colorPickerDragging;
    private readonly Dictionary<string, DispatcherTimer> _colorDebounce = new();
    private readonly Dictionary<string, (double Hue, double Saturation, byte R, byte G, byte B)> _pendingColor = new();

    // Por debajo de este umbral el color queda tan cerca del blanco que conviene mandar
    // el canal nativo de blanco (igual que los botones predeterminados) en vez de RGB
    // mezclado, que da mucho menos brillo real aunque se vea "blanco" en la UI.
    private const double ColorPickerWhiteThreshold = 0.12;

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

        DebounceColorSend(bulb, hue, saturation, r, g, b);
    }

    private void DebounceColorSend(BulbViewModel bulb, double hue, double saturation, byte r, byte g, byte b)
    {
        _pendingColor[bulb.Ip] = (hue, saturation, r, g, b);

        if (!_colorDebounce.TryGetValue(bulb.Ip, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += async (s, args) =>
            {
                timer!.Stop();
                if (_pendingColor.TryGetValue(bulb.Ip, out var c))
                {
                    try
                    {
                        if (c.Saturation <= ColorPickerWhiteThreshold)
                        {
                            // Casi blanco: mismo canal nativo que los botones predeterminados
                            // (blanco cálido/frío), que llega a mucho más brillo real que RGB.
                            int kelvin = c.Hue is >= 300 or < 90 ? 2700 : 6500;
                            await _control.SetColorTempAsync(bulb.Ip, kelvin);
                        }
                        else
                        {
                            await _control.SetColorBoostedAsync(bulb.Ip, c.R, c.G, c.B);
                        }
                    }
                    catch { /* offline bulb, ignore */ }
                }
            };
            _colorDebounce[bulb.Ip] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private static Color[] GetPalette(RhythmColorMode mode) => mode switch
    {
        RhythmColorMode.PaletteOcean => PaletteOcean,
        RhythmColorMode.PaletteNeon => PaletteNeon,
        RhythmColorMode.PaletteFire => PaletteFire,
        _ => PaletteSunset
    };

    private static (byte R, byte G, byte B) LerpPalette(Color[] palette, double pos)
    {
        int i0 = (int)Math.Floor(pos) % palette.Length;
        int i1 = (i0 + 1) % palette.Length;
        double t = pos - Math.Floor(pos);
        var c0 = palette[i0];
        var c1 = palette[i1];
        byte r = (byte)Math.Round(c0.R + (c1.R - c0.R) * t);
        byte g = (byte)Math.Round(c0.G + (c1.G - c0.G) * t);
        byte b = (byte)Math.Round(c0.B + (c1.B - c0.B) * t);
        return (r, g, b);
    }

    private static (double Hue, double Saturation) RgbToHueSaturation(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;

        double hue;
        if (delta < 1e-9) hue = 0;
        else if (max == rf) hue = 60 * (((gf - bf) / delta) % 6);
        else if (max == gf) hue = 60 * (((bf - rf) / delta) + 2);
        else hue = 60 * (((rf - gf) / delta) + 4);
        if (hue < 0) hue += 360;

        double saturation = max <= 1e-9 ? 0 : delta / max;
        return (hue, saturation);
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
