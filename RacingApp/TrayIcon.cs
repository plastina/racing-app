using System.IO;
using Forms = System.Windows.Forms;

namespace RacingApp;

/// <summary>Racing Studio icon in the Windows notification area, with Open/Exit menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private Action? _messageClicked;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Racing Studio", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "app.ico")),
            Text = "Racing Studio",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
        _icon.BalloonTipClicked += (_, _) => _messageClicked?.Invoke();
    }

    public void ShowMessage(string text, Action? clicked = null)
    {
        _messageClicked = clicked;
        _icon.ShowBalloonTip(4000, "Racing Studio", text, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
