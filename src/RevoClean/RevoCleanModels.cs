using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ResetExplode;

/// <summary>Modo de varredura profunda de sobras residuais.</summary>
public enum RevoScanMode
{
    /// <summary>Apenas pasta de instalação e chaves oficiais de desinstalação.</summary>
    Safe = 1,
    /// <summary>Modo seguro + pastas de dados do usuário (AppData, ProgramData) e atalhos.</summary>
    Moderate = 2,
    /// <summary>Varredura profunda completa no Registro, inicialização e diretórios do sistema.</summary>
    Advanced = 3
}

/// <summary>Aplicativo instalado no Windows (Win32 clássico ou pacote AppX/Loja Microsoft).</summary>
public sealed class InstalledProgram : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallDate { get; set; } = "";
    public string DisplayDate { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string QuietUninstallString { get; set; } = "";
    public string DisplayIcon { get; set; } = "";
    public long EstimatedSizeBytes { get; set; }
    public string FormattedSize { get; set; } = "Desconhecido";
    public bool IsSystemComponent { get; set; }
    public bool IsWindowsInstaller { get; set; }
    public string RegistryRoot { get; set; } = "";       // "HKLM", "HKCU" ou "AppX"
    public string RegistryPath { get; set; } = "";
    public string KeyName { get; set; } = "";
    public string AppType { get; set; } = "win32";       // "win32" ou "store"
    public string PackageFamilyName { get; set; } = "";

    public string TypeBadge => AppType == "store" ? "Loja Windows" : "Desktop";
    public bool IsStoreApp => AppType == "store";

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Item residual encontrado no Registro do Windows.</summary>
public sealed class RegistryLeftover : INotifyPropertyChanged
{
    public string RootName { get; init; } = "";          // "HKCU" ou "HKLM"
    public string KeyPath { get; init; } = "";           // ex: "Software\MyApp"
    public string? ValueName { get; init; }              // null se for a chave inteira
    public string? ValueData { get; init; }

    public string ItemType => string.IsNullOrEmpty(ValueName) ? "Chave" : "Valor";

    public string DisplayPath => string.IsNullOrEmpty(ValueName)
        ? $"{RootName}\\{KeyPath}"
        : $"{RootName}\\{KeyPath} -> [{ValueName}]";

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Arquivo, diretório ou atalho residual encontrado no sistema de arquivos.</summary>
public sealed class FileLeftover : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string ItemType { get; init; } = "Pasta";     // "Pasta de Instalação", "Pasta Residual", "Atalho", "Arquivo"
    public long SizeBytes { get; init; }
    public string FormattedSize { get; init; } = "";

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set { _selected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
