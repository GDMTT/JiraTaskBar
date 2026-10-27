using System.Diagnostics;
using Microsoft.Win32;

namespace JiraTaskBar.App.Services;

public static class Shell
{
    public static void Open(Uri url) => Open(url.AbsoluteUri);

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {target}", ex);
        }
    }
}

/// <summary>Registers the app under HKCU\...\Run so it starts at sign-in.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JiraTaskBar";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
