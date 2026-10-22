using System.Threading;
using System.Windows;

namespace ResetExplode;

public partial class App : Application
{
    public static App CurrentApp => (App)Current;

    private MainWindow? _window;
    private TrayIcon? _tray;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _listenCts;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AdminService.EnsureBaseFilteringEngine();

        _tray = new TrayIcon();
        _window = new MainWindow();
        _window.Closing += WindowOnClosing;
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized && (ServiceSetup.IsInstalled() || Program.StartHidden))
                _window.Hide();
        };

        StartShowListener();

        if (Program.StartHidden)
            _tray.ShowBalloon("RodrigoTech está ativo na bandeja, ao lado do relógio.");
        else
            _window.Show();
    }

    public void ShowMainWindow()
    {
        if (_window is null) return;
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void ShowFirewall()
    {
        ShowMainWindow();
        _window?.OpenFirewallPage();
    }

    public void ExitApp()
    {
        _exiting = true;
        _listenCts?.Cancel();
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    private void WindowOnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        if (ServiceSetup.IsInstalled() || Program.StartHidden)
        {
            e.Cancel = true;
            _window?.Hide();
            _tray?.ShowBalloon("Continua ativo ao lado do relógio.");
        }
        else
        {
            ExitApp();
        }
    }

    private void StartShowListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (_showEvent.WaitOne(TimeSpan.FromMilliseconds(400)))
                    Dispatcher.Invoke(ShowMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "RodrigoTech-Show"
        };
        thread.Start();
    }
}
