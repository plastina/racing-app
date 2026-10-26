using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace RacingApp;

public sealed record TemperatureSource(string Key, string Label);

/// <summary>One row of the fan control panel: what the user is editing for a header, plus its live RPM and duty.</summary>
public sealed class FanChannelView : INotifyPropertyChanged
{
    private int _modeIndex;
    private double _fixedPercent = 50;
    private string? _sourceKey;
    private string _tempLow = "40";
    private string _percentLow = "30";
    private string _tempHigh = "75";
    private string _percentHigh = "100";
    private string _statusText = "—";

    public FanChannelView(FanChannel channel, IReadOnlyList<TemperatureSource> sources, string? defaultSourceKey)
    {
        Channel = channel;
        Sources = sources;
        _sourceKey = defaultSourceKey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FanChannel Channel { get; }

    public string Label => Channel.IsCpu ? $"{Channel.Label} (mín. {FanController.MinCpuPercent:0} %)" : Channel.Label;

    public IReadOnlyList<TemperatureSource> Sources { get; }

    public double MinPercent => Channel.IsCpu ? FanController.MinCpuPercent : 0;

    /// <summary>Index into the mode combo box, in <see cref="FanMode"/> order.</summary>
    public int ModeIndex
    {
        get => _modeIndex;
        set
        {
            if (Set(ref _modeIndex, value))
            {
                OnPropertyChanged(nameof(FixedVisibility));
                OnPropertyChanged(nameof(CurveVisibility));
            }
        }
    }

    public Visibility FixedVisibility => _modeIndex == (int)FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CurveVisibility => _modeIndex == (int)FanMode.Curve ? Visibility.Visible : Visibility.Collapsed;

    public double FixedPercent { get => _fixedPercent; set => Set(ref _fixedPercent, value); }

    public string? SourceKey { get => _sourceKey; set => Set(ref _sourceKey, value); }

    public string TempLow { get => _tempLow; set => Set(ref _tempLow, value); }

    public string PercentLow { get => _percentLow; set => Set(ref _percentLow, value); }

    public string TempHigh { get => _tempHigh; set => Set(ref _tempHigh, value); }

    public string PercentHigh { get => _percentHigh; set => Set(ref _percentHigh, value); }

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>Validates the row; returns null with <paramref name="error"/> set when a field is invalid.</summary>
    public FanSettings? ToSettings(out string? error)
    {
        error = null;
        var settings = new FanSettings
        {
            Index = Channel.Index,
            Mode = (FanMode)_modeIndex,
            FixedPercent = (float)Math.Round(Math.Max(_fixedPercent, MinPercent)),
            SourceKey = _sourceKey,
        };

        if (settings.Mode != FanMode.Curve)
            return settings;

        if (_sourceKey is null || Sources.All(s => s.Key != _sourceKey))
        {
            error = $"{Channel.Label}: escolha a temperatura que a curva segue.";
            return null;
        }

        if (!TryParse(_tempLow, out float tLow) || !TryParse(_tempHigh, out float tHigh) || tHigh <= tLow)
        {
            error = $"{Channel.Label}: a temperatura final da curva precisa ser maior que a inicial.";
            return null;
        }

        if (!TryParse(_percentLow, out float pLow) || !TryParse(_percentHigh, out float pHigh) ||
            pLow is < 0 or > 100 || pHigh is < 0 or > 100)
        {
            error = $"{Channel.Label}: as velocidades da curva vão de 0 a 100 %.";
            return null;
        }

        (settings.TempLow, settings.TempHigh, settings.PercentLow, settings.PercentHigh) = (tLow, tHigh, pLow, pHigh);
        return settings;
    }

    public void SetSettings(FanSettings settings)
    {
        ModeIndex = (int)settings.Mode;
        FixedPercent = settings.FixedPercent;
        if (settings.SourceKey is not null && Sources.Any(s => s.Key == settings.SourceKey))
            SourceKey = settings.SourceKey;
        TempLow = settings.TempLow.ToString(CultureInfo.CurrentCulture);
        TempHigh = settings.TempHigh.ToString(CultureInfo.CurrentCulture);
        PercentLow = settings.PercentLow.ToString(CultureInfo.CurrentCulture);
        PercentHigh = settings.PercentHigh.ToString(CultureInfo.CurrentCulture);
    }

    private static bool TryParse(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
