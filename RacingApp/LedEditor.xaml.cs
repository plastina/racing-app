using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RacingApp;

/// <summary>
/// Effect, colour and effect-specific settings for one LED device. The effect list mixes the chip's own effects
/// with the app-computed ones (<see cref="SoftEffectOption"/>); the controls below follow the selected effect.
/// </summary>
public partial class LedEditor : UserControl
{
    private static readonly Rgb[] QuickColors =
    {
        new(0xFF, 0x00, 0x00), new(0xFF, 0x80, 0x00), new(0xFF, 0xFF, 0x00), new(0x00, 0xFF, 0x00),
        new(0x00, 0xFF, 0xFF), new(0x00, 0x00, 0xFF), new(0x66, 0x49, 0xFF), new(0xFF, 0x00, 0xFF),
        new(0xFF, 0xFF, 0xFF),
    };

    private bool _syncingInputs;
    private bool _showSpeed = true;

    public LedEditor()
    {
        InitializeComponent();
        foreach (Rgb color in QuickColors)
        {
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(ToMedia(color)),
                ToolTip = color.ToString(),
            };
            swatch.Click += (_, _) => Color = color;
            Swatches.Children.Add(swatch);
        }

        UpdateGradientPreviews();
        UpdateSections();
    }

    /// <summary>Whether the chip-effect speed slider exists for this device (board only).</summary>
    public bool ShowSpeed
    {
        get => _showSpeed;
        set
        {
            _showSpeed = value;
            UpdateSections();
        }
    }

    public IEnumerable? Effects
    {
        get => EffectBox.ItemsSource;
        set
        {
            EffectBox.ItemsSource = value;
            EffectBox.SelectedIndex = 0;
        }
    }

    public object? SelectedEffect
    {
        get => EffectBox.SelectedItem;
        set => EffectBox.SelectedItem = value;
    }

    public bool IsAppEffectSelected => EffectBox.SelectedItem is SoftEffectOption;

    public Rgb Color
    {
        get => new((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        set
        {
            _syncingInputs = true;
            RedSlider.Value = value.R;
            GreenSlider.Value = value.G;
            BlueSlider.Value = value.B;
            _syncingInputs = false;
            HexBox.Text = value.ToString();
            Preview.Background = new SolidColorBrush(ToMedia(value));
        }
    }

    /// <summary>Speed register value (1..255, smaller = faster).</summary>
    public byte Speed
    {
        get => (byte)(255 - (int)SpeedSlider.Value);
        set => SpeedSlider.Value = 255 - Math.Max((int)value, 1);
    }

    /// <summary>
    /// Builds the selected app effect with the current settings; null when a chip effect is selected
    /// (<paramref name="error"/> null) or the settings are invalid (<paramref name="error"/> set).
    /// </summary>
    public ISoftwareEffect? BuildAppEffect(Func<float?> cpuTemperature, out string? error)
    {
        error = null;
        if (EffectBox.SelectedItem is not SoftEffectOption option)
            return null;

        switch (option.Kind)
        {
            case SoftEffectKind.Temperature:
                if (!float.TryParse(TempMinBox.Text, out float min) || !float.TryParse(TempMaxBox.Text, out float max) || max <= min)
                {
                    error = "Faixa de temperatura inválida (o máximo precisa ser maior que o mínimo).";
                    return null;
                }

                return new TemperatureEffect(cpuTemperature, min, max);

            case SoftEffectKind.Gradient:
                var colors = new[] { Gradient1Box.Text, Gradient2Box.Text, Gradient3Box.Text }
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(TryParseColor)
                    .ToList();
                if (colors.Count < 2 || colors.Any(c => c is null))
                {
                    error = "Gradiente: use 2 ou 3 cores no formato #RRGGBB.";
                    return null;
                }

                return new GradientEffect(colors.Select(c => c!.Value).ToList(), CycleSlider.Value);

            default:
                return new DimmPatternEffect(option.Kind, Color, CycleSlider.Value);
        }
    }

    public LedEditorState GetState() => new()
    {
        Effect = EffectBox.SelectedItem?.ToString(),
        Color = Color.ToString(),
        Speed = Speed,
        TempMin = TempMinBox.Text,
        TempMax = TempMaxBox.Text,
        Gradient1 = Gradient1Box.Text,
        Gradient2 = Gradient2Box.Text,
        Gradient3 = Gradient3Box.Text,
        Cycle = CycleSlider.Value,
    };

    public void SetState(LedEditorState state)
    {
        object? effect = EffectBox.Items.Cast<object>().FirstOrDefault(item => item.ToString() == state.Effect);
        if (effect is not null)
            EffectBox.SelectedItem = effect;
        if (TryParseColor(state.Color) is Rgb color)
            Color = color;
        Speed = state.Speed;
        TempMinBox.Text = state.TempMin;
        TempMaxBox.Text = state.TempMax;
        Gradient1Box.Text = state.Gradient1;
        Gradient2Box.Text = state.Gradient2;
        Gradient3Box.Text = state.Gradient3;
        CycleSlider.Value = state.Cycle;
    }

    private void OnEffectChanged(object sender, SelectionChangedEventArgs e) => UpdateSections();

    /// <summary>Shows only the controls the selected effect uses.</summary>
    private void UpdateSections()
    {
        if (SpeedPanel is null)
            return;

        SoftEffectKind? kind = (EffectBox.SelectedItem as SoftEffectOption)?.Kind;
        ColorSection.Visibility = kind is SoftEffectKind.Temperature or SoftEffectKind.Gradient ? Visibility.Collapsed : Visibility.Visible;
        TempPanel.Visibility = kind == SoftEffectKind.Temperature ? Visibility.Visible : Visibility.Collapsed;
        GradientPanel.Visibility = kind == SoftEffectKind.Gradient ? Visibility.Visible : Visibility.Collapsed;
        CyclePanel.Visibility = kind is not null and not SoftEffectKind.Temperature ? Visibility.Visible : Visibility.Collapsed;
        SpeedPanel.Visibility = kind is null && _showSpeed ? Visibility.Visible : Visibility.Collapsed;
        AppEffectNote.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingInputs)
            return;

        Rgb color = Color;
        Preview.Background = new SolidColorBrush(ToMedia(color));
        HexBox.Text = color.ToString();
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        SpeedText.Text = $"registrador 0xAB = {Speed} (0x{Speed:X2})";

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ParseHex();
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => ParseHex();

    private void ParseHex()
    {
        if (TryParseColor(HexBox.Text) is Rgb color)
            Color = color;
        else
            HexBox.Text = Color.ToString();
    }

    private void OnGradientTextChanged(object sender, TextChangedEventArgs e) => UpdateGradientPreviews();

    private void UpdateGradientPreviews()
    {
        if (Gradient3Preview is null)
            return;

        foreach (var (box, preview) in new[] { (Gradient1Box, Gradient1Preview), (Gradient2Box, Gradient2Preview), (Gradient3Box, Gradient3Preview) })
            preview.Background = TryParseColor(box.Text) is Rgb c ? new SolidColorBrush(ToMedia(c)) : Brushes.Transparent;
    }

    private static Rgb? TryParseColor(string text)
    {
        string hex = text.Trim().TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)
            ? new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : null;
    }

    private static System.Windows.Media.Color ToMedia(Rgb color) => System.Windows.Media.Color.FromRgb(color.R, color.G, color.B);
}
