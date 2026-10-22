using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Utilitários do sistema: Lixeira nativa, backup e remoção de chaves do Registro, ponto de restauração e navegação.
/// </summary>
public static class RevoCleanSystem
{
    public static string BackupFolder
    {
        get
        {
            string baseFolder = AppSettings.Folder;
            string bkp = Path.Combine(baseFolder, "Backups");
            if (!Directory.Exists(bkp)) Directory.CreateDirectory(bkp);
            return bkp;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>Move arquivo ou pasta com segurança para a Lixeira do Windows (recuperável pelo usuário).</summary>
    public static bool SendToRecycleBin(string path)
    {
        if (!RevoCleanEngine.IsPathSafeToDelete(path)) return false;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        try
        {
            string absPath = Path.GetFullPath(path);
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = absPath + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };

            int ret = SHFileOperationW(ref op);
            return ret == 0 && !File.Exists(absPath) && !Directory.Exists(absPath);
        }
        catch { return false; }
    }

    /// <summary>Gera backup em arquivo .reg antes de apagar qualquer chave ou valor do Registro.</summary>
    public static string ExportRegistryBackup(string rootName, string subkeyPath, string appName)
    {
        try
        {
            string safeApp = Regex.Replace(appName, @"[^\w\- ]", "").Trim();
            if (string.IsNullOrEmpty(safeApp)) safeApp = "Programa";
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{safeApp}_{timestamp}_{Guid.NewGuid().ToString("N")[..6]}.reg";
            string filePath = Path.Combine(BackupFolder, fileName);

            string fullKey = $"{rootName}\\{subkeyPath}";
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{fullKey}\" \"{filePath}\" /y",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var p = Process.Start(psi);
            p?.WaitForExit(5000);

            if (File.Exists(filePath)) return filePath;
        }
        catch { }
        return "";
    }

    /// <summary>Remove recursivamente uma chave do Registro do Windows com verificação de segurança.</summary>
    public static bool DeleteRegistryKey(string rootName, string subkeyPath)
    {
        if (!RevoCleanEngine.IsRegistryKeySafeToDelete(subkeyPath)) return false;

        var hive = rootName.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.CurrentUser
            : RegistryHive.LocalMachine;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            baseKey.DeleteSubKeyTree(subkeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            try
            {
                using var baseKey32 = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32);
                baseKey32.DeleteSubKeyTree(subkeyPath, throwOnMissingSubKey: false);
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Remove um valor específico dentro de uma chave do Registro.</summary>
    public static bool DeleteRegistryValue(string rootName, string subkeyPath, string valueName)
    {
        if (!RevoCleanEngine.IsRegistryKeySafeToDelete(subkeyPath)) return false;

        var hive = rootName.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            ? RegistryHive.CurrentUser
            : RegistryHive.LocalMachine;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var sub = baseKey.OpenSubKey(subkeyPath, writable: true);
            if (sub != null)
            {
                sub.DeleteValue(valueName, throwOnMissingValue: false);
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Cria Ponto de Restauração do Windows via PowerShell.</summary>
    public static async Task<(bool Success, string Message)> CreateRestorePointAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                string desc = description.Replace("'", " ");
                string psCmd = $"Checkpoint-Computer -Description '{desc}' -RestorePointType 'APPLICATION_UNINSTALL'";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCmd}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var p = Process.Start(psi);
                if (p == null) return (false, "Falha ao iniciar PowerShell.");

                p.WaitForExit(60000);
                if (p.ExitCode == 0)
                    return (true, "Ponto de restauração criado com sucesso!");

                string err = p.StandardError.ReadToEnd();
                return (false, string.IsNullOrWhiteSpace(err) ? "Não foi possível criar o ponto de restauração." : err.Trim());
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    /// <summary>Abre o Windows Explorer na pasta ou seleciona o arquivo.</summary>
    public static void OpenInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    /// <summary>Abre o Editor de Registro na chave especificada.</summary>
    public static void OpenInRegedit(string rootName, string subkeyPath)
    {
        try
        {
            string fullKey = $@"Computer\{rootName}\{subkeyPath}";
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", writable: true))
            {
                k?.SetValue("LastKey", fullKey, RegistryValueKind.String);
            }
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch { }
    }
}
