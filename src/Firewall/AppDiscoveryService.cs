using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

public static class AppDiscoveryService
{
    public static List<AppEntry> Discover()
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        AddFromAppPaths(map);
        AddFromUninstall(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        AddFromUninstall(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", map);
        AddFromUninstall(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        AddFromStartMenu(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), map, "Atalho");
        AddFromStartMenu(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), map, "Atalho");
        AddFromRunningProcesses(map);

        return map.Values
            .OrderBy(a => a.Publisher, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static AppEntry? FromPath(string path, string origin)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!IsCandidate(path)) return null;

            FileVersionInfo? info = null;
            try { info = FileVersionInfo.GetVersionInfo(path); } catch { /* sem versão */ }

            var name = FirstNonEmpty(info?.FileDescription, info?.ProductName, Path.GetFileNameWithoutExtension(path));
            var publisher = FirstNonEmpty(info?.CompanyName, "Desconhecido");

            return new AppEntry
            {
                Path = path,
                Name = Clean(name),
                Publisher = Clean(publisher),
                Origin = origin,
                Icon = IconService.FromFile(path)
            };
        }
        catch
        {
            return null;
        }
    }

    private static void Add(Dictionary<string, AppEntry> map, string path, string origin)
    {
        var entry = FromPath(path, origin);
        if (entry is null) return;
        if (map.TryGetValue(entry.Path, out var existing))
        {
            if (existing.Origin == "Em execução" || origin == "Instalado") return;
            map[entry.Path] = entry;
            return;
        }
        map[entry.Path] = entry;
    }

    private static void AddFromAppPaths(Dictionary<string, AppEntry> map)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    var path = sub?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(path))
                        Add(map, StripQuotes(path), "Instalado");
                }
            }
            catch
            {
                // hive indisponível
            }
        }
    }

    private static void AddFromUninstall(RegistryHive hive, string subKey, Dictionary<string, AppEntry> map)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub is null) continue;

                var displayIcon = sub.GetValue("DisplayIcon") as string;
                var installLocation = sub.GetValue("InstallLocation") as string;
                var displayName = sub.GetValue("DisplayName") as string;

                var fromIcon = ExtractExe(displayIcon);
                if (fromIcon is not null)
                {
                    Add(map, fromIcon, "Instalado");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation) && !string.IsNullOrWhiteSpace(displayName))
                {
                    try
                    {
                        var token = displayName.Split(' ')[0];
                        var guessed = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                                .Contains(token, StringComparison.OrdinalIgnoreCase));
                        if (guessed is not null)
                            Add(map, guessed, "Instalado");
                    }
                    catch
                    {
                        // pasta sem permissão
                    }
                }
            }
        }
        catch
        {
            // chave indisponível
        }
    }

    private static void AddFromStartMenu(string root, Dictionary<string, AppEntry> map, string origin)
    {
        if (!Directory.Exists(root)) return;
        foreach (var lnk in EnumerateFilesSafe(root, "*.lnk"))
        {
            var target = ResolveShortcut(lnk);
            if (!string.IsNullOrWhiteSpace(target))
                Add(map, target, origin);
        }
    }

    private static void AddFromRunningProcesses(Dictionary<string, AppEntry> map)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    Add(map, path, "Em execução");
            }
            catch
            {
                // processo protegido
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files = [];
            string[] dirs = [];
            try { files = Directory.GetFiles(dir, pattern); } catch { /* ignore */ }
            try { dirs = Directory.GetDirectories(dir); } catch { /* ignore */ }
            foreach (var file in files) yield return file;
            foreach (var child in dirs) pending.Push(child);
        }
    }

    private static bool IsCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(path)) return false;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;

        var own = Environment.ProcessPath;
        if (own is not null && path.Equals(own, StringComparison.OrdinalIgnoreCase)) return false;

        var file = Path.GetFileName(path);
        if (file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Equals("install.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string? ExtractExe(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var value = StripQuotes(displayIcon);
        var comma = value.LastIndexOf(',');
        if (comma > 1 && int.TryParse(value[(comma + 1)..], out _))
            value = value[..comma];
        value = StripQuotes(value);
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : null;
    }

    private static string StripQuotes(string value)
        => value.Trim().Trim('"');

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string Clean(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? ResolveShortcut(string lnkPath)
    {
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var target = sb.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link is not null)
                Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
