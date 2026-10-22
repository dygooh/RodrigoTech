using System.Drawing;
using Forms = System.Windows.Forms;

namespace ResetExplode;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream
            ?? throw new InvalidOperationException("Ícone do RodrigoTech não encontrado.");
        _icon = new Icon(stream);
        stream.Dispose();

        _notify = new Forms.NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "RodrigoTech — proteção de rede ativa",
            ContextMenuStrip = BuildMenu()
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                App.CurrentApp.ShowMainWindow();
        };
    }

    public void ShowBalloon(string text)
    {
        _notify.BalloonTipTitle = "RodrigoTech";
        _notify.BalloonTipText = text;
        _notify.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
        _icon.Dispose();
    }

    private static Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir RodrigoTech", null, (_, _) => App.CurrentApp.ShowMainWindow());
        menu.Items.Add("Regras de rede", null, (_, _) => App.CurrentApp.ShowFirewall());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => App.CurrentApp.ExitApp());
        return menu;
    }
}
