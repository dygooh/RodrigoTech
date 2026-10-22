using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Varredor de programas instalados no Windows (Win32 Desktop e Aplicativos da Microsoft Store AppX/UWP).
/// </summary>
public static class RevoCleanScanner
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static async Task<List<InstalledProgram>> ScanAllAsync(bool includeSystemComponents = false, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var win32 = ScanWin32Programs(includeSystemComponents);
            var store = ScanStoreApps();

            var combined = new List<InstalledProgram>(win32.Count + store.Count);
            combined.AddRange(win32);
            combined.AddRange(store);
            combined.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return combined;
        }, ct);
    }

    public static List<InstalledProgram> ScanWin32Programs(bool includeSystem = false)
    {
        var list = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var targets = new (RegistryHive Hive, RegistryView View, string Root)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM"),
            (RegistryHive.CurrentUser, RegistryView.Default, "HKCU")
        };

        foreach (var (hive, view, root) in targets)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninst = baseKey.OpenSubKey(UninstallSubKey);
                if (uninst == null) continue;

                foreach (var subName in uninst.GetSubKeyNames())
                {
                    string uniqueId = $"{root}\\{subName}";
                    if (!seen.Add(uniqueId)) continue;

                    try
                    {
                        using var sub = uninst.OpenSubKey(subName);
                        if (sub == null) continue;

                        string name = sub.GetValue("DisplayName")?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        if (!includeSystem)
                        {
                            if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;
                            if (sub.GetValue("ParentKeyName") != null) continue;
                        }

                        string pub = sub.GetValue("Publisher")?.ToString()?.Trim() ?? "";
                        string ver = sub.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "";
                        string dateRaw = sub.GetValue("InstallDate")?.ToString()?.Trim() ?? "";
                        string loc = sub.GetValue("InstallLocation")?.ToString()?.Trim() ?? "";
                        string uninstCmd = sub.GetValue("UninstallString")?.ToString()?.Trim() ?? "";
                        string quietCmd = sub.GetValue("QuietUninstallString")?.ToString()?.Trim() ?? "";
                        string iconPath = CleanIconPath(sub.GetValue("DisplayIcon")?.ToString()?.Trim() ?? "");
                        bool msi = sub.GetValue("WindowsInstaller") is int wi && wi == 1;

                        long sizeBytes = 0;
                        if (sub.GetValue("EstimatedSize") is int es && es > 0)
                            sizeBytes = (long)es * 1024;
                        else if (sub.GetValue("EstimatedSize") is long esL && esL > 0)
                            sizeBytes = esL * 1024;

                        // Se não houver InstallLocation informado, tenta inferir a partir do DisplayIcon ou comando
                        if (string.IsNullOrEmpty(loc))
                            loc = InferLocation(iconPath, uninstCmd);

                        // Se não tinha tamanho no registro mas a pasta existe, calcula
                        if (sizeBytes <= 0 && !string.IsNullOrEmpty(loc) && Directory.Exists(loc))
                        {
                            try { sizeBytes = GetDirectorySize(loc, maxDepth: 4); } catch { }
                        }

                        list.Add(new InstalledProgram
                        {
                            Name = name,
                            Publisher = pub,
                            Version = ver,
                            InstallDate = dateRaw,
                            DisplayDate = FormatDate(dateRaw),
                            InstallLocation = loc,
                            UninstallString = uninstCmd,
                            QuietUninstallString = quietCmd,
                            DisplayIcon = iconPath,
                            EstimatedSizeBytes = sizeBytes,
                            FormattedSize = FormatSize(sizeBytes),
                            IsSystemComponent = sub.GetValue("SystemComponent") is int s && s == 1,
                            IsWindowsInstaller = msi,
                            RegistryRoot = root,
                            RegistryPath = $"{UninstallSubKey}\\{subName}",
                            KeyName = subName,
                            AppType = "win32"
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        return list;
    }

    public static List<InstalledProgram> ScanStoreApps()
    {
        var list = new List<InstalledProgram>();
        try
        {
            string psCmd = "Get-AppxPackage | " +
                "Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable } | " +
                "Select-Object Name, PackageFullName, Version, InstallLocation, PackageFamilyName | " +
                "ConvertTo-Csv -NoTypeInformation -Delimiter ';'";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var p = Process.Start(psi);
            if (p == null) return list;

            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);

            if (string.IsNullOrWhiteSpace(output)) return list;

            using var reader = new StringReader(output);
            string? header = reader.ReadLine();
            if (header == null) return list;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] parts = line.Split(';');
                if (parts.Length < 5) continue;

                string rawName = parts[0].Trim('"', ' ');
                string pkgFullName = parts[1].Trim('"', ' ');
                string version = parts[2].Trim('"', ' ');
                string installLoc = parts[3].Trim('"', ' ');
                string familyName = parts[4].Trim('"', ' ');

                if (string.IsNullOrEmpty(rawName) || string.IsNullOrEmpty(pkgFullName))
                    continue;

                string friendlyName = ExtractStoreFriendlyName(installLoc, rawName);

                long sizeBytes = 0;
                if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                {
                    try { sizeBytes += GetDirectorySize(installLoc, maxDepth: 3); } catch { }
                }

                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(familyName) && !string.IsNullOrEmpty(localApp))
                {
                    string userData = Path.Combine(localApp, "Packages", familyName);
                    if (Directory.Exists(userData))
                    {
                        try { sizeBytes += GetDirectorySize(userData, maxDepth: 4); } catch { }
                    }
                }

                list.Add(new InstalledProgram
                {
                    Name = friendlyName,
                    Publisher = "Microsoft Store",
                    Version = version,
                    InstallLocation = installLoc,
                    EstimatedSizeBytes = sizeBytes,
                    FormattedSize = FormatSize(sizeBytes),
                    RegistryRoot = "AppX",
                    RegistryPath = pkgFullName,
                    KeyName = pkgFullName,
                    AppType = "store",
                    PackageFamilyName = familyName,
                    DisplayIcon = installLoc
                });
            }
        }
        catch { }

        return list;
    }

    private static string ExtractStoreFriendlyName(string installLoc, string pkgName)
    {
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            string manifest = Path.Combine(installLoc, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                try
                {
                    var doc = XDocument.Load(manifest);
                    var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
                    var visual = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                    if (visual != null)
                    {
                        string? disp = visual.Attribute("DisplayName")?.Value;
                        if (!string.IsNullOrWhiteSpace(disp) && !disp.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                            return disp.Trim();
                    }
                    var dispElem = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DisplayName");
                    if (dispElem != null)
                    {
                        string val = dispElem.Value;
                        if (!string.IsNullOrWhiteSpace(val) && !val.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                            return val.Trim();
                    }
                }
                catch { }
            }
        }

        // Heurística de limpeza: "SpotifyAB.SpotifyMusic" -> "Spotify"
        string clean = pkgName;
        if (clean.Contains('.'))
        {
            string[] dots = clean.Split('.');
            string last = dots[^1];
            foreach (var suffix in new[] { "Music", "App", "Desktop", "Client", "Store" })
            {
                if (last.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && last.Length > suffix.Length)
                {
                    last = last[..^suffix.Length];
                    break;
                }
            }
            clean = last;
        }
        return clean;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "Desconhecido";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {units[order]}";
    }

    public static string FormatDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string clean = raw.Trim();
        if (clean.Length == 8 && clean.All(char.IsDigit))
            return $"{clean[6..8]}/{clean[4..6]}/{clean[0..4]}";
        return clean;
    }

    public static string CleanIconPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string c = raw.Trim('"', '\'', ' ');
        int comma = c.IndexOf(',');
        if (comma > 0) c = c[..comma].Trim('"', '\'', ' ');
        return c;
    }

    private static string InferLocation(string iconPath, string uninstCmd)
    {
        string candidate = "";
        if (!string.IsNullOrEmpty(iconPath) && (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)))
            candidate = iconPath;
        else if (!string.IsNullOrEmpty(uninstCmd))
        {
            string c = uninstCmd.Trim('"', ' ');
            int exeIdx = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0) candidate = c[..(exeIdx + 4)].Trim('"', ' ');
        }

        if (!string.IsNullOrEmpty(candidate))
        {
            try
            {
                string? dir = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return dir;
            }
            catch { }
        }
        return "";
    }

    public static long GetDirectorySize(string path, int maxDepth = 4, int currentDepth = 0)
    {
        if (currentDepth > maxDepth) return 0;
        long total = 0;
        try
        {
            var di = new DirectoryInfo(path);
            foreach (var f in di.GetFiles())
                total += f.Length;
            if (currentDepth < maxDepth)
            {
                foreach (var d in di.GetDirectories())
                    total += GetDirectorySize(d.FullName, maxDepth, currentDepth + 1);
            }
        }
        catch { }
        return total;
    }

    public static ImageSource? ExtractAppIcon(string iconOrExePath)
    {
        if (string.IsNullOrWhiteSpace(iconOrExePath)) return null;
        try
        {
            string clean = CleanIconPath(iconOrExePath);
            if (!File.Exists(clean) && !Directory.Exists(clean)) return null;

            var shinfo = new SHFILEINFO();
            IntPtr hImg = SHGetFileInfo(clean, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON);
            if (hImg != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                DestroyIcon(shinfo.hIcon);
                return src;
            }
        }
        catch { }
        return null;
    }

    public static void ExportToCsv(IEnumerable<InstalledProgram> apps, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nome;Publicador;Versão;Data de Instalação;Tamanho;Tipo;Localização;Comando de Desinstalação");
        foreach (var a in apps)
        {
            sb.AppendLine($"\"{EscapeCsv(a.Name)}\";\"{EscapeCsv(a.Publisher)}\";\"{EscapeCsv(a.Version)}\";\"{EscapeCsv(a.DisplayDate)}\";\"{EscapeCsv(a.FormattedSize)}\";\"{EscapeCsv(a.TypeBadge)}\";\"{EscapeCsv(a.InstallLocation)}\";\"{EscapeCsv(a.UninstallString)}\"");
        }
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public static void ExportToTxt(IEnumerable<InstalledProgram> apps, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RODRIGOTECH REVOCLEAN — LISTA DE PROGRAMAS INSTALADOS ({DateTime.Now:dd/MM/yyyy HH:mm})");
        sb.AppendLine(new string('=', 90));
        sb.AppendLine();
        foreach (var a in apps)
        {
            sb.AppendLine($"[ {a.Name} ]");
            if (!string.IsNullOrEmpty(a.Publisher)) sb.AppendLine($"  Publicador: {a.Publisher}");
            if (!string.IsNullOrEmpty(a.Version)) sb.AppendLine($"  Versão:     {a.Version}");
            if (!string.IsNullOrEmpty(a.DisplayDate)) sb.AppendLine($"  Data:       {a.DisplayDate}");
            if (a.EstimatedSizeBytes > 0) sb.AppendLine($"  Tamanho:    {a.FormattedSize}");
            sb.AppendLine($"  Tipo:       {a.TypeBadge}");
            if (!string.IsNullOrEmpty(a.InstallLocation)) sb.AppendLine($"  Local:      {a.InstallLocation}");
            if (!string.IsNullOrEmpty(a.UninstallString)) sb.AppendLine($"  Comando:    {a.UninstallString}");
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string s) => s?.Replace("\"", "\"\"") ?? "";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
