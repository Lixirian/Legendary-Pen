using System.Windows;
using LegendaryPen.Core;
using Forms = System.Windows.Forms;

namespace LegendaryPen.App;

/// <summary>Icône de la zone de notification et son menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly NotificationClicks _clicks = new();

    public TrayIcon(Action toggleToolbar, Action capture, Action record, Func<(string Text, bool Enabled)> recordState, Action signPdf, PresentationState presentation, Action settings, Action quit)
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            ShowCheckMargin = true,
            BackColor = DarkMenuColors.Surface,
            ForeColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Segoe UI", 10f),
            ShowItemToolTips = true,
        };
        menu.Items.Add(Loc.T("Afficher ou masquer la barre"), null, (_, _) => toggleToolbar());
        menu.Items.Add(Loc.T("Capture d'écran"), null, (_, _) => capture());
        var recordItem = new Forms.ToolStripMenuItem(recordState().Text, null, (_, _) => record());
        menu.Items.Add(recordItem);
        menu.Items.Add(Loc.T("Signer un PDF"), null, (_, _) => signPdf());
        menu.Items.Add(new Forms.ToolStripSeparator { BackColor = DarkMenuColors.Surface, ForeColor = DarkMenuColors.Line });

        // Les cases reflètent l'état à l'ouverture : la barre ou un raccourci ont pu le changer entre-temps.
        var effects = new (string Text, Action Toggle, Func<bool> IsOn)[]
        {
            (Loc.T("Projecteur"), presentation.ToggleSpotlight, () => presentation.SpotlightOn),
            (Loc.T("Loupe"), presentation.ToggleMagnifier, () => presentation.MagnifierOn),
            (Loc.T("Effets de clic"), presentation.ToggleClickEffects, () => presentation.ClickEffectsOn),
        };
        var effectItems = new List<(Forms.ToolStripMenuItem Item, Func<bool> IsOn)>();

        foreach (var (text, toggle, isOn) in effects)
        {
            var item = new Forms.ToolStripMenuItem(text, null, (_, _) => toggle());
            effectItems.Add((item, isOn));
            menu.Items.Add(item);
        }

        menu.Opening += (_, _) =>
        {
            var (recordText, recordEnabled) = recordState();
            recordItem.Text = recordText;
            recordItem.Enabled = recordEnabled;
            recordItem.ToolTipText = recordEnabled ? string.Empty : recordText;

            foreach (var (item, isOn) in effectItems)
            {
                item.Checked = isOn();
            }
        };

        menu.Items.Add(new Forms.ToolStripSeparator { BackColor = DarkMenuColors.Surface, ForeColor = DarkMenuColors.Line });
        menu.Items.Add(Loc.T("Réglages"), null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator { BackColor = DarkMenuColors.Surface, ForeColor = DarkMenuColors.Line });
        menu.Items.Add(Loc.T("Quitter"), null, (_, _) => quit());

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
        _icon.BalloonTipClicked += (_, _) => _clicks.Action?.Invoke();
    }

    public void Notify(string message, Action? clicked = null)
    {
        _clicks.Shown(clicked, TimeSpan.FromMilliseconds(Environment.TickCount64));
        _icon.ShowBalloonTip(3000, "Legendary Pen", message, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        // Sans cela, l'icône resterait affichée jusqu'au survol de la zone de notification.
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Couleurs du menu de notification, reprises de la barre d'outils.</summary>
internal sealed class DarkMenuColors : Forms.ProfessionalColorTable
{
    public static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(32, 33, 36);
    public static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(70, 71, 75);

    private static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(229, 57, 53);

    public override System.Drawing.Color ToolStripDropDownBackground => Surface;

    public override System.Drawing.Color ImageMarginGradientBegin => Surface;

    public override System.Drawing.Color ImageMarginGradientMiddle => Surface;

    public override System.Drawing.Color ImageMarginGradientEnd => Surface;

    public override System.Drawing.Color MenuBorder => Line;

    public override System.Drawing.Color MenuItemBorder => Accent;

    public override System.Drawing.Color MenuItemSelected => Accent;

    public override System.Drawing.Color CheckBackground => Accent;

    public override System.Drawing.Color CheckSelectedBackground => Accent;

    public override System.Drawing.Color CheckPressedBackground => Accent;

    public override System.Drawing.Color SeparatorDark => Line;

    public override System.Drawing.Color SeparatorLight => Surface;
}
