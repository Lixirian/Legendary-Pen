using System.Windows;
using Forms = System.Windows.Forms;

namespace LegendaryPen.App;

/// <summary>Icône de la zone de notification et son menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private Action? _notificationClicked;

    public TrayIcon(Action toggleToolbar, Action capture, Action settings, Action quit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Afficher ou masquer la barre", null, (_, _) => toggleToolbar());
        menu.Items.Add("Capture d'écran", null, (_, _) => capture());
        menu.Items.Add("Réglages", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => quit());

        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/legendary-pen.ico"));

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(resource.Stream, Forms.SystemInformation.SmallIconSize),
            Text = "Legendary Pen",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                toggleToolbar();
            }
        };
        _icon.BalloonTipClicked += (_, _) => _notificationClicked?.Invoke();
    }

    public void Notify(string message, Action? clicked = null)
    {
        _notificationClicked = clicked;
        _icon.ShowBalloonTip(3000, "Legendary Pen", message, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        // Sans cela, l'icône resterait affichée jusqu'au survol de la zone de notification.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
