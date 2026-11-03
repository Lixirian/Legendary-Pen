using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using LegendaryPen.Core;

namespace LegendaryPen.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly Dictionary<HotkeyAction, TextBox> _hotkeyBoxes = [];

    private HotkeyMap _hotkeys;

    public SettingsWindow(AppSettings settings, bool startupEnabled, bool canChangeStartup)
    {
        _original = settings;
        _hotkeys = new HotkeyMap(settings.Hotkeys);

        InitializeComponent();

        StartupBox.IsChecked = startupEnabled;
        StartupBox.IsEnabled = canChangeStartup;
        StartupNote.Visibility = canChangeStartup ? Visibility.Collapsed : Visibility.Visible;

        CollapseSlider.Value = ToolbarDocking.CollapseDelay(settings.ToolbarCollapseSeconds).TotalSeconds;

        var fade = new FadeTiming(settings.FadeDelaySeconds, settings.FadeDurationSeconds);
        FadeDelaySlider.Value = fade.Delay.TotalSeconds;
        FadeDurationSlider.Value = fade.Duration.TotalSeconds;

        (settings.Language switch
        {
            AppLanguage.French => LanguageFrenchRadio,
            AppLanguage.English => LanguageEnglishRadio,
            _ => LanguageSystemRadio,
        }).IsChecked = true;

        WheelBox.IsChecked = settings.WheelChangesSize;
        PressureBox.IsChecked = settings.PressureEnabled;
        HighlightSnapBox.IsChecked = settings.HighlightSnapEnabled;
        SizePerToolBox.IsChecked = settings.SizePerToolEnabled;
        KeepAnnotationsBox.IsChecked = settings.KeepAnnotations;
        BoardScreenBox.IsChecked = settings.BoardOnToolbarScreenOnly;

        FolderBox.Text = string.IsNullOrWhiteSpace(settings.CaptureFolder) ? CaptureDelivery.DefaultFolder : settings.CaptureFolder;
        (settings.CaptureDestination switch
        {
            CaptureDestination.Clipboard => ClipboardRadio,
            CaptureDestination.Ask => AskRadio,
            _ => FolderRadio,
        }).IsChecked = true;

        CreateHotkeyRows();
        ShowHotkeys();
        UpdateSliderTexts();
    }

    public AppSettings? Result { get; private set; }

    public bool StartupEnabled => StartupBox.IsChecked == true;

    private void CreateHotkeyRows()
    {
        foreach (var definition in HotkeyCatalog.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 8, 2) };

            var clear = new Button { Content = Loc.T("Aucun"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            AutomationProperties.SetName(clear, Loc.T("Désactiver : {0}", definition.Label));
            clear.Click += (_, _) => SetHotkey(definition.Action, HotkeyBinding.None);
            DockPanel.SetDock(clear, Dock.Right);

            var box = new TextBox { Width = 160, IsReadOnly = true, Padding = new Thickness(6, 3, 6, 3), IsReadOnlyCaretVisible = false };
            AutomationProperties.SetName(box, definition.Label);
            box.PreviewKeyDown += (_, args) => OnHotkeyKey(definition.Action, args);
            DockPanel.SetDock(box, Dock.Right);

            var label = new TextBlock
            {
                Text = definition.IsGlobal ? Loc.T("{0} (toujours actif)", definition.Label) : definition.Label,
                VerticalAlignment = VerticalAlignment.Center,
            };

            row.Children.Add(clear);
            row.Children.Add(box);
            row.Children.Add(label);

            HotkeyRows.Children.Add(row);
            _hotkeyBoxes[definition.Action] = box;
        }
    }

    private void OnHotkeyKey(HotkeyAction action, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            args.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;

        // Tab seul doit continuer à passer au champ suivant.
        if (key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            return;
        }

        uint nativeModifiers = 0;
        nativeModifiers |= modifiers.HasFlag(ModifierKeys.Control) ? HotkeyBinding.Control : 0;
        nativeModifiers |= modifiers.HasFlag(ModifierKeys.Alt) ? HotkeyBinding.Alt : 0;
        nativeModifiers |= modifiers.HasFlag(ModifierKeys.Shift) ? HotkeyBinding.Shift : 0;
        nativeModifiers |= modifiers.HasFlag(ModifierKeys.Windows) ? HotkeyBinding.Windows : 0;

        args.Handled = true;
        SetHotkey(action, new HotkeyBinding(nativeModifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }

    private void SetHotkey(HotkeyAction action, HotkeyBinding binding)
    {
        _hotkeys = _hotkeys.With(action, binding);
        ShowHotkeys();
    }

    private void ShowHotkeys()
    {
        foreach (var (action, box) in _hotkeyBoxes)
        {
            box.Text = _hotkeys[action].Format();
        }

        var issues = _hotkeys.Validate();
        ErrorText.Text = string.Join(Environment.NewLine, issues);
        ErrorText.Visibility = issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = issues.Count == 0;
    }

    private void OnResetHotkeysClicked(object sender, RoutedEventArgs e)
    {
        _hotkeys = new HotkeyMap();
        ShowHotkeys();
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderTexts();

    private void UpdateSliderTexts()
    {
        if (!IsInitialized)
        {
            return;
        }

        CollapseText.Text = FormatSeconds(CollapseSlider.Value);
        FadeDelayText.Text = FormatSeconds(FadeDelaySlider.Value);
        FadeDurationText.Text = FormatSeconds(FadeDurationSlider.Value);
    }

    private static string FormatSeconds(double seconds)
    {
        return seconds.ToString("0.#", CultureInfo.CurrentCulture) + " s";
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Dossier des captures"),
            InitialDirectory = FolderBox.Text,
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();

        Result = _original with
        {
            Language = LanguageFrenchRadio.IsChecked == true ? AppLanguage.French
                : LanguageEnglishRadio.IsChecked == true ? AppLanguage.English
                : AppLanguage.System,
            ToolbarCollapseSeconds = CollapseSlider.Value,
            FadeDelaySeconds = FadeDelaySlider.Value,
            FadeDurationSeconds = FadeDurationSlider.Value,
            WheelChangesSize = WheelBox.IsChecked == true,
            PressureEnabled = PressureBox.IsChecked == true,
            HighlightSnapEnabled = HighlightSnapBox.IsChecked == true,
            SizePerToolEnabled = SizePerToolBox.IsChecked == true,
            KeepAnnotations = KeepAnnotationsBox.IsChecked == true,
            BoardOnToolbarScreenOnly = BoardScreenBox.IsChecked == true,
            CaptureFolder = folder == CaptureDelivery.DefaultFolder ? null : folder,
            CaptureDestination = ClipboardRadio.IsChecked == true ? CaptureDestination.Clipboard
                : AskRadio.IsChecked == true ? CaptureDestination.Ask
                : CaptureDestination.Folder,
            Hotkeys = _hotkeys.ToSettings(),
        };

        DialogResult = true;
    }
}
