using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using JiraTaskBar.App.Interop;
using static JiraTaskBar.App.Interop.NativeMethods;

namespace JiraTaskBar.App.Views;

/// <summary>Translucent highlight showing where the bar will dock when the drag is released.</summary>
internal sealed class DockPreviewWindow : Window
{
    public DockPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Width = 1;
        Height = 1;
        var accent = SystemParameters.WindowGlassColor;
        Background = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
        BorderThickness = new Thickness(2);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE));
        };
    }

    public void ShowAt(RECT rect)
    {
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);
    }
}
