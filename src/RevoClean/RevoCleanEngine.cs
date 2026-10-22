using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Motor de busca profunda de sobras residuais (chaves de registro, pastas órfãs, dados de usuário e atalhos).
/// </summary>
public sealed class RevoCleanEngine
{
    private static readonly HashSet<string> GenericExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "software", "app", "application", "setup", "installer", "install",
        "uninstall", "windows", "microsoft", "system", "program", "files",
        "x86", "x64", "64-bit", "32-bit", "free", "version", "update",
        "updater", "service", "client", "player", "tools", "util", "utility",
        "edition", "release", "patch", "portable", "desktop", "suite", "the",
        "and", "for", "pro", "plus", "standard", "community",
        "llc", "inc", "corp", "corporation", "ltd", "limited", "gmbh",
        "sarl", "co", "holding", "holdings", "group", "technologies"
    };

    private static readonly HashSet<string> SystemProtectedDirs;
    private static readonly HashSet<string> ProtectedRegistryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"software\microsoft\windows",
        @"software\microsoft\windows nt",
        @"software\microsoft\windows defender",
        @"software\microsoft\directx",
        @"software\microsoft\ole",
        @"software\microsoft\rpc",
        @"software\classes",
        @"software\policies",
        @"system",
        @"sam",
        @"security",
        @"hardware"
    };

    static RevoCleanEngine()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(win))
        {
            dirs.Add(Normalize(win));
            dirs.Add(Normalize(Path.Combine(win, "System32")));
            dirs.Add(Normalize(Path.Combine(win, "SysWOW64")));
            dirs.Add(Normalize(Path.Combine(win, "WinSxS")));
        }

        AddSpecial(dirs, Environment.SpecialFolder.ProgramFiles);
        AddSpecial(dirs, Environment.SpecialFolder.ProgramFilesX86);
        AddSpecial(dirs, Environment.SpecialFolder.CommonApplicationData);
        AddSpecial(dirs, Environment.SpecialFolder.ApplicationData);
        AddSpecial(dirs, Environment.SpecialFolder.LocalApplicationData);
        AddSpecial(dirs, Environment.SpecialFolder.UserProfile);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try { dirs.Add(Normalize(drive.RootDirectory.FullName)); } catch { }
        }

        SystemProtectedDirs = dirs;
    }

    private static void AddSpecial(HashSet<string> set, Environment.SpecialFolder folder)
    {
        try
        {
            string p = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(p)) set.Add(Normalize(p));
        }
        catch { }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return path.TrimEnd('\\').ToLowerInvariant(); }
    }

    public InstalledProgram App { get; }
    public RevoScanMode Mode { get; }

    public List<string> AppTokens { get; }
    public List<string> PubTokens { get; }
    public List<string> SearchTokens { get; }

    public RevoCleanEngine(InstalledProgram app, RevoScanMode mode = RevoScanMode.Moderate)
    {
        App = app;
        Mode = mode;
        (AppTokens, PubTokens) = ExtractKeywords(app);
        var combined = new List<string>(AppTokens);
        foreach (var p in PubTokens)
        {
            if (!combined.Contains(p, StringComparer.OrdinalIgnoreCase))
                combined.Add(p);
        }
        SearchTokens = combined;
    }

    private static (List<string> AppTokens, List<string> PubTokens) ExtractKeywords(InstalledProgram app)
    {
        var appTokens = new List<string>();
        string cleanApp = Regex.Replace(app.Name, @"\(.*?\)|\[.*?\]", "").Trim();
        if (cleanApp.Length >= 3 && !GenericExclusions.Contains(cleanApp))
            appTokens.Add(cleanApp.ToLowerInvariant());

        foreach (Match m in Regex.Matches(app.Name, @"[A-Za-z0-9_\-\.]+"))
        {
            string w = m.Value.Trim().ToLowerInvariant();
            if (w.Length >= 3 && !GenericExclusions.Contains(w) && !Regex.IsMatch(w, @"^\d+(\.\d+)*$"))
            {
                if (!appTokens.Contains(w, StringComparer.OrdinalIgnoreCase))
                    appTokens.Add(w);
            }
        }

        var pubTokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(app.Publisher))
        {
            foreach (Match m in Regex.Matches(app.Publisher, @"[A-Za-z0-9_\-\.]+"))
            {
                string w = m.Value.Trim().ToLowerInvariant();
                if (w.Length >= 3 && !GenericExclusions.Contains(w) && !Regex.IsMatch(w, @"^\d+(\.\d+)*$"))
                {
                    if (!pubTokens.Contains(w, StringComparer.OrdinalIgnoreCase))
                        pubTokens.Add(w);
                }
            }
        }

        return (appTokens, pubTokens);
    }

    public static bool IsPathSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string norm = Normalize(path);
        if (string.IsNullOrEmpty(norm)) return false;

        if (SystemProtectedDirs.Contains(norm)) return false;

        // Raiz de unidade (ex: c:, d:)
        if (norm.Length <= 3 && norm.EndsWith(':')) return false;

        string win = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!string.IsNullOrEmpty(win) && norm.StartsWith(win, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static bool IsRegistryKeySafeToDelete(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return false;
        string clean = keyPath.Trim('\\').ToLowerInvariant();
        foreach (var p in ProtectedRegistryKeys)
        {
            if (clean == p || clean.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public (List<RegistryLeftover> Registry, List<FileLeftover> Files) ScanAll(Action<string, double>? progress = null)
    {
        progress?.Invoke("Varrendo sobras no Registro do Windows...", 0.1);
        var reg = ScanRegistry();

        progress?.Invoke("Varrendo arquivos, pastas e atalhos órfãos...", 0.6);
        var files = ScanFiles();

        progress?.Invoke("Varredura concluída com sucesso!", 1.0);
        return (reg, files);
    }

    public List<RegistryLeftover> ScanRegistry()
    {
        var leftovers = new List<RegistryLeftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddReg(string rootName, string subkeyPath, string? valueName = null, string? valData = null)
        {
            string id = $"{rootName}\\{subkeyPath}\\{valueName ?? ""}".ToLowerInvariant();
            if (!seen.Add(id)) return;
            if (!IsRegistryKeySafeToDelete(subkeyPath)) return;

            // Se for chave com nome do publicador (ex: Software\Google) e contiver outros produtos, não remove a chave-mãe
            string[] parts = subkeyPath.Trim('\\').Split('\\');
            string last = parts[^1].ToLowerInvariant();
            if (PubTokens.Contains(last, StringComparer.OrdinalIgnoreCase))
            {
                var hive = rootName == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                try
                {
                    using var baseK = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var testK = baseK.OpenSubKey(subkeyPath);
                    if (testK != null && testK.SubKeyCount > 0)
                    {
                        bool hasOther = false;
                        foreach (var sk in testK.GetSubKeyNames())
                        {
                            string skLower = sk.ToLowerInvariant();
                            if (!AppTokens.Any(t => skLower.Contains(t) && t != last))
                            {
                                hasOther = true;
                                break;
                            }
                        }
                        if (hasOther) return;
                    }
                }
                catch { }
            }

            leftovers.Add(new RegistryLeftover
            {
                RootName = rootName,
                KeyPath = subkeyPath,
                ValueName = valueName,
                ValueData = valData
            });
        }

        // 1. Chave oficial de desinstalação
        if (!string.IsNullOrEmpty(App.RegistryPath) && (App.RegistryRoot == "HKCU" || App.RegistryRoot == "HKLM"))
        {
            AddReg(App.RegistryRoot, App.RegistryPath);
        }

        var roots = new (string RootName, RegistryHive Hive)[]
        {
            ("HKCU", RegistryHive.CurrentUser),
            ("HKLM", RegistryHive.LocalMachine)
        };

        // 2. Chaves diretas: Software\<App>, Software\<Publisher>\<App>, Software\WOW6432Node\<App>
        foreach (var (rootName, hive) in roots)
        {
            var views = new List<RegistryView> { RegistryView.Default };
            if (hive == RegistryHive.LocalMachine) views.Add(RegistryView.Registry32);

            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var token in AppTokens)
                    {
                        var candidates = new List<string> { $@"Software\{token}" };
                        if (!string.IsNullOrEmpty(App.Publisher))
                            candidates.Add($@"Software\{App.Publisher}\{token}");

                        foreach (var pub in PubTokens)
                            candidates.Add($@"Software\{pub}\{token}");

                        if (hive == RegistryHive.LocalMachine)
                        {
                            candidates.Add($@"Software\WOW6432Node\{token}");
                            if (!string.IsNullOrEmpty(App.Publisher))
                                candidates.Add($@"Software\WOW6432Node\{App.Publisher}\{token}");
                        }

                        foreach (var cand in candidates)
                        {
                            try
                            {
                                using var test = baseKey.OpenSubKey(cand);
                                if (test != null) AddReg(rootName, cand);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // 3. Modo Moderado ou Avançado: Entradas de Run / RunOnce na Inicialização
        if (Mode >= RevoScanMode.Moderate)
        {
            string[] runPaths = { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" };
            foreach (var (rootName, hive) in roots)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    foreach (var rp in runPaths)
                    {
                        try
                        {
                            using var rk = baseKey.OpenSubKey(rp);
                            if (rk == null) continue;
                            foreach (var vn in rk.GetValueNames())
                            {
                                string valData = rk.GetValue(vn)?.ToString() ?? "";
                                string vnLower = vn.ToLowerInvariant();
                                string vdLower = valData.ToLowerInvariant();
                                if (SearchTokens.Any(t => vnLower.Contains(t) || vdLower.Contains(t)))
                                {
                                    AddReg(rootName, rp, vn, valData);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // 4. Modo Avançado: Varredura de subchaves sob Software\
        if (Mode >= RevoScanMode.Advanced)
        {
            foreach (var (rootName, hive) in roots)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    using var soft = baseKey.OpenSubKey("Software");
                    if (soft == null) continue;
                    foreach (var sub in soft.GetSubKeyNames())
                    {
                        string sLower = sub.ToLowerInvariant();
                        if (SearchTokens.Any(t => sLower == t || (t.Length > 4 && sLower.Contains(t))))
                        {
                            AddReg(rootName, $@"Software\{sub}");
                        }
                    }
                }
                catch { }
            }
        }

        return leftovers;
    }

    public List<FileLeftover> ScanFiles()
    {
        var leftovers = new List<FileLeftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddItem(string itemPath, string? itemType = null)
        {
            if (string.IsNullOrWhiteSpace(itemPath)) return;
            string norm = Normalize(itemPath);
            if (string.IsNullOrEmpty(norm) || !seen.Add(norm)) return;
            if (!IsPathSafeToDelete(norm)) return;

            bool isDir = Directory.Exists(norm);
            bool isFile = File.Exists(norm);
            if (!isDir && !isFile) return;

            // Proteção se for pasta mãe de publicador contendo outros produtos
            if (isDir && !string.IsNullOrEmpty(App.Publisher))
            {
                string dirName = Path.GetFileName(norm).ToLowerInvariant();
                if (PubTokens.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var subdirs = Directory.GetDirectories(norm);
                        bool hasOther = subdirs.Any(s =>
                        {
                            string sName = Path.GetFileName(s).ToLowerInvariant();
                            return !AppTokens.Any(t => sName.Contains(t));
                        });
                        if (hasOther) return;
                    }
                    catch { }
                }
            }

            long size = 0;
            try
            {
                if (isDir) size = RevoCleanScanner.GetDirectorySize(norm);
                else size = new FileInfo(norm).Length;
            }
            catch { }

            string detectedType = itemType ?? (isDir ? "Pasta Residual" : norm.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? "Atalho" : "Arquivo");

            leftovers.Add(new FileLeftover
            {
                Path = norm,
                ItemType = detectedType,
                SizeBytes = size,
                FormattedSize = RevoCleanScanner.FormatSize(size)
            });
        }

        // 1. Pasta de instalação original
        if (!string.IsNullOrEmpty(App.InstallLocation) && Directory.Exists(App.InstallLocation))
        {
            AddItem(App.InstallLocation, "Pasta de Instalação");
        }

        // 1.1 Pasta de pacote AppX (Loja)
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(App.PackageFamilyName) && !string.IsNullOrEmpty(localApp))
        {
            string appxData = Path.Combine(localApp, "Packages", App.PackageFamilyName);
            if (Directory.Exists(appxData))
                AddItem(appxData, "Dados do Aplicativo (Loja)");

            foreach (var tok in AppTokens)
            {
                string alias = Path.Combine(localApp, "Microsoft", "WindowsApps", $"{tok}.exe");
                if (File.Exists(alias))
                    AddItem(alias, "Atalho de Inicialização");
            }
        }

        // 2. Pastas comuns de dados e programas
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var searchBases = new List<string>();
        if (!string.IsNullOrEmpty(appData)) searchBases.Add(appData);
        if (!string.IsNullOrEmpty(localApp))
        {
            searchBases.Add(localApp);
            searchBases.Add(Path.Combine(localApp, "Programs"));
        }
        if (!string.IsNullOrEmpty(progData)) searchBases.Add(progData);
        if (!string.IsNullOrEmpty(progFiles)) searchBases.Add(progFiles);
        if (!string.IsNullOrEmpty(progFilesX86)) searchBases.Add(progFilesX86);

        if (Mode >= RevoScanMode.Moderate)
        {
            foreach (var b in searchBases)
            {
                if (!Directory.Exists(b)) continue;

                foreach (var tok in AppTokens)
                {
                    string cand1 = Path.Combine(b, tok);
                    if (Directory.Exists(cand1)) AddItem(cand1, "Pasta Residual");

                    if (!string.IsNullOrEmpty(App.Publisher))
                    {
                        string cand2 = Path.Combine(b, App.Publisher, tok);
                        if (Directory.Exists(cand2)) AddItem(cand2, "Pasta Residual");
                    }

                    foreach (var pub in PubTokens)
                    {
                        string cand3 = Path.Combine(b, pub, tok);
                        if (Directory.Exists(cand3)) AddItem(cand3, "Pasta Residual");
                    }

                    // Varre subdiretórios de 1º nível
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(b))
                        {
                            string sName = Path.GetFileName(sub).ToLowerInvariant();
                            if (sName == tok || (tok.Length > 4 && sName.Contains(tok)))
                                AddItem(sub, "Pasta Residual");
                        }
                    }
                    catch { }
                }
            }
        }

        // 3. Atalhos na Área de Trabalho e Menu Iniciar
        var shortcutDirs = new List<string>();
        if (!string.IsNullOrEmpty(userProf))
            shortcutDirs.Add(Path.Combine(userProf, "Desktop"));
        shortcutDirs.Add(@"C:\Users\Public\Desktop");
        if (!string.IsNullOrEmpty(appData))
            shortcutDirs.Add(Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs"));
        if (!string.IsNullOrEmpty(progData))
            shortcutDirs.Add(Path.Combine(progData, @"Microsoft\Windows\Start Menu\Programs"));

        foreach (var sdir in shortcutDirs)
        {
            if (!Directory.Exists(sdir)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(sdir, "*.lnk", SearchOption.AllDirectories))
                {
                    string fLower = Path.GetFileName(f).ToLowerInvariant();
                    if (SearchTokens.Any(t => fLower.Contains(t)))
                        AddItem(f, "Atalho");
                }

                foreach (var d in Directory.GetDirectories(sdir, "*", SearchOption.TopDirectoryOnly))
                {
                    string dLower = Path.GetFileName(d).ToLowerInvariant();
                    if (SearchTokens.Any(t => dLower.Contains(t)))
                        AddItem(d, "Pasta do Menu Iniciar");
                }
            }
            catch { }
        }

        leftovers.Sort((a, b) =>
        {
            int tA = a.ItemType.StartsWith("Pasta") ? 0 : 1;
            int tB = b.ItemType.StartsWith("Pasta") ? 0 : 1;
            if (tA != tB) return tA.CompareTo(tB);
            return b.SizeBytes.CompareTo(a.SizeBytes);
        });

        return leftovers;
    }
}
