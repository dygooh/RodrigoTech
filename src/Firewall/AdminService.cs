using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace ResetExplode;

public static class AdminService
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RelaunchAsAdmin()
    {
        try
        {
            var args = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a =>
                a.Contains(' ') ? $"\"{a}\"" : a));
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureBaseFilteringEngine()
    {
        try
        {
            using var service = new ServiceController("BFE");
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // O WFP ainda pode abrir se o serviço já estiver ativo.
        }
    }

    public static bool IsKasperskyRunning()
    {
        string[] names = ["avp", "avpui", "kavfs", "klnagent", "ksde", "avpsus"];
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (names.Any(n => process.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch
            {
                // processo sem permissão
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }
}
