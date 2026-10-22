using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ResetExplode;

public static class WindowsFirewallRules
{
    private const string Description = "AppFirewall";
    private const int ActionBlock = 0;
    private const int DirIn = 1;
    private const int DirOut = 2;
    private const int ProtocolAny = 256;
    private const int ProfileAll = unchecked((int)0x7FFFFFFF);

    public static void Block(string exePath)
    {
        exePath = Path.GetFullPath(exePath);
        try
        {
            var policy = CreatePolicy();
            TryEnableFirewall(policy);
            Remove(policy, exePath);
            Add(policy, exePath, DirOut);
            Add(policy, exePath, DirIn);
            return;
        }
        catch
        {
            BlockWithNetsh(exePath);
        }
    }

    public static void Unblock(string exePath)
    {
        exePath = Path.GetFullPath(exePath);
        try { Remove(CreatePolicy(), exePath); }
        catch { /* tenta netsh abaixo */ }
        UnblockWithNetsh(exePath);
    }

    public static void UnblockAll()
    {
        try
        {
            var policy = CreatePolicy();
            foreach (var name in EnumerateOurRuleNames(policy))
            {
                try { policy.Rules.Remove(name); }
                catch { /* já ausente */ }
            }
        }
        catch
        {
            // netsh cobre o restante
        }

        RunNetsh("advfirewall firewall delete rule name=all description=AppFirewall");
    }

    public static HashSet<string> GetBlockedPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var policy = CreatePolicy();
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    string? desc = rule.Description;
                    string? app = rule.ApplicationName;
                    if (desc == Description && !string.IsNullOrWhiteSpace(app))
                        paths.Add(app);
                }
                catch
                {
                    // regra inacessível
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(rule); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // COM indisponível
        }

        return paths;
    }

    private static dynamic CreatePolicy()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new InvalidOperationException("Firewall do Windows indisponível.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Falha ao abrir o Firewall do Windows.");
    }

    private static void TryEnableFirewall(dynamic policy)
    {
        foreach (var profile in new[] { 1, 2, 4 })
        {
            try { policy.FirewallEnabled[profile] = true; }
            catch { /* perfil bloqueado por política */ }
        }
    }

    private static void Add(dynamic policy, string exePath, int direction)
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwRule")
            ?? throw new InvalidOperationException("Firewall do Windows indisponível.");
        dynamic rule = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Falha ao criar regra de firewall.");
        var tag = direction == DirOut ? "out" : "in";
        rule.Name = $"AppFirewall {tag} {Path.GetFileName(exePath)} {Guid.NewGuid():N}";
        rule.Description = Description;
        rule.ApplicationName = exePath;
        rule.Action = ActionBlock;
        rule.Direction = direction;
        rule.Enabled = true;
        rule.Profiles = ProfileAll;
        rule.Protocol = ProtocolAny;
        policy.Rules.Add(rule);
    }

    private static void Remove(dynamic policy, string exePath)
    {
        foreach (var name in EnumerateOurRuleNames(policy, exePath))
        {
            try { policy.Rules.Remove(name); }
            catch { /* não existia */ }
        }
    }

    private static List<string> EnumerateOurRuleNames(dynamic policy, string? exePath = null)
    {
        var names = new List<string>();
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                string? desc = rule.Description;
                string? app = rule.ApplicationName;
                string? name = rule.Name;
                if (desc != Description || string.IsNullOrWhiteSpace(name))
                    continue;
                if (exePath is null || string.Equals(app, exePath, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(rule); } catch { /* ignore */ }
            }
        }
        return names;
    }

    private static void BlockWithNetsh(string exePath)
    {
        var file = Path.GetFileNameWithoutExtension(exePath);
        RunNetsh($"advfirewall firewall add rule name=\"AppFirewall out {file}\" dir=out action=block program=\"{exePath}\" enable=yes profile=any description=AppFirewall");
        RunNetsh($"advfirewall firewall add rule name=\"AppFirewall in {file}\" dir=in action=block program=\"{exePath}\" enable=yes profile=any description=AppFirewall");
    }

    private static void UnblockWithNetsh(string exePath)
    {
        RunNetsh($"advfirewall firewall delete rule program=\"{exePath}\" description=AppFirewall");
    }

    private static void RunNetsh(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process?.WaitForExit(20000);
        if (process is { ExitCode: not 0 and not 1 })
            throw new InvalidOperationException("netsh falhou ao aplicar a regra.");
    }
}
