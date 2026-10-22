using System.ServiceProcess;

namespace ResetExplode;

public sealed class FirewallWindowsService : ServiceBase
{
    public const string Name = "RodrigoTech";
    public const string Display = "RodrigoTech";
    public const string Description = "RodrigoTech: bloqueio de internet por aplicativo (WFP).";

    private CancellationTokenSource? _cts;
    private Thread? _worker;
    private WfpService? _wfp;

    public FirewallWindowsService()
    {
        ServiceName = Name;
        CanStop = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _wfp = new WfpService();
        _worker = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "RodrigoTech-WFP"
        };
        _worker.Start();
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(5));
        _wfp?.Dispose();
        _wfp = null;
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                AdminService.EnsureBaseFilteringEngine();
                _wfp?.EnsureReady();
            }
            catch
            {
                // o serviço tenta de novo no próximo ciclo
            }

            token.WaitHandle.WaitOne(TimeSpan.FromMinutes(2));
        }
    }
}
