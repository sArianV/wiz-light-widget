using System.Diagnostics;
using Microsoft.Win32;

namespace WizLightWidget.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WizLightWidget";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return value != null && value.Trim('"').Equals(GetExePath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
            key.SetValue(ValueName, $"\"{GetExePath()}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetExePath() =>
        Process.GetCurrentProcess().MainModule?.FileName ?? "";
}
