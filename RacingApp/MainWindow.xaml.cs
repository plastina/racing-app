using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;

namespace RacingApp;

public partial class MainWindow : Window
{
    private static readonly Rgb[] QuickColors =
    {
        new(0xFF, 0x00, 0x00), new(0xFF, 0x80, 0x00), new(0xFF, 0xFF, 0x00), new(0x00, 0xFF, 0x00),
        new(0x00, 0xFF, 0xFF), new(0x00, 0x00, 0xFF), new(0x66, 0x49, 0xFF), new(0xFF, 0x00, 0xFF),
        new(0xFF, 0xFF, 0xFF),
    };

    private readonly ObservableCollection<SensorRow> _temperatures = new();
    private readonly ObservableCollection<SensorRow> _fans = new();
    private readonly ObservableCollection<SensorRow> _voltages = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private HardwareService? _hardware;
    private LedController? _leds;
    private LedState? _original;
    private bool _refreshing;
    private bool _syncingInputs;

    public MainWindow()
    {
        InitializeComponent();
        TemperatureList.ItemsSource = _temperatures;
        FanList.ItemsSource = _fans;
        VoltageList.ItemsSource = _voltages;

        foreach (Rgb color in QuickColors)
        {
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(ToMedia(color)),
                ToolTip = color.ToString(),
            };
            swatch.Click += (_, _) => ShowColor(color);
            Swatches.Children.Add(swatch);
        }

        EffectBox.ItemsSource = LedEffect.All;
        EffectBox.SelectedIndex = 0;
        SpeedSlider.Value = 255 - DefaultSpeed;
    }

    // RCGTII's per-effect default ("3_Speed" setting).
    private const byte DefaultSpeed = 50;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        string iniPath = Path.Combine(AppContext.BaseDirectory, "BoardConfig.ini");
        try
        {
            _hardware = await Task.Run(() => new HardwareService(iniPath));
        }
        catch (Exception ex)
        {
            BoardText.Text = $"Não foi possível abrir o acesso ao hardware: {ex.Message}";
            LedStatus.Text = "LEDs indisponíveis.";
            return;
        }

        BoardText.Text = $"{_hardware.BoardManufacturer} {_hardware.BoardName}" +
                         (_hardware.Profile is null ? " · sem perfil no Config.ini" : " · perfil BIOSTAR carregado") +
                         (_hardware.HasHardwareAccess ? "" : "  ⚠ sem administrador ou PawnIO: leituras limitadas");

        _timer.Tick += async (_, _) => await RefreshSensorsAsync();
        _timer.Start();
        await RefreshSensorsAsync();
        await InitLedsAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _hardware?.Dispose();
    }

    private async Task RefreshSensorsAsync()
    {
        if (_hardware is null || _refreshing)
            return;

        _refreshing = true;
        try
        {
            IReadOnlyList<SensorReading> readings = await Task.Run(_hardware.ReadSensors);
            Update(_temperatures, readings.Where(r => r.Type == SensorType.Temperature), "0", "°C");
            Update(_fans, readings.Where(r => r.Type == SensorType.Fan), "0", "RPM");
            Update(_voltages, readings.Where(r => r.Type == SensorType.Voltage), "0.000", "V");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static void Update(ObservableCollection<SensorRow> rows, IEnumerable<SensorReading> readings, string format, string unit)
    {
        foreach (SensorReading reading in readings)
        {
            SensorRow? row = rows.FirstOrDefault(r => r.Key == reading.Key);
            if (row is null)
            {
                row = new SensorRow(reading.Key, reading.Label);
                rows.Add(row);
            }

            row.ValueText = reading.Value is null ? "—" : $"{reading.Value.Value.ToString(format, CultureInfo.CurrentCulture)} {unit}";
        }
    }

    private async Task InitLedsAsync()
    {
        if (_hardware is null)
            return;

        if (!_hardware.HasHardwareAccess)
        {
            LedStatus.Text = "LEDs indisponíveis: abra o app como administrador com o PawnIO instalado.";
            return;
        }

        var bus = await Task.Run(_hardware.FindAuxBus);
        if (bus is null)
        {
            LedStatus.Text = "LEDs indisponíveis: não encontrei o SMBus auxiliar (0x0B20) pelo PawnIO.";
            return;
        }

        _leds = new LedController(bus, _hardware.Gate);
        LedState? state = await Task.Run(_leds.ReadState);
        if (state is null)
        {
            LedStatus.Text = "LEDs indisponíveis: o controlador 0x30 não respondeu.";
            _leds = null;
            return;
        }

        _original = state;
        ShowState(state);
        LedPanel.IsEnabled = true;
        LedStatus.Text = $"Atual: {state.Color} · {EffectName(state.Mode)} · velocidade 0x{state.Speed:X2} · conector 5V {(state.Enabled ? "ligado" : "DESLIGADO")}" +
                         (state.Enabled ? "" : "\nO conector está desligado; a cor só aparece quando ele estiver ligado.");
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (EffectBox.SelectedItem is LedEffect effect)
            await ApplyAsync(CurrentColor(), effect.RegisterValue, CurrentSpeed(), "Aplicado");
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        SpeedText.Text = $"registrador 0xAB = {CurrentSpeed()} (0x{CurrentSpeed():X2})";

    private byte CurrentSpeed() => (byte)(255 - (int)SpeedSlider.Value);

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_original is null)
            return;

        ShowState(_original);
        await ApplyAsync(_original.Color, _original.Mode, _original.Speed, "Original restaurado");
    }

    private async Task ApplyAsync(Rgb color, byte mode, byte speed, string what)
    {
        if (_leds is null)
            return;

        LedPanel.IsEnabled = false;
        try
        {
            bool ok = await Task.Run(() => _leds.Apply(color, mode, speed));
            LedStatus.Text = ok
                ? $"{what}: {color} · {EffectName(mode)} · velocidade 0x{speed:X2}"
                : $"Falha ao aplicar {color} · {EffectName(mode)} · velocidade 0x{speed:X2}: o controlador não confirmou. Tente de novo.";
        }
        finally
        {
            LedPanel.IsEnabled = true;
        }
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingInputs)
            return;

        Rgb color = CurrentColor();
        Preview.Background = new SolidColorBrush(ToMedia(color));
        _syncingInputs = true;
        HexBox.Text = color.ToString();
        _syncingInputs = false;
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ParseHex();
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => ParseHex();

    private void ParseHex()
    {
        string text = HexBox.Text.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            ShowColor(new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value));
        else
            HexBox.Text = CurrentColor().ToString();
    }

    private void ShowColor(Rgb color)
    {
        _syncingInputs = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _syncingInputs = false;
        HexBox.Text = color.ToString();
        Preview.Background = new SolidColorBrush(ToMedia(color));
    }

    private void ShowState(LedState state)
    {
        ShowColor(state.Color);
        LedEffect? effect = LedEffect.All.FirstOrDefault(e => e.RegisterValue == state.Mode);
        if (effect is not null)
            EffectBox.SelectedItem = effect;
        SpeedSlider.Value = 255 - Math.Max((int)state.Speed, 1);
    }

    private static string EffectName(byte mode) =>
        LedEffect.All.FirstOrDefault(e => e.RegisterValue == mode)?.Name is string name
            ? $"efeito {name} (0x{mode:X2})"
            : $"efeito desconhecido (0x{mode:X2})";

    private Rgb CurrentColor() => new((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);

    private static Color ToMedia(Rgb color) => Color.FromRgb(color.R, color.G, color.B);
}

public sealed class SensorRow : INotifyPropertyChanged
{
    private string _valueText = "—";

    public SensorRow(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Label { get; }

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value)
                return;
            _valueText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
        }
    }
}
