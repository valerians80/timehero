using Microsoft.Win32;

namespace TimeHero.App;

/// <summary>Avvio automatico con Windows (chiave HKCU\...\Run, nessun privilegio admin).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TimeHero";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                      ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) k.SetValue(ValueName, $"\"{exe}\"");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
