using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using JiraTaskBar.App.Interop;
using JiraTaskBar.App.Services;
using JiraTaskBar.App.ViewModels;
using JiraTaskBar.Core.Storage;
using Wpf.Ui.Controls;
using static JiraTaskBar.App.Interop.NativeMethods;

namespace JiraTaskBar.App.Views;

public partial class BarWindow : Window
{
    public const double VerticalThickness = 148;
    public const double HorizontalThickness = 52;
    private const double HorizontalButtonWidth = 136;
    private const double SnapDistanceDip = 24;
    private const double UndockDragDistanceDip = 12;

    private readonly BarViewModel _vm;
    private readonly AppController _app;
    private readonly AppBar _appBar;
    private readonly DockPreviewWindow _preview = new();

    private bool _dragging;
    private POINT _dragStartCursor;
    private RECT _dragStartWindow;
    private DockEdge? _snapEdge;
    private MonitorInfo? _snapMonitor;

    public BarWindow(BarViewModel vm, AppController app)
    {
        InitializeComponent();
        _vm = vm;
        _app = app;
        DataContext = vm;
        _appBar = new AppBar(this);
        SourceInitialized += (_, _) => HideFromAltTab();
        SizeChanged += (_, _) =>
        {
            // A floating bar grows as tickets load; keep it fully on screen.
            if (IsLoaded && !_appBar.IsDocked && !_dragging)
            {
                var (x, y) = PhysicalPosition();
                ClampFloating(x, y, MonitorInfo.FromWindow(Hwnd));
            }
        };
    }

    public DockEdge DockEdge => _appBar.Edge;

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    /// <summary>Docks the bar to an edge of <paramref name="monitor"/>, or floats it there.</summary>
    public void ApplyDock(DockEdge edge, MonitorInfo monitor)
    {
        if (edge == DockEdge.Floating)
        {
            _appBar.Undock();
            SetLayout(vertical: true);
            SizeToContent = SizeToContent.Height;
            Width = VerticalThickness;
            MaxHeight = monitor.WorkArea.Height / monitor.Scale * 0.85;
            SetRoundedCorners(true);
            return;
        }

        var horizontal = edge is DockEdge.Top or DockEdge.Bottom;
        SetLayout(vertical: !horizontal);
        SizeToContent = SizeToContent.Manual;
        MaxHeight = double.PositiveInfinity;
        SetRoundedCorners(false);
        _appBar.Dock(edge, monitor.Handle, horizontal ? HorizontalThickness : VerticalThickness);
    }

    /// <summary>Moves a floating bar to a physical-pixel position, keeping it on screen.</summary>
    public void PlaceFloating(int? x, int? y, MonitorInfo monitor)
    {
        UpdateLayout();
        ClampFloating(x, y, monitor);
    }

    /// <summary>Hides the bar and releases any screen edge it reserved.</summary>
    public void HideBar()
    {
        _appBar.Undock();
        Hide();
    }

    private void ClampFloating(int? x, int? y, MonitorInfo monitor)
    {
        GetWindowRect(Hwnd, out var r);
        var wa = monitor.WorkArea;
        var margin = (int)(24 * monitor.Scale);
        var px = x ?? wa.Right - r.Width - margin;
        var py = y ?? wa.Top + (wa.Height - r.Height) / 2;
        px = Math.Clamp(px, wa.Left, Math.Max(wa.Left, wa.Right - r.Width));
        py = Math.Clamp(py, wa.Top, Math.Max(wa.Top, wa.Bottom - r.Height));
        SetWindowPos(Hwnd, IntPtr.Zero, px, py, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public (int X, int Y) PhysicalPosition()
    {
        GetWindowRect(Hwnd, out var r);
        return (r.Left, r.Top);
    }

    private void SetLayout(bool vertical)
    {
        _vm.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        _vm.ButtonWidth = vertical ? double.NaN : HorizontalButtonWidth;

        DockPanel.SetDock(Grip, vertical ? Dock.Top : Dock.Left);
        Grip.Width = vertical ? double.NaN : 16;
        Grip.Height = vertical ? 16 : double.NaN;
        GripIcon.Symbol = vertical ? SymbolRegular.ReOrderDotsHorizontal24 : SymbolRegular.ReOrderDotsVertical24;

        DockPanel.SetDock(Footer, vertical ? Dock.Bottom : Dock.Right);
        Footer.VerticalAlignment = VerticalAlignment.Center;

        Scroller.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        Scroller.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
    }

    private void SetRoundedCorners(bool round)
    {
        var preference = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void HideFromAltTab()
    {
        var style = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
    }

    // ---- Dragging & snap-to-edge docking -------------------------------------------------

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GetCursorPos(out _dragStartCursor);
        GetWindowRect(Hwnd, out _dragStartWindow);
        _dragging = true;
        _snapEdge = null;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        GetCursorPos(out var cursor);
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;

        if (_appBar.IsDocked)
        {
            var scale = MonitorInfo.FromWindow(Hwnd).Scale;
            if (Math.Abs(dx) + Math.Abs(dy) < UndockDragDistanceDip * scale) return;

            // Tear the bar off the edge and keep the grip under the cursor.
            ApplyDock(DockEdge.Floating, MonitorInfo.FromPoint(cursor));
            UpdateLayout();
            GetWindowRect(Hwnd, out var r);
            var x = cursor.X - r.Width / 2;
            var y = cursor.Y - (int)(8 * scale);
            SetWindowPos(Hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            _dragStartCursor = cursor;
            _dragStartWindow = new RECT(x, y, x + r.Width, y + r.Height);
            return;
        }

        SetWindowPos(Hwnd, IntPtr.Zero, _dragStartWindow.Left + dx, _dragStartWindow.Top + dy, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        UpdateSnapPreview(cursor);
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        EndDrag(commit: true);
        e.Handled = true;
    }

    private void Grip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) EndDrag(commit: false);
    }

    private void EndDrag(bool commit)
    {
        _dragging = false;
        _preview.Hide();
        Grip.ReleaseMouseCapture();

        if (commit && _snapEdge is { } edge && _snapMonitor is { } monitor)
        {
            _app.SetDock(edge, monitor);
        }
        else if (!_appBar.IsDocked)
        {
            _app.SaveFloatingPosition(MonitorInfo.FromWindow(Hwnd));
        }
        _snapEdge = null;
    }

    private void UpdateSnapPreview(POINT cursor)
    {
        var monitor = MonitorInfo.FromPoint(cursor);
        var b = monitor.Bounds;
        var candidates = new (DockEdge Edge, int Distance)[]
        {
            (DockEdge.Left, cursor.X - b.Left),
            (DockEdge.Right, b.Right - 1 - cursor.X),
            (DockEdge.Top, cursor.Y - b.Top),
            (DockEdge.Bottom, b.Bottom - 1 - cursor.Y),
        };
        var nearest = candidates.MinBy(c => c.Distance);

        if (nearest.Distance <= SnapDistanceDip * monitor.Scale)
        {
            _snapEdge = nearest.Edge;
            _snapMonitor = monitor;
            var thickness = nearest.Edge is DockEdge.Top or DockEdge.Bottom ? HorizontalThickness : VerticalThickness;
            _preview.ShowAt(AppBar.EdgeRect(monitor, nearest.Edge, thickness));
        }
        else
        {
            _snapEdge = null;
            _preview.Hide();
        }
    }

    // ---- Other UI ------------------------------------------------------------------------

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm.Orientation != Orientation.Horizontal) return;
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = _app.BuildMenu();
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void Pending_Click(object sender, RoutedEventArgs e) => await _app.ShowPendingWorklogsAsync();

    protected override void OnClosing(CancelEventArgs e)
    {
        // The bar only closes when the app exits; otherwise Alt+F4 just hides it to the tray.
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            // Hiding inside the Closing event throws in WPF, so hide once it has finished.
            Dispatcher.BeginInvoke(HideBar);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _appBar.Dispose();
        _preview.Close();
        base.OnClosed(e);
    }
}
