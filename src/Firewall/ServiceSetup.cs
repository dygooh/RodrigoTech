using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace ResetExplode;

public static class ServiceSetup
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RodrigoTech");

    public static string InstalledExe => Path.Combine(InstallDir, "RodrigoTech.exe");

    public static bool IsInstalled()
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

    public static string StatusText()
    {
        if (!IsInstalled())
            return "Modo portátil (sem serviço em segundo plano)";

        return IsRunning()
            ? "Serviço ativo (inicia com o Windows)"
            : "Serviço instalado (parado)";
    }

    public static void Install()
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            throw new InvalidOperationException("Não foi possível localizar o executável atual.");

        Directory.CreateDirectory(InstallDir);

        var sameFile = current.Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);
        if (!sameFile)
        {
            TryStop();
            TerminateInstalledProcesses();
            File.Copy(current, InstalledExe, overwrite: true);
        }

        if (!IsInstalled())
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

    public static void EnsureInstalled() => Install();

    public static void Uninstall()
    {
        if (IsInstalled())
        {
            TryStop();
            try { RunSc($"delete {FirewallWindowsService.Name}"); } catch { }
        }

        try
        {
            Run("schtasks.exe", "/Delete /TN \"RodrigoTechTray\" /F");
        }
        catch { }

        try
        {
            var current = Environment.ProcessPath;
            if (!string.Equals(current, InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                TerminateInstalledProcesses();
                if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
                if (Directory.Exists(InstallDir) && !Directory.EnumerateFileSystemEntries(InstallDir).Any())
                {
                    Directory.Delete(InstallDir);
                }
            }
        }
        catch { }
    }

    public static void EnsureTrayStartup()
    {
        var command = $"\\\"{InstalledExe}\\\" --tray";
        Run("schtasks.exe", $"/Create /TN \"RodrigoTechTray\" /TR \"{command}\" /SC ONLOGON /RL HIGHEST /F");
    }

    private static void TerminateInstalledProcesses()
    {
        try
        {
            var currentId = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("RodrigoTech"))
            {
                if (proc.Id != currentId)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                    catch { }
                }
            }
        }
        catch { }
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
            // ainda não instalado ou já parado
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
