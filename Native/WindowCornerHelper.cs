using System.Runtime.InteropServices;

namespace WizLightWidget.Native;

/// <summary>
/// Redondea las esquinas de una ventana usando el compositor de Windows 11 (DWM),
/// en vez de AllowsTransparency de WPF (que puede romper la miniatura del taskbar).
/// En Windows 10 o versiones sin soporte, simplemente no hace nada.
/// </summary>
public static class WindowCornerHelper
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Windows 10 u otro caso sin soporte: la ventana queda con esquinas cuadradas.
        }
    }
}
