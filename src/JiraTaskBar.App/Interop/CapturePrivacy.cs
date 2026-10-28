using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using JiraTaskBar.App.Services;
using static JiraTaskBar.App.Interop.NativeMethods;

namespace JiraTaskBar.App.Interop;

/// <summary>
/// Keeps ticket details out of screen shares, screenshots and recordings (threat model I4).
/// The bar is always on top, so without this its titles show in every Teams/Zoom share.
/// Tooltips and context menus are separate popup windows, so they are covered too.
/// </summary>
public static class CapturePrivacy
{
    private static bool _registered;

    public static bool Enabled { get; private set; }

    /// <summary>Turns exclusion on or off for <paramref name="window"/> and all tooltips/menus opened from now on.</summary>
    public static void Apply(Window window, bool enabled)
    {
        Enabled = enabled;
        if (!_registered)
        {
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
            _registered = true;
        }
        SetAffinity(new WindowInteropHelper(window).Handle, enabled);
    }

    private static void OnPopupOpened(object sender, RoutedEventArgs e)
    {
        if (Enabled && sender is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source)
            SetAffinity(source.Handle, true);
    }

    private static void SetAffinity(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE))
            Log.Error($"SetWindowDisplayAffinity failed (Windows 10 2004 or later is required), error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }
}
