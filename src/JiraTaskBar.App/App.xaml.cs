using System.Windows;
using System.Windows.Threading;
using JiraTaskBar.App.Services;
using Wpf.Ui.Appearance;

namespace JiraTaskBar.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The release workflow runs the packaged executable on a clean Windows runner.
        // Reaching this point proves that the single-file bundle and WPF resources loaded;
        // exit before credentials, network access, or tray UI are required.
        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            Shutdown(0);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\JiraTaskBar.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Jira TaskBar is already running. Look for it in the system tray.", "Jira TaskBar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        ApplySystemTheme();

        _controller = new AppController(this);
        await _controller.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    public static void ApplySystemTheme()
    {
        var system = ApplicationThemeManager.GetSystemTheme();
        var theme = system is SystemTheme.Light or SystemTheme.HCWhite or SystemTheme.Sunrise or SystemTheme.Flow
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, true);
    }

    public static bool IsDarkTheme => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "Jira TaskBar – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
