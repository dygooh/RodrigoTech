using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ResetExplode;

public sealed class AppEntry : INotifyPropertyChanged
{
    private bool _isBlocked;
    private bool _hasTraffic;
    private ImageSource? _icon;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string Origin { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (Set(ref _isBlocked, value))
                OnPropertyChanged(nameof(NetworkTooltip));
        }
    }

    public bool HasTraffic
    {
        get => _hasTraffic;
        set
        {
            if (Set(ref _hasTraffic, value))
                OnPropertyChanged(nameof(TrafficText));
        }
    }

    public string TrafficText => HasTraffic ? "Ativo" : "—";
    public string NetworkTooltip => IsBlocked ? "Bloqueado — clique para permitir" : "Permitido — clique para bloquear";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    public void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PublisherGroup : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public required string Publisher { get; init; }
    public required List<AppEntry> Apps { get; init; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public int Count => Apps.Count;
    public bool AnyBlocked => Apps.Exists(a => a.IsBlocked);
    public bool AllBlocked => Apps.Count > 0 && Apps.TrueForAll(a => a.IsBlocked);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshNetworkState()
    {
        OnPropertyChanged(nameof(AnyBlocked));
        OnPropertyChanged(nameof(AllBlocked));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
