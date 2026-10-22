using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace ResetExplode;

public static class ServiceSetup
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RodrigoTech");

    public static string InstalledExe => Path.Combine(InstallDir, "RodrigoTech.exe");

    public static bool IsRunning()
    {
        try
        {
            using var service = new ServiceController(FirewallWindowsService.Name);
            return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    public static string StatusText() => IsRunning() ? "Serviço ativo (inicia com o Windows)" : "Serviço parado";

    public static void EnsureInstalled()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            throw new InvalidOperationException("Não foi possível localizar o executável.");

        Directory.CreateDirectory(InstallDir);

        var sameFile = current.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);
        if (!sameFile)
        {
            TryStop();
            File.Copy(current, InstalledExe, overwrite: true);
        }

        if (!Exists())
        {
            RunSc($"create {FirewallWindowsService.Name} binPath= \"\\\"{InstalledExe}\\\" --service\" start= auto DisplayName= \"{FirewallWindowsService.Display}\" depend= BFE");
            RunSc($"description {FirewallWindowsService.Name} \"{FirewallWindowsService.Description}\"");
            RunSc($"failure {FirewallWindowsService.Name} reset= 86400 actions= restart/5000/restart/5000/restart/5000");
        }
        else
        {
            RunSc($"config {FirewallWindowsService.Name} binPath= \"\\\"{InstalledExe}\\\" --service\" start= auto depend= BFE");
        }

        TryStart();
        EnsureTrayStartup();
    }

    public static void EnsureTrayStartup()
    {
        var command = $"\\\"{InstalledExe}\\\" --tray";
        Run("schtasks.exe", $"/Create /TN \"RodrigoTechTray\" /TR \"{command}\" /SC ONLOGON /RL HIGHEST /F");
    }

    private static bool Exists()
    {
        try
        {
            using var service = new ServiceController(FirewallWindowsService.Name);
            _ = service.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryStop()
    {
        try
        {
            using var service = new ServiceController(FirewallWindowsService.Name);
            if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                return;
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
        }
        catch
        {
            // ainda não instalado
        }
    }

    private static void TryStart()
    {
        using var service = new ServiceController(FirewallWindowsService.Name);
        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            return;
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    private static void RunSc(string arguments) => Run("sc.exe", arguments);

    private static void Run(string fileName, string arguments)
    {
        var path = Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(Environment.SystemDirectory, fileName);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null)
            throw new InvalidOperationException($"Falha ao executar {fileName}.");
        process.WaitForExit(20000);
    }
}
