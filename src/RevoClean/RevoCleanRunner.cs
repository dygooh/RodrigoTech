using System.Diagnostics;

namespace ResetExplode;

/// <summary>
/// Executor do desinstalador oficial do fabricante ou comando de remoção de pacote AppX da Loja.
/// </summary>
public sealed class RevoCleanRunner
{
    public InstalledProgram App { get; }
    public bool Quiet { get; }
    public Process? RunningProcess { get; private set; }
    public bool IsRunning { get; private set; }
    public int? ExitCode { get; private set; }

    public RevoCleanRunner(InstalledProgram app, bool quiet = false)
    {
        App = app;
        Quiet = quiet;
    }

    public string BuildCommand()
    {
        if (App.AppType == "store")
        {
            return $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Remove-AppxPackage -Package '{App.KeyName}'\"";
        }

        string cmd = (Quiet && !string.IsNullOrEmpty(App.QuietUninstallString))
            ? App.QuietUninstallString
            : App.UninstallString;

        if (string.IsNullOrWhiteSpace(cmd) && App.IsWindowsInstaller)
        {
            cmd = $"msiexec.exe /x \"{App.KeyName}\"";
        }

        if (!string.IsNullOrEmpty(cmd))
        {
            if (cmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && cmd.Contains("/i", StringComparison.OrdinalIgnoreCase))
            {
                cmd = cmd.Replace("/i", "/x", StringComparison.OrdinalIgnoreCase);
            }
        }

        return cmd.Trim();
    }

    public async Task<int?> LaunchAsync(Action<int?>? onFinished = null)
    {
        string cmd = BuildCommand();
        if (string.IsNullOrWhiteSpace(cmd)) return null;

        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {cmd}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                RunningProcess = Process.Start(psi);
                if (RunningProcess == null) return null;

                IsRunning = true;
                RunningProcess.WaitForExit();
                ExitCode = RunningProcess.ExitCode;
                IsRunning = false;

                onFinished?.Invoke(ExitCode);
                return ExitCode;
            }
            catch
            {
                IsRunning = false;
                return null;
            }
        });
    }
}
