using System.Windows;
using System.Threading;

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
