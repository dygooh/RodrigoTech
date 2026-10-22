using System.ServiceProcess;
using System.Threading;

namespace ResetExplode;

public static class Program
{
    public const string MutexName = @"Local\RodrigoTech.SingleInstance";
    public const string ShowEventName = @"Local\RodrigoTech.ShowWindow";
    public static bool StartHidden { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceBase.Run(new FirewallWindowsService());
            return;
        }

        StartHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
