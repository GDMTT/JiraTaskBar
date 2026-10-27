using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace JiraTaskBar.App.Services;

public static class Shell
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttps, Uri.UriSchemeHttp, Uri.UriSchemeMailto,
    };

    /// <summary>
    /// True for absolute http(s)/mailto URLs. Anything else (file:, UNC paths, custom protocol
    /// handlers such as ms-msdt:) could launch programs via ShellExecute, so it is refused.
    /// </summary>
    public static bool IsSafeUrl(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme) && !uri.IsUnc;

    /// <summary>Opens a web or mailto link in the default handler; other schemes are refused and logged.</summary>
    public static bool OpenUrl(Uri url) => OpenUrl(url.AbsoluteUri);

    public static bool OpenUrl(string url)
    {
        if (!IsSafeUrl(url))
        {
            Log.Error($"Refused to open non-web link: {url}");
            return false;
        }
        return Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>Opens an existing local folder in Explorer.</summary>
    public static bool OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add(path);
        return Start(info);
    }

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {info.FileName}", ex);
            return false;
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
