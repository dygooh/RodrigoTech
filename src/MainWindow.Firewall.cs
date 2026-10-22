using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ResetExplode;

public partial class MainWindow
{
    private WfpService? _wfp;
    private readonly ObservableCollection<PublisherGroup> _fwGroups = [];
    private DispatcherTimer? _fwTrafficTimer;
    private List<AppEntry> _fwApps = [];
    private bool _fwBusy;
    private bool _fwLoaded;

    public void OpenFirewallPage()
    {
        NavFirewall.IsChecked = true;
    }

    private void InitFirewall()
    {
        if (_fwLoaded) return;
        _fwLoaded = true;
        FwGroupList.ItemsSource = _fwGroups;
        _wfp = new WfpService();
        _fwTrafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _fwTrafficTimer.Tick += (_, _) => RefreshFwTraffic();
        Closed += (_, _) =>
        {
            _fwTrafficTimer?.Stop();
            _wfp?.Dispose();
        };
        _ = ReloadFirewallAsync();
    }

    private async Task ReloadFirewallAsync()
    {
        if (_fwBusy) return;
        _fwBusy = true;
        FwLoading.Visibility = Visibility.Visible;
        FwStatus.Text = "Carregando aplicativos...";
        FwKaspersky.Visibility = AdminService.IsKasperskyRunning() ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var apps = await Task.Run(AppDiscoveryService.Discover);
            HashSet<string> blocked;
            try
            {
                blocked = await Task.Run(() => _wfp!.GetBlockedPaths());
            }
            catch (Exception ex)
            {
                blocked = [];
                MessageBox.Show($"Não foi possível abrir o motor WFP.\n{ex.Message}", "RodrigoTech",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            foreach (var path in blocked)
            {
                if (apps.Exists(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                var extra = AppDiscoveryService.FromPath(path, "Bloqueado");
                if (extra is not null) apps.Add(extra);
            }

            foreach (var app in apps)
                app.IsBlocked = blocked.Contains(app.Path);

            _fwApps = apps;
            ApplyFwFilter();
            RefreshFwTraffic();
            _fwTrafficTimer?.Start();
            FwStatus.Text = $"{_fwApps.Count} aplicativos · {_fwApps.Count(a => a.IsBlocked)} bloqueados";
            FwEngine.Text = ServiceSetup.StatusText();
        }
        catch (Exception ex)
        {
            FwStatus.Text = ex.Message;
        }
        finally
        {
            FwLoading.Visibility = Visibility.Collapsed;
            _fwBusy = false;
        }
    }

    private void ApplyFwFilter()
    {
        var query = FwSearch.Text.Trim();
        FwSearchHint.Visibility = string.IsNullOrEmpty(FwSearch.Text) ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<AppEntry> source = _fwApps;
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = _fwApps.Where(a =>
                a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                a.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                a.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        _fwGroups.Clear();
        foreach (var group in source.GroupBy(a => a.Publisher, StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            _fwGroups.Add(new PublisherGroup
            {
                Publisher = group.Key,
                Apps = [.. group],
                IsExpanded = true
            });
        }
    }

    private void RefreshFwTraffic()
    {
        try
        {
            var active = ConnectionService.GetActiveExePaths();
            foreach (var app in _fwApps)
                app.HasTraffic = active.Contains(app.Path);
        }
        catch
        {
            // ignore
        }
    }

    private async void FwApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppEntry app }) return;
        await ToggleFwAsync(app);
    }

    private async void FwGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PublisherGroup group }) return;
        var block = !group.AllBlocked;
        foreach (var app in group.Apps.ToArray())
        {
            if (app.IsBlocked == block) continue;
            await ToggleFwAsync(app);
        }
        group.RefreshNetworkState();
    }

    private void FwGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PublisherGroup group }) return;
        group.IsExpanded = !group.IsExpanded;
    }

    private async Task ToggleFwAsync(AppEntry app)
    {
        if (_wfp is null) return;
        try
        {
            if (app.IsBlocked)
            {
                await Task.Run(() => _wfp.Unblock(app.Path));
                app.IsBlocked = false;
            }
            else
            {
                await Task.Run(() => _wfp.Block(app.Path));
                app.IsBlocked = true;
            }

            if (app.IsBlocked)
                ConnectionService.DropTcpConnections(app.Path);

            FwStatus.Text = $"{_fwApps.Count} aplicativos · {_fwApps.Count(a => a.IsBlocked)} bloqueados";
            foreach (var group in _fwGroups)
                group.RefreshNetworkState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Falha ao aplicar regra", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FwAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Aplicativos (*.exe)|*.exe",
            Title = "Escolher aplicativo para controlar"
        };
        if (dialog.ShowDialog() != true) return;

        var entry = AppDiscoveryService.FromPath(dialog.FileName, "Manual");
        if (entry is null)
        {
            MessageBox.Show("Não foi possível adicionar este executável.", "RodrigoTech",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_fwApps.Exists(a => a.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)))
            _fwApps.Add(entry);
        ApplyFwFilter();
        await ToggleFwAsync(entry);
    }

    private async void FwUnblockAll_Click(object sender, RoutedEventArgs e)
    {
        if (_wfp is null) return;
        try
        {
            await Task.Run(_wfp.UnblockAll);
            foreach (var app in _fwApps)
                app.IsBlocked = false;
            foreach (var group in _fwGroups)
                group.RefreshNetworkState();
            FwStatus.Text = $"{_fwApps.Count} aplicativos · 0 bloqueados";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "RodrigoTech", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FwRefresh_Click(object sender, RoutedEventArgs e) => await ReloadFirewallAsync();

    private void FwSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFwFilter();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (PageFirewall.Visibility != Visibility.Visible) return;
        FwSearch.Focus();
        FwSearch.SelectAll();
        e.Handled = true;
    }
}
