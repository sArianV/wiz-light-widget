using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace WizLightWidget.Native;

public static class WindowMonitorHelper
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>Monitores de izquierda a derecha (y de arriba abajo), para que alternar sea predecible.</summary>
    public static List<WinForms.Screen> GetOrderedScreens() =>
        WinForms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

    /// <summary>Centra la ventana en el área de trabajo del monitor indicado y la trae al frente.</summary>
    public static void MoveToScreenAndActivate(IntPtr hwnd, WinForms.Screen target)
    {
        var wa = target.WorkingArea;

        // Primero se la deja adentro del monitor destino para que Windows le aplique el DPI de ese
        // monitor; recién ahí se mide, porque el tamaño en píxeles puede cambiar al cruzar de monitor.
        SetWindowPos(hwnd, IntPtr.Zero, wa.X + 40, wa.Y + 40, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        int x = Math.Max(wa.X, wa.X + (wa.Width - w) / 2);
        int y = Math.Max(wa.Y, wa.Y + (wa.Height - h) / 2);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        ForceForeground(hwnd);
    }

    // Windows suele rechazar SetForegroundWindow si el proceso no tiene el foco; engancharse al hilo
    // de la ventana activa es el truco habitual para poder traer esta al frente igual.
    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == hwnd) return;

        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != thisThread && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
