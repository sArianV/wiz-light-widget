using System.Diagnostics;
using System.Threading;
using System.Windows;
using WizLightWidget.Services;

namespace WizLightWidget;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "WizLightWidget_SingleInstance_9F2C1B", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("WiZ Light Widget ya se está ejecutando (revisa la bandeja del sistema).",
                "WiZ Light Widget", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();
        _ = BootstrapAsync(splash);
    }

    private async Task BootstrapAsync(SplashWindow splash)
    {
        splash.SetStatus("Buscando actualizaciones...");

        try
        {
            var updateService = new UpdateService();
            var update = await updateService.CheckForUpdateAsync();

            if (update != null)
            {
                splash.SetStatus($"Descargando versión {update.Version}...");
                var progress = new Progress<double>(p => splash.SetProgress(p));
                var tempExe = await updateService.DownloadUpdateAsync(update.DownloadUrl, progress);

                var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(currentExe))
                {
                    splash.SetStatus("Instalando actualización...");
                    UpdateService.LaunchUpdateAndExit(tempExe, currentExe);
                    return; // El proceso se cierra desde LaunchUpdateAndExit.
                }
            }
        }
        catch
        {
            // Si algo falla (sin internet, descarga interrumpida, etc.) seguimos con la versión actual.
        }

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        splash.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
