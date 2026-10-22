using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Aba FERRAMENTAS · Desinstalador (RevoClean): remoção profunda de programas Win32 e apps da Loja com varredura de sobras.
/// </summary>
public partial class MainWindow
{
    private List<InstalledProgram>? _allUninstallerApps;
    private List<InstalledProgram>? _filteredUninstallerApps;
    private string _uninstallerTypeFilter = "Todos";
    private bool _uninstallerBusy;

    // Estado do Assistente (Wizard)
    private InstalledProgram? _wizardApp;
    private RevoScanMode _wizardScanMode = RevoScanMode.Moderate;
    private List<RegistryLeftover> _wizardRegLeftovers = new();
    private List<FileLeftover> _wizardFileLeftovers = new();
    private int _wizardStep = 1;
    private int _wizardDeletedRegCount;
    private int _wizardDeletedFilesCount;
    private long _wizardFreedBytes;

    private async void InitUninstaller()
    {
        if (_allUninstallerApps == null && !_uninstallerBusy)
        {
            await RefreshUninstallerAsync();
        }
    }

    private async Task RefreshUninstallerAsync()
    {
        if (_uninstallerBusy) return;
        _uninstallerBusy = true;
        BtnUninstallerRefresh.IsEnabled = false;
        UninstallerStatus.Text = "Carregando programas instalados e aplicativos da Loja Windows…";

        try
        {
            var apps = await RevoCleanScanner.ScanAllAsync();
            _allUninstallerApps = apps;

            // Extrai ícones em segundo plano para não travar a UI
            _ = Task.Run(() =>
            {
                foreach (var a in apps)
                {
                    if (string.IsNullOrEmpty(a.DisplayIcon) && string.IsNullOrEmpty(a.InstallLocation)) continue;
                    try
                    {
                        var icon = RevoCleanScanner.ExtractAppIcon(!string.IsNullOrEmpty(a.DisplayIcon) ? a.DisplayIcon : a.InstallLocation);
                        if (icon != null)
                        {
                            Dispatcher.Invoke(() => a.Icon = icon);
                        }
                    }
                    catch { }
                }
            });

            ApplyUninstallerFilters();
        }
        catch (Exception ex)
        {
            UninstallerStatus.Text = $"Erro ao carregar programas: {ex.Message}";
        }
        finally
        {
            _uninstallerBusy = false;
            BtnUninstallerRefresh.IsEnabled = true;
        }
    }

    private void ApplyUninstallerFilters()
    {
        if (_allUninstallerApps == null) return;

        string q = UninstallerSearch.Text.Trim().ToLowerInvariant();
        var query = _allUninstallerApps.AsEnumerable();

        if (_uninstallerTypeFilter == "Desktop")
            query = query.Where(a => a.AppType == "win32");
        else if (_uninstallerTypeFilter == "Loja")
            query = query.Where(a => a.AppType == "store");

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(a =>
                a.Name.ToLowerInvariant().Contains(q) ||
                a.Publisher.ToLowerInvariant().Contains(q) ||
                a.InstallLocation.ToLowerInvariant().Contains(q));
        }

        _filteredUninstallerApps = query.ToList();
        UninstallerGrid.ItemsSource = _filteredUninstallerApps;

        int win32Count = _allUninstallerApps.Count(a => a.AppType == "win32");
        int storeCount = _allUninstallerApps.Count(a => a.AppType == "store");
        UninstallerStatus.Text = $"{_filteredUninstallerApps.Count} exibidos ({_allUninstallerApps.Count} no total: {win32Count} Desktop, {storeCount} Loja Windows).";

        UpdateFilterChips();
    }

    private void UpdateFilterChips()
    {
        ChipFilterAll.Background = _uninstallerTypeFilter == "Todos" ? (Brush)FindResource("Fg") : Brushes.Transparent;
        ChipFilterAll.Foreground = _uninstallerTypeFilter == "Todos" ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");

        ChipFilterWin32.Background = _uninstallerTypeFilter == "Desktop" ? (Brush)FindResource("Fg") : Brushes.Transparent;
        ChipFilterWin32.Foreground = _uninstallerTypeFilter == "Desktop" ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");

        ChipFilterStore.Background = _uninstallerTypeFilter == "Loja" ? (Brush)FindResource("Fg") : Brushes.Transparent;
        ChipFilterStore.Foreground = _uninstallerTypeFilter == "Loja" ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _uninstallerTypeFilter = tag;
            ApplyUninstallerFilters();
        }
    }

    private void UninstallerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        UninstallerSearchHint.Visibility = string.IsNullOrEmpty(UninstallerSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyUninstallerFilters();
    }

    private async void BtnUninstallerRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshUninstallerAsync();
    }

    private async void BtnUninstallerRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "Deseja criar um Ponto de Restauração do Windows agora?\nIsso permite reverter alterações no sistema caso necessário.",
            "Ponto de Restauração",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (r != MessageBoxResult.Yes) return;

        UninstallerStatus.Text = "Criando Ponto de Restauração do Windows… aguarde.";
        var (ok, msg) = await RevoCleanSystem.CreateRestorePointAsync("Ponto de Restauração - RodrigoTech RevoClean");
        UninstallerStatus.Text = msg;
        MessageBox.Show(msg, "Ponto de Restauração", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void BtnUninstallerExport_Click(object sender, RoutedEventArgs e)
    {
        if (_filteredUninstallerApps == null || _filteredUninstallerApps.Count == 0)
        {
            MessageBox.Show("Não há programas listados para exportar.", "Exportar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Arquivo CSV (*.csv)|*.csv|Arquivo de Texto (*.txt)|*.txt",
            FileName = $"Programas_Instalados_{DateTime.Now:yyyyMMdd}.csv",
            Title = "Exportar Lista de Programas"
        };

        if (sfd.ShowDialog() == true)
        {
            try
            {
                if (sfd.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    RevoCleanScanner.ExportToTxt(_filteredUninstallerApps, sfd.FileName);
                else
                    RevoCleanScanner.ExportToCsv(_filteredUninstallerApps, sfd.FileName);

                MessageBox.Show($"Lista exportada com sucesso em:\n{sfd.FileName}", "Exportar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao exportar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void UninstallerGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app)
        {
            BtnUninstallSelected.IsEnabled = true;
        }
    }

    private void UninstallerGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app)
        {
            StartUninstallWizard(app);
        }
    }

    private void BtnUninstallSelected_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app)
        {
            StartUninstallWizard(app);
        }
        else
        {
            MessageBox.Show("Selecione um programa na lista para desinstalar.", "Desinstalador", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnRowUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledProgram app })
        {
            StartUninstallWizard(app);
        }
    }

    // Context Menu
    private void MenuUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app)
            StartUninstallWizard(app);
    }

    private void MenuOpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app && !string.IsNullOrEmpty(app.InstallLocation))
            RevoCleanSystem.OpenInExplorer(app.InstallLocation);
    }

    private void MenuOpenRegedit_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app && !string.IsNullOrEmpty(app.RegistryPath) && app.RegistryRoot != "AppX")
            RevoCleanSystem.OpenInRegedit(app.RegistryRoot, app.RegistryPath);
    }

    private void MenuCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app)
            Clipboard.SetText(app.Name);
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (UninstallerGrid.SelectedItem is InstalledProgram app)
            Clipboard.SetText(app.InstallLocation);
    }

    // =========================================================================
    // ASSISTENTE DE DESINSTALAÇÃO (WIZARD)
    // =========================================================================

    private void StartUninstallWizard(InstalledProgram app)
    {
        _wizardApp = app;
        _wizardStep = 1;
        _wizardDeletedRegCount = 0;
        _wizardDeletedFilesCount = 0;
        _wizardFreedBytes = 0;
        _wizardRegLeftovers.Clear();
        _wizardFileLeftovers.Clear();

        WzAppName.Text = app.Name;
        WzAppBadge.Text = app.TypeBadge;
        WzAppPub.Text = string.IsNullOrEmpty(app.Publisher) ? "Desconhecido" : app.Publisher;
        WzAppVer.Text = string.IsNullOrEmpty(app.Version) ? "N/A" : app.Version;
        WzAppSize.Text = app.FormattedSize;
        WzAppLoc.Text = string.IsNullOrEmpty(app.InstallLocation) ? "Registro do Windows" : app.InstallLocation;

        WzChkRestorePoint.IsChecked = true;
        WzChkQuiet.IsChecked = false;
        WzRadModerate.IsChecked = true;

        ShowWizardStep(1);
        UninstallerWizardModal.Visibility = Visibility.Visible;
    }

    private void ShowWizardStep(int step)
    {
        _wizardStep = step;
        WzStep1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        WzStep2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        WzStep3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        WzStep4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        WzStep5.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;

        switch (step)
        {
            case 1:
                WzTitle.Text = "ASSISTENTE DE DESINSTALAÇÃO";
                WzSubtitle.Text = $"Etapa 1 de 4: Opções de análise · {_wizardApp?.Name}";
                BtnWzCancel.Content = "CANCELAR";
                BtnWzNext.Content = "INICIAR DESINSTALAÇÃO ➔";
                BtnWzNext.Visibility = Visibility.Visible;
                BtnWzNext.IsEnabled = true;
                break;

            case 2:
                WzTitle.Text = "DESINSTALADOR OFICIAL";
                WzSubtitle.Text = $"Etapa 2 de 4: Remoção padrão · {_wizardApp?.Name}";
                BtnWzCancel.Content = "CANCELAR";
                BtnWzNext.Content = "AVANÇAR PARA VARREDURA DE SOBRAS ➔";
                BtnWzNext.Visibility = Visibility.Visible;
                BtnWzNext.IsEnabled = true;
                break;

            case 3:
                WzTitle.Text = "VARREDURA PROFUNDA (REVOCLEAN)";
                WzSubtitle.Text = $"Etapa 3 de 4: Analisando resíduos · {_wizardApp?.Name}";
                BtnWzCancel.Content = "CANCELAR";
                BtnWzNext.Visibility = Visibility.Collapsed;
                break;

            case 4:
                WzTitle.Text = "REVISÃO E LIMPEZA DE SOBRAS";
                WzSubtitle.Text = $"Etapa 4 de 4: Sobras encontradas · {_wizardApp?.Name}";
                BtnWzCancel.Content = "PULAR LIMPEZA";
                BtnWzNext.Content = "🗑️ EXCLUIR SELECIONADOS";
                BtnWzNext.Visibility = Visibility.Visible;
                BtnWzNext.IsEnabled = true;
                break;

            case 5:
                WzTitle.Text = "LIMPEZA CONCLUÍDA";
                WzSubtitle.Text = $"Desinstalação e limpeza finalizadas · {_wizardApp?.Name}";
                BtnWzCancel.Visibility = Visibility.Collapsed;
                BtnWzNext.Content = "CONCLUIR E FECHAR";
                BtnWzNext.Visibility = Visibility.Visible;
                BtnWzNext.IsEnabled = true;
                break;
        }
    }

    private async void BtnWizardNext_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardApp == null) return;

        if (_wizardStep == 1)
        {
            // Determina modo de varredura
            _wizardScanMode = WzRadSafe.IsChecked == true ? RevoScanMode.Safe
                : WzRadAdvanced.IsChecked == true ? RevoScanMode.Advanced
                : RevoScanMode.Moderate;

            bool createRestore = WzChkRestorePoint.IsChecked == true;
            bool quiet = WzChkQuiet.IsChecked == true;

            ShowWizardStep(2);

            // 1. Ponto de restauração opcional
            if (createRestore)
            {
                WzStep2Status.Text = "Criando Ponto de Restauração do Windows antes de desinstalar…";
                await RevoCleanSystem.CreateRestorePointAsync($"Desinstalação RevoClean: {_wizardApp.Name}");
            }

            // 2. Dispara desinstalador oficial
            WzStep2Status.Text = "Executando o desinstalador integrado… conclua a remoção na janela do programa.";
            var runner = new RevoCleanRunner(_wizardApp, quiet);
            _ = Task.Run(async () =>
            {
                await runner.LaunchAsync(exitCode =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_wizardStep == 2)
                        {
                            WzStep2Status.Text = "Desinstalador finalizado. Clique abaixo para varrer as sobras residuais.";
                        }
                    });
                });
            });
        }
        else if (_wizardStep == 2)
        {
            // Avança para varredura profunda
            await StartDeepScanAsync();
        }
        else if (_wizardStep == 4)
        {
            // Executa exclusão dos selecionados
            ExecuteLeftoversDeletion();
        }
        else if (_wizardStep == 5)
        {
            // Concluir e fechar
            UninstallerWizardModal.Visibility = Visibility.Collapsed;
            await RefreshUninstallerAsync();
        }
    }

    private async Task StartDeepScanAsync()
    {
        if (_wizardApp == null) return;
        ShowWizardStep(3);

        var cleaner = new RevoCleanEngine(_wizardApp, _wizardScanMode);

        var (reg, files) = await Task.Run(() =>
        {
            return cleaner.ScanAll((msg, pct) =>
            {
                Dispatcher.Invoke(() =>
                {
                    WzScanStatus.Text = msg;
                    WzScanScale.ScaleX = pct;
                });
            });
        });

        _wizardRegLeftovers = reg;
        _wizardFileLeftovers = files;

        BtnWzTabReg.Content = $"Registro ({_wizardRegLeftovers.Count})";
        BtnWzTabFiles.Content = $"Arquivos e Pastas ({_wizardFileLeftovers.Count})";

        GridWzReg.ItemsSource = _wizardRegLeftovers;
        GridWzFiles.ItemsSource = _wizardFileLeftovers;

        WzTab_Click(BtnWzTabReg, null!);
        ShowWizardStep(4);
    }

    private void WzTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            bool isReg = tag == "Reg";
            WzTabRegView.Visibility = isReg ? Visibility.Visible : Visibility.Collapsed;
            WzTabFilesView.Visibility = isReg ? Visibility.Collapsed : Visibility.Visible;

            BtnWzTabReg.Background = isReg ? (Brush)FindResource("Fg") : Brushes.Transparent;
            BtnWzTabReg.Foreground = isReg ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");

            BtnWzTabFiles.Background = !isReg ? (Brush)FindResource("Fg") : Brushes.Transparent;
            BtnWzTabFiles.Foreground = !isReg ? (Brush)FindResource("Ink") : (Brush)FindResource("Fg");
        }
    }

    private void BtnWzSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (WzTabRegView.Visibility == Visibility.Visible)
        {
            foreach (var r in _wizardRegLeftovers) r.Selected = true;
            GridWzReg.Items.Refresh();
        }
        else
        {
            foreach (var f in _wizardFileLeftovers) f.Selected = true;
            GridWzFiles.Items.Refresh();
        }
    }

    private void BtnWzDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        if (WzTabRegView.Visibility == Visibility.Visible)
        {
            foreach (var r in _wizardRegLeftovers) r.Selected = false;
            GridWzReg.Items.Refresh();
        }
        else
        {
            foreach (var f in _wizardFileLeftovers) f.Selected = false;
            GridWzFiles.Items.Refresh();
        }
    }

    private void ExecuteLeftoversDeletion()
    {
        if (_wizardApp == null) return;

        var selReg = _wizardRegLeftovers.Where(r => r.Selected).ToList();
        var selFiles = _wizardFileLeftovers.Where(f => f.Selected).ToList();

        if (selReg.Count == 0 && selFiles.Count == 0)
        {
            MessageBox.Show("Nenhum item residual selecionado para exclusão.", "Limpeza", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Deseja excluir com segurança:\n\n" +
            $"• {selReg.Count} chave(s)/valor(es) do Registro (com backup automático .reg)\n" +
            $"• {selFiles.Count} arquivo(s)/pasta(s) residuais (enviados para a Lixeira)?",
            "Confirmar Limpeza de Sobras",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        // 1. Deleta registros selecionados com backup prévio
        foreach (var r in selReg)
        {
            RevoCleanSystem.ExportRegistryBackup(r.RootName, r.KeyPath, _wizardApp.Name);

            if (!string.IsNullOrEmpty(r.ValueName))
            {
                if (RevoCleanSystem.DeleteRegistryValue(r.RootName, r.KeyPath, r.ValueName))
                    _wizardDeletedRegCount++;
            }
            else
            {
                if (RevoCleanSystem.DeleteRegistryKey(r.RootName, r.KeyPath))
                    _wizardDeletedRegCount++;
            }
        }

        // 2. Move arquivos selecionados para a lixeira
        foreach (var f in selFiles)
        {
            if (RevoCleanSystem.SendToRecycleBin(f.Path))
            {
                _wizardDeletedFilesCount++;
                _wizardFreedBytes += f.SizeBytes;
            }
        }

        WzFinalStats.Text =
            $"• Chaves e valores de registro removidos: {_wizardDeletedRegCount}\n" +
            $"• Arquivos e pastas residuais limpos: {_wizardDeletedFilesCount}\n" +
            $"• Espaço em disco liberado: {RevoCleanScanner.FormatSize(_wizardFreedBytes)}";

        WzBackupPath.Text = RevoCleanSystem.BackupFolder;

        ShowWizardStep(5);
    }

    private void BtnWzOpenBackups_Click(object sender, RoutedEventArgs e)
    {
        RevoCleanSystem.OpenInExplorer(RevoCleanSystem.BackupFolder);
    }

    private void BtnWizardCancel_Click(object sender, RoutedEventArgs e)
    {
        UninstallerWizardModal.Visibility = Visibility.Collapsed;
    }

    // =========================================================================
    // DESINSTALAÇÃO FORÇADA
    // =========================================================================

    private void BtnForcedUninstall_Click(object sender, RoutedEventArgs e)
    {
        ForcedNameInput.Text = "";
        ForcedPathInput.Text = "";
        ForcedRadModerate.IsChecked = true;
        ForcedUninstallModal.Visibility = Visibility.Visible;
    }

    private void BtnForcedBrowse_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar Executável ou Arquivo do Programa",
            Filter = "Executáveis (*.exe)|*.exe|Todos os Arquivos (*.*)|*.*"
        };

        if (ofd.ShowDialog() == true)
        {
            ForcedPathInput.Text = ofd.FileName;
            if (string.IsNullOrEmpty(ForcedNameInput.Text))
            {
                ForcedNameInput.Text = Path.GetFileNameWithoutExtension(ofd.FileName);
            }
        }
    }

    private async void BtnForcedStart_Click(object sender, RoutedEventArgs e)
    {
        string name = ForcedNameInput.Text.Trim();
        string targetPath = ForcedPathInput.Text.Trim();

        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(targetPath))
        {
            name = Path.GetFileNameWithoutExtension(targetPath);
        }

        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Informe pelo menos o nome do programa ou selecione um executável/pasta.", "Desinstalação Forçada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ForcedUninstallModal.Visibility = Visibility.Collapsed;

        var syntheticApp = new InstalledProgram
        {
            Name = name,
            Publisher = "",
            InstallLocation = targetPath,
            AppType = "win32"
        };

        _wizardApp = syntheticApp;
        _wizardScanMode = ForcedRadSafe.IsChecked == true ? RevoScanMode.Safe
            : ForcedRadAdvanced.IsChecked == true ? RevoScanMode.Advanced
            : RevoScanMode.Moderate;

        _wizardDeletedRegCount = 0;
        _wizardDeletedFilesCount = 0;
        _wizardFreedBytes = 0;

        UninstallerWizardModal.Visibility = Visibility.Visible;
        await StartDeepScanAsync();
    }

    private void BtnForcedCancel_Click(object sender, RoutedEventArgs e)
    {
        ForcedUninstallModal.Visibility = Visibility.Collapsed;
    }
}
