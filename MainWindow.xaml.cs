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
    private readonly TaskbarThumbnailManager _taskbarMgr = new();
    private readonly AudioReactiveService _audioReactive = new();
    private readonly Dictionary<string, DispatcherTimer> _brightnessDebounce = new();

    // Re-escanea la red para detectar focos que se conectaron o desconectaron.
    private readonly DispatcherTimer _periodicScanTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private WinForms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _appIcon;
    private bool _cleanedUp;

    private IntPtr _iconPowerOn, _iconPowerOff, _iconBrightUp, _iconBrightDown, _iconWarmWhite, _iconCoolWhite;

    private const uint ThumbIdPower = 1;
    private const uint ThumbIdBrightDown = 2;
    private const uint ThumbIdBrightUp = 3;
    private const uint ThumbIdWarmWhite = 4;
    private const uint ThumbIdCoolWhite = 5;

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

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _appIcon?.Dispose();
        _audioReactive.Dispose();

        foreach (var h in new[] { _iconPowerOn, _iconPowerOff, _iconBrightUp, _iconBrightDown, _iconWarmWhite, _iconCoolWhite })
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

    private enum RhythmColorMode { Rainbow, WarmCoolWhite, FixedList }

    private static readonly Color WarmWhite = (Color)ColorConverter.ConvertFromString("#FFD9A6")!;
    private static readonly Color CoolWhite = (Color)ColorConverter.ConvertFromString("#EAF4FF")!;

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
    private int _fixedColorIndex;
    private bool _warmToggle;
    private bool _rhythmRunning;

    private void RhythmButton_Click(object sender, RoutedEventArgs e)
    {
        RainbowModeRadio.IsChecked = _rhythmMode == RhythmColorMode.Rainbow;
        WhiteModeRadio.IsChecked = _rhythmMode == RhythmColorMode.WarmCoolWhite;
        FixedModeRadio.IsChecked = _rhythmMode == RhythmColorMode.FixedList;
        RhythmStopButton.IsEnabled = _rhythmRunning;
        RhythmPopup.IsOpen = !RhythmPopup.IsOpen;
    }

    private void RhythmModeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        _rhythmMode = tag switch
        {
            "FixedList" => RhythmColorMode.FixedList,
            "WarmCoolWhite" => RhythmColorMode.WarmCoolWhite,
            _ => RhythmColorMode.Rainbow
        };
        _rhythmHue = 0;
        _fixedColorIndex = 0;
        _warmToggle = false;

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
                case RhythmColorMode.WarmCoolWhite:
                    _warmToggle = !_warmToggle;
                    var white = _warmToggle ? WarmWhite : CoolWhite;
                    (r, g, b) = (white.R, white.G, white.B);
                    break;
                default:
                    // Cada golpe avanza el tono; su "fuerza" acelera el salto de color.
                    _rhythmHue = (_rhythmHue + 35 + strength * 40) % 360;
                    (r, g, b) = HsvToRgb(_rhythmHue, 1.0, 1.0);
                    break;
            }

            int brightness = 35 + (int)Math.Round(Math.Clamp(strength, 0, 1) * 65);

            foreach (var bulb in targets)
            {
                bulb.Color = Color.FromRgb(r, g, b);
                bulb.Brightness = brightness;
                _ = SafeSetColorAndBrightness(bulb.Ip, r, g, b, brightness);
            }
        });
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

        _taskbarMgr.SetButtons(new List<ThumbButtonDef>
        {
            new(ThumbIdPower, _iconPowerOff, "Encender/apagar todas"),
            new(ThumbIdBrightDown, _iconBrightDown, "Bajar brillo"),
            new(ThumbIdBrightUp, _iconBrightUp, "Subir brillo"),
            new(ThumbIdWarmWhite, _iconWarmWhite, "Blanco cálido"),
            new(ThumbIdCoolWhite, _iconCoolWhite, "Blanco frío"),
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

    private async Task OnThumbButtonClicked(uint id)
    {
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
