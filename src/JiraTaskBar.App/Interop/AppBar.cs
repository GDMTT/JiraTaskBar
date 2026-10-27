using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using JiraTaskBar.Core.Storage;
using static JiraTaskBar.App.Interop.NativeMethods;

namespace JiraTaskBar.App.Interop;

/// <summary>
/// Registers a window as a Windows "application desktop toolbar" (like the taskbar) so it
/// reserves space on a screen edge and other maximised windows shrink around it.
/// </summary>
internal sealed class AppBar : IDisposable
{
    private readonly Window _window;
    private readonly int _callbackMessage;
    private HwndSource? _source;
    private bool _registered;
    private IntPtr _monitor;
    private double _thicknessDip;
    private bool _fullscreenAppOpen;

    public AppBar(Window window)
    {
        _window = window;
        _callbackMessage = RegisterWindowMessage("JiraTaskBar.AppBarMessage");
    }

    public DockEdge Edge { get; private set; } = DockEdge.Floating;

    public bool IsDocked => _registered;

    private IntPtr Hwnd => new WindowInteropHelper(_window).Handle;

    public void Dock(DockEdge edge, IntPtr monitor, double thicknessDip)
    {
        if (edge == DockEdge.Floating)
        {
            Undock();
            return;
        }

        EnsureHook();
        if (!_registered)
        {
            var data = NewData();
            data.uCallbackMessage = _callbackMessage;
            SHAppBarMessage(ABM_NEW, ref data);
            _registered = true;
        }

        Edge = edge;
        _monitor = monitor;
        _thicknessDip = thicknessDip;
        UpdatePosition();
    }

    public void Undock()
    {
        if (!_registered) return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
        Edge = DockEdge.Floating;
    }

    /// <summary>The rectangle (physical pixels) the bar would occupy if docked to <paramref name="edge"/>.</summary>
    public static RECT EdgeRect(MonitorInfo monitor, DockEdge edge, double thicknessDip)
    {
        var px = (int)Math.Round(thicknessDip * monitor.Scale);
        var r = monitor.Bounds;
        return edge switch
        {
            DockEdge.Left => new RECT(r.Left, r.Top, r.Left + px, r.Bottom),
            DockEdge.Right => new RECT(r.Right - px, r.Top, r.Right, r.Bottom),
            DockEdge.Top => new RECT(r.Left, r.Top, r.Right, r.Top + px),
            _ => new RECT(r.Left, r.Bottom - px, r.Right, r.Bottom),
        };
    }

    private void UpdatePosition()
    {
        if (!_registered) return;
        var monitor = MonitorInfo.FromHandle(_monitor);
        var px = (int)Math.Round(_thicknessDip * monitor.Scale);

        var data = NewData();
        data.uEdge = Edge switch
        {
            DockEdge.Left => ABE_LEFT,
            DockEdge.Right => ABE_RIGHT,
            DockEdge.Top => ABE_TOP,
            _ => ABE_BOTTOM,
        };
        data.rc = EdgeRect(monitor, Edge, _thicknessDip);

        // Let the shell move our rectangle out of the way of the taskbar / other app bars...
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        // ...then restore our thickness from the adjusted edge.
        switch (Edge)
        {
            case DockEdge.Left: data.rc.Right = data.rc.Left + px; break;
            case DockEdge.Right: data.rc.Left = data.rc.Right - px; break;
            case DockEdge.Top: data.rc.Bottom = data.rc.Top + px; break;
            default: data.rc.Top = data.rc.Bottom - px; break;
        }
        SHAppBarMessage(ABM_SETPOS, ref data);

        SetWindowPos(Hwnd, _fullscreenAppOpen ? HWND_NOTOPMOST : HWND_TOPMOST,
            data.rc.Left, data.rc.Top, data.rc.Width, data.rc.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Hwnd };

    private void EnsureHook()
    {
        if (_source is not null) return;
        _source = HwndSource.FromHwnd(Hwnd);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_registered) return IntPtr.Zero;

        if (msg == _callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    UpdatePosition();
                    break;
                case ABN_FULLSCREENAPP:
                    // Step out of the way of full-screen apps (videos, presentations).
                    _fullscreenAppOpen = lParam != IntPtr.Zero;
                    SetWindowPos(Hwnd, _fullscreenAppOpen ? HWND_NOTOPMOST : HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    break;
            }
            handled = true;
        }
        else if (msg == WM_ACTIVATE)
        {
            var data = NewData();
            SHAppBarMessage(ABM_ACTIVATE, ref data);
        }
        else if (msg == WM_WINDOWPOSCHANGED)
        {
            var data = NewData();
            SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
        }
        else if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
        {
            // Monitor layout or scaling changed: recompute after WPF has processed the change.
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!MonitorInfo.All().Any(m => m.Handle == _monitor))
                    _monitor = MonitorFromWindow(Hwnd, MONITOR_DEFAULTTONEAREST);
                UpdatePosition();
            });
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Undock();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
