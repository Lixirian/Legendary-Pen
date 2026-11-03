using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LegendaryPen.Core;

namespace LegendaryPen.App;

public partial class ColorPickerWindow : Window
{
    private bool _synchronizing = true;

    public ColorPickerWindow(InkColor initial, bool canReplaceQuickColor, bool replaceQuickColor)
    {
        InitializeComponent();

        ReplaceCheckBox.Visibility = canReplaceQuickColor ? Visibility.Visible : Visibility.Collapsed;
        ReplaceCheckBox.IsChecked = canReplaceQuickColor && replaceQuickColor;

        SelectedColor = initial;
        ShowOnSliders(initial);
        HexBox.Text = initial.ToHex();
        ShowPreview(initial);
        _synchronizing = false;
    }

    public InkColor SelectedColor { get; private set; }

    public bool ReplaceQuickColor => ReplaceCheckBox.IsChecked == true;

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_synchronizing)
        {
            return;
        }

        var color = InkColor.FromHsv(HueSlider.Value, SaturationSlider.Value / 100, BrightnessSlider.Value / 100);

        _synchronizing = true;
        HexBox.Text = color.ToHex();
        _synchronizing = false;

        Accept(color);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_synchronizing)
        {
            return;
        }

        if (InkColor.TryParse(HexBox.Text, out var color))
        {
            _synchronizing = true;
            ShowOnSliders(color);
            _synchronizing = false;

            Accept(color);
        }
        else
        {
            OkButton.IsEnabled = false;
        }
    }

    private void Accept(InkColor color)
    {
        SelectedColor = color;
        OkButton.IsEnabled = true;
        ShowPreview(color);
    }

    private void ShowOnSliders(InkColor color)
    {
        var (hue, saturation, brightness) = color.ToHsv();
        HueSlider.Value = hue;
        SaturationSlider.Value = saturation * 100;
        BrightnessSlider.Value = brightness * 100;
    }

    private void ShowPreview(InkColor color)
    {
        Preview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    private void OnOkClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
