using LibreHardwareMonitor.Hardware;

namespace RacingApp;

public enum FanMode
{
    /// <summary>The Super I/O's own (BIOS-configured) automatic control; the app writes nothing.</summary>
    Bios,
    Fixed,
    Curve,
}

/// <summary>What the user chose for one fan header, stored by LibreHardwareMonitor's control index.</summary>
public sealed class FanSettings
{
    public int Index { get; set; }

    public FanMode Mode { get; set; }

    public float FixedPercent { get; set; } = 50;

    /// <summary>Sensor identifier of the temperature the curve follows (CPU Tctl by default).</summary>
    public string? SourceKey { get; set; }

    public float TempLow { get; set; } = 40;

    public float PercentLow { get; set; } = 30;

    public float TempHigh { get; set; } = 75;

    public float PercentHigh { get; set; } = 100;

    /// <summary>Linear between the two points, flat outside them.</summary>
    public float Evaluate(float temperature)
    {
        if (temperature <= TempLow)
            return PercentLow;
        if (temperature >= TempHigh)
            return PercentHigh;
        return PercentLow + (temperature - TempLow) / (TempHigh - TempLow) * (PercentHigh - PercentLow);
    }
}

/// <summary>One PWM output of the Super I/O, with the tachometer that reads the same header.</summary>
public sealed record FanChannel(int Index, string Label, string ControlKey, string? FanKey, bool IsCpu, IControl Control);

/// <summary>
/// Drives the fan headers from the sensor timer: fixed duty or a temperature curve per header, or hands the header
/// back to the BIOS. LibreHardwareMonitor restores the BIOS control when the <see cref="Computer"/> closes, so
/// fans never stay on a manual duty after the app exits.
/// </summary>
public sealed class FanController
{
    /// <summary>Above this CPU temperature every header the app controls goes to 100 %.</summary>
    public const float FailsafeTemperature = 90;

    /// <summary>The CPU cooler never goes below this while the app controls it.</summary>
    public const float MinCpuPercent = 25;

    // Curves ramp up at once but down at most this much per tick (1 s), so short load spikes don't make fans pump.
    private const float MaxStepDown = 3;

    private readonly HardwareService _hardware;
    private readonly object _sync = new();
    private readonly Dictionary<int, FanSettings> _settings = new();

    // Last duty written per header; null (or missing) = left to the BIOS.
    private readonly Dictionary<int, float?> _written = new();

    public FanController(HardwareService hardware, IReadOnlyList<FanChannel> channels)
    {
        _hardware = hardware;
        Channels = channels;
    }

    public IReadOnlyList<FanChannel> Channels { get; }

    public bool FailsafeActive { get; private set; }

    public void Configure(IEnumerable<FanSettings> settings)
    {
        lock (_sync)
        {
            _settings.Clear();
            foreach (FanSettings s in settings)
                _settings[s.Index] = s;
        }
    }

    /// <summary>Computes and writes every header's duty. Runs off the UI thread, once per sensor refresh.</summary>
    public void Tick(IReadOnlyList<SensorReading> readings, float? cpuTemperature)
    {
        lock (_sync)
        {
            FailsafeActive = cpuTemperature >= FailsafeTemperature;
            foreach (FanChannel channel in Channels)
            {
                _written.TryGetValue(channel.Index, out float? previous);
                float? target = Target(channel, readings, previous);
                if (target == previous || (target is float t && previous is float p && Math.Abs(t - p) < 0.5f))
                    continue;

                _hardware.SetFanDuty(channel, target);
                _written[channel.Index] = target;
            }
        }
    }

    /// <summary>Hands every header back to the BIOS.</summary>
    public void RestoreBios()
    {
        lock (_sync)
        {
            _settings.Clear();
            foreach (FanChannel channel in Channels)
            {
                if (_written.TryGetValue(channel.Index, out float? previous) && previous is not null)
                    _hardware.SetFanDuty(channel, null);
                _written[channel.Index] = null;
            }
        }
    }

    private float? Target(FanChannel channel, IReadOnlyList<SensorReading> readings, float? previous)
    {
        if (!_settings.TryGetValue(channel.Index, out FanSettings? s) || s.Mode == FanMode.Bios)
            return null;

        float target;
        if (s.Mode == FanMode.Fixed)
        {
            target = s.FixedPercent;
        }
        else
        {
            // A curve whose sensor vanished runs the fan flat out rather than guessing.
            float? temperature = readings.FirstOrDefault(r => r.Key == s.SourceKey)?.Value;
            target = temperature is float value ? s.Evaluate(value) : 100;
            if (previous is float p && target < p)
                target = Math.Max(target, p - MaxStepDown);
        }

        if (FailsafeActive)
            target = 100;
        if (channel.IsCpu)
            target = Math.Max(target, MinCpuPercent);
        return Math.Clamp(MathF.Round(target), 0, 100);
    }
}
