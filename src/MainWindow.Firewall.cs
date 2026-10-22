using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            UpdateServiceStatusUI();
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

    public void UpdateServiceStatusUI()
    {
        Dispatcher.Invoke(() =>
        {
            bool installed = ServiceSetup.IsInstalled();
            bool running = installed && ServiceSetup.IsRunning();

            string statusText = ServiceSetup.StatusText();
            FwEngine.Text = statusText;

            if (!installed)
            {
                TxtServiceBadge.Text = "NÃO INSTALADO";
                TxtServiceBadge.Foreground = (Brush)FindResource("Muted");
                BadgeServiceStatus.Background = (Brush)FindResource("Line");

                TxtStartupServiceBadge.Text = "NÃO INSTALADO";
                TxtStartupServiceBadge.Foreground = (Brush)FindResource("Muted");
                BadgeStartupService.Background = (Brush)FindResource("Line");

                BtnFwServiceToggle.Content = "INSTALAR SERVIÇO";
                BtnStartupServiceToggle.Content = "INSTALAR SERVIÇO";

                string desc = "Por padrão, o RodrigoTech roda de forma portátil sem instalar nada no sistema. Instale o serviço se desejar que a proteção de rede inicie com o Windows mesmo com o app fechado.";
                FwServiceDesc.Text = desc;
                StartupServiceDesc.Text = desc;
            }
            else if (running)
            {
                TxtServiceBadge.Text = "INSTALADO E ATIVO";
                TxtServiceBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71));
                BadgeServiceStatus.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x2E, 0xCC, 0x71));

                TxtStartupServiceBadge.Text = "INSTALADO E ATIVO";
                TxtStartupServiceBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0xCC, 0x71));
                BadgeStartupService.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x2E, 0xCC, 0x71));

                BtnFwServiceToggle.Content = "DESINSTALAR SERVIÇO";
                BtnStartupServiceToggle.Content = "DESINSTALAR SERVIÇO";

                string desc = "O serviço do Windows está configurado e protege as regras de rede automaticamente ao iniciar o sistema, mesmo com o aplicativo fechado.";
                FwServiceDesc.Text = desc;
                StartupServiceDesc.Text = desc;
            }
            else
            {
                TxtServiceBadge.Text = "INSTALADO (PARADO)";
                TxtServiceBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0x9C, 0x12));
                BadgeServiceStatus.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xF3, 0x9C, 0x12));

                TxtStartupServiceBadge.Text = "INSTALADO (PARADO)";
                TxtStartupServiceBadge.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0x9C, 0x12));
                BadgeStartupService.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xF3, 0x9C, 0x12));

                BtnFwServiceToggle.Content = "DESINSTALAR SERVIÇO";
                BtnStartupServiceToggle.Content = "DESINSTALAR SERVIÇO";

                string desc = "O serviço em segundo plano está registrado no Windows, porém encontra-se parado no momento.";
                FwServiceDesc.Text = desc;
                StartupServiceDesc.Text = desc;
            }
        });
    }

    private async void BtnFwServiceToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;

        bool isInstalled = ServiceSetup.IsInstalled();

        if (!isInstalled)
        {
            var result = MessageBox.Show(
                "Deseja instalar o RodrigoTech como serviço em segundo plano do Windows?\n\n" +
                "• Ele iniciará automaticamente com o Windows para manter as regras de rede ativas.\n" +
                "• Uma cópia será mantida em Arquivos de Programas.\n" +
                "• O ícone na bandeja ficará disponível ao lado do relógio.",
                "Instalar Serviço em Segundo Plano",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                FwLoading.Visibility = Visibility.Visible;
                await Task.Run(ServiceSetup.Install);
                UpdateServiceStatusUI();
                MessageBox.Show(
                    "Serviço em segundo plano instalado e iniciado com sucesso!",
                    "RodrigoTech",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Falha ao instalar o serviço:\n{ex.Message}",
                    "RodrigoTech",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                FwLoading.Visibility = Visibility.Collapsed;
                UpdateServiceStatusUI();
            }
        }
        else
        {
            var result = MessageBox.Show(
                "Deseja desinstalar o serviço em segundo plano do RodrigoTech?\n\n" +
                "• O serviço do Windows e a inicialização automática serão excluídos.\n" +
                "• O aplicativo voltará a operar exclusivamente no modo portátil.",
                "Desinstalar Serviço em Segundo Plano",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                FwLoading.Visibility = Visibility.Visible;
                await Task.Run(ServiceSetup.Uninstall);
                UpdateServiceStatusUI();
                MessageBox.Show(
                    "Serviço em segundo plano desinstalado com sucesso!\nO RodrigoTech agora opera no modo portátil.",
                    "RodrigoTech",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Falha ao desinstalar o serviço:\n{ex.Message}",
                    "RodrigoTech",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                FwLoading.Visibility = Visibility.Collapsed;
                UpdateServiceStatusUI();
            }
        }
    }
}
