using System.Diagnostics;

namespace RacingApp;

public enum SoftEffectKind
{
    Temperature,
    Gradient,
    RunningLight,
    Bounce,
    Fill,
}

/// <summary>App-computed effect as listed next to the chips' own effects.</summary>
public sealed record SoftEffectOption(string Name, SoftEffectKind Kind)
{
    public static readonly IReadOnlyList<SoftEffectOption> All = new SoftEffectOption[]
    {
        new("Cor pela temperatura (app)", SoftEffectKind.Temperature),
        new("Gradiente (app)", SoftEffectKind.Gradient),
        new("Luz correndo (app)", SoftEffectKind.RunningLight),
        new("Vai e vem (app)", SoftEffectKind.Bounce),
        new("Preenchimento (app)", SoftEffectKind.Fill),
    };

    public override string ToString() => Name;
}

/// <summary>One animation frame: a single colour for the board's 5V header and one colour per DIMM LED.</summary>
public sealed record EffectFrame(Rgb Board, Rgb[] Ram);

/// <summary>An effect computed by the app (as opposed to the effects built into the LED chips).</summary>
public interface ISoftwareEffect
{
    string Name { get; }

    /// <summary>Frame at <paramref name="seconds"/> since the effect started.</summary>
    EffectFrame Render(double seconds);
}

/// <summary>Blue (cold) → green → red (hot) following the CPU temperature, smoothed over time.</summary>
public sealed class TemperatureEffect : ISoftwareEffect
{
    internal static readonly Rgb Cold = new(0x00, 0x50, 0xFF);
    internal static readonly Rgb Warm = new(0x00, 0xFF, 0x40);
    internal static readonly Rgb Hot = new(0xFF, 0x00, 0x00);

    private readonly Func<float?> _temperature;
    private readonly float _min;
    private readonly float _max;
    private double _smoothed = double.NaN;

    public TemperatureEffect(Func<float?> temperature, float min, float max)
    {
        _temperature = temperature;
        _min = min;
        _max = Math.Max(max, min + 1);
    }

    public string Name => "Cor pela temperatura";

    public float? LastTemperature { get; private set; }

    public EffectFrame Render(double seconds)
    {
        LastTemperature = _temperature();
        double target = LastTemperature is float t ? Math.Clamp((t - _min) / (_max - _min), 0, 1) : 0;

        // Ease toward the target so a temperature spike fades in instead of jumping.
        _smoothed = double.IsNaN(_smoothed) ? target : _smoothed + (target - _smoothed) * 0.08;
        Rgb color = _smoothed < 0.5 ? Rgb.Lerp(Cold, Warm, _smoothed * 2) : Rgb.Lerp(Warm, Hot, (_smoothed - 0.5) * 2);
        return new EffectFrame(color, Enumerable.Repeat(color, RamLedController.Leds).ToArray());
    }
}

/// <summary>Smooth loop through 2–3 colours; each DIMM LED is phase-shifted so the gradient travels.</summary>
public sealed class GradientEffect : ISoftwareEffect
{
    private readonly Rgb[] _colors;
    private readonly double _period;

    public GradientEffect(IReadOnlyList<Rgb> colors, double periodSeconds)
    {
        _colors = colors.Count >= 2 ? colors.ToArray() : new[] { colors[0], colors[0] };
        _period = Math.Max(periodSeconds, 0.5);
    }

    public string Name => "Gradiente";

    public EffectFrame Render(double seconds)
    {
        double phase = seconds / _period;
        var ram = new Rgb[RamLedController.Leds];
        for (int i = 0; i < ram.Length; i++)
            ram[i] = At(phase + (double)i / ram.Length / _colors.Length);
        return new EffectFrame(At(phase), ram);
    }

    private Rgb At(double phase)
    {
        double position = (phase - Math.Floor(phase)) * _colors.Length;
        int index = (int)position % _colors.Length;
        double t = position - Math.Floor(position);
        double eased = (1 - Math.Cos(t * Math.PI)) / 2;
        return Rgb.Lerp(_colors[index], _colors[(index + 1) % _colors.Length], eased);
    }
}

/// <summary>
/// Patterns along the 8 LEDs of each DIMM; the board's single-colour header stays on the colour.
/// Running light: a point with a fading tail that wraps around. Bounce: the point goes back and forth.
/// Fill: LEDs light up one by one until the stick is full, then go out in the same order.
/// </summary>
public sealed class DimmPatternEffect : ISoftwareEffect
{
    private const double Tail = 3.0;

    private readonly SoftEffectKind _kind;
    private readonly Rgb _color;
    private readonly double _period;

    public DimmPatternEffect(SoftEffectKind kind, Rgb color, double periodSeconds)
    {
        _kind = kind;
        _color = color;
        _period = Math.Max(periodSeconds, 0.3);
    }

    public string Name => _kind switch
    {
        SoftEffectKind.Bounce => "Vai e vem",
        SoftEffectKind.Fill => "Preenchimento",
        _ => "Luz correndo",
    };

    public EffectFrame Render(double seconds)
    {
        int count = RamLedController.Leds;
        double phase = seconds / _period - Math.Floor(seconds / _period);
        var ram = new Rgb[count];
        for (int i = 0; i < count; i++)
            ram[i] = _color.Scale(Intensity(i, phase, count));
        return new EffectFrame(_color, ram);
    }

    private double Intensity(int led, double phase, int count)
    {
        switch (_kind)
        {
            case SoftEffectKind.Bounce:
            {
                // Triangle wave 0 → count-1 → 0; light falls off around the head on both sides.
                double head = (phase < 0.5 ? phase * 2 : (1 - phase) * 2) * (count - 1);
                double distance = Math.Abs(head - led);
                return distance < Tail / 2 ? Math.Pow(1 - distance / (Tail / 2), 2) : 0;
            }

            case SoftEffectKind.Fill:
            {
                // First half fills from the first LED, second half empties from the first LED.
                double level = (phase < 0.5 ? phase * 2 : (phase - 0.5) * 2) * count;
                double lit = phase < 0.5 ? level - led : led + 1 - level;
                return Math.Clamp(lit, 0, 1);
            }

            default:
            {
                double head = phase * count;
                double behind = (head - led + count) % count; // distance behind the head, wrapping around
                return behind < Tail ? Math.Pow(1 - behind / Tail, 2) : 0;
            }
        }
    }
}

/// <summary>
/// Runs a software effect: renders frames at a fixed rate and pushes them to the board and/or DIMM controllers.
/// Before starting, both targets are put in their static mode so the written colours show as-is.
/// </summary>
public sealed class SoftwareEffectEngine
{
    private const int FramesPerSecond = 20;
    private const int MaxConsecutiveFailures = 10;

    private readonly LedController? _board;
    private readonly RamLedController? _ram;
    private CancellationTokenSource? _cancel;
    private Task? _loop;

    public SoftwareEffectEngine(LedController? board, RamLedController? ram)
    {
        _board = board;
        _ram = ram;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Raised from the effect thread about once per second, and when the loop stops on errors.</summary>
    public event Action<string>? Status;

    /// <summary>
    /// Starts animating <paramref name="boardEffect"/> on the board and <paramref name="ramEffect"/> on the DIMMs
    /// (either may be null; pass the same instance to keep both in sync).
    /// </summary>
    public void Start(ISoftwareEffect? boardEffect, ISoftwareEffect? ramEffect, byte boardSpeed)
    {
        if (IsRunning)
            throw new InvalidOperationException("Um efeito já está rodando.");

        LedController? board = boardEffect is null ? null : _board;
        RamLedController? ram = ramEffect is null ? null : _ram;
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;
        _loop = Task.Run(() => Run(boardEffect, board, ramEffect, ram, boardSpeed, token), token);
    }

    public async Task StopAsync()
    {
        if (_cancel is null || _loop is null)
            return;

        _cancel.Cancel();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cancel.Dispose();
        _cancel = null;
        _loop = null;
    }

    private void Run(ISoftwareEffect? boardEffect, LedController? board, ISoftwareEffect? ramEffect, RamLedController? ram,
                     byte boardSpeed, CancellationToken token)
    {
        // Static mode first, so the colours written below show as-is instead of under a chip effect.
        if (board is not null && boardEffect is not null
            && !board.Apply(boardEffect.Render(0).Board, LedEffect.All[0].RegisterValue, boardSpeed))
            board = null;
        if (ram is not null && ramEffect is not null && !ram.Apply(ramEffect.Render(0).Ram[0], RamLedEffect.All[0].Mode))
            ram = null;
        if (board is null && ram is null)
        {
            Status?.Invoke("Efeito do app: não foi possível preparar os LEDs.");
            return;
        }

        var clock = Stopwatch.StartNew();
        var frameTime = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
        int frames = 0, failures = 0;
        double lastReport = 0;
        while (!token.IsCancellationRequested)
        {
            double now = clock.Elapsed.TotalSeconds;
            EffectFrame? boardFrame = board is null ? null : boardEffect!.Render(now);
            EffectFrame? ramFrame = ram is null ? null
                : ReferenceEquals(ramEffect, boardEffect) && boardFrame is not null ? boardFrame : ramEffect!.Render(now);
            bool ok = (boardFrame is null || board!.WriteFrame(boardFrame.Board))
                      & (ramFrame is null || ram!.WriteFrame(ramFrame.Ram));
            failures = ok ? 0 : failures + 1;
            if (failures >= MaxConsecutiveFailures)
            {
                Status?.Invoke("Efeito do app interrompido: os controladores pararam de responder.");
                return;
            }

            frames++;
            if (now - lastReport >= 1)
            {
                Status?.Invoke($"Efeito do app rodando ({Describe(boardEffect, board, ramEffect, ram)}) · {frames / (now - lastReport):0} quadros/s");
                frames = 0;
                lastReport = now;
            }

            TimeSpan wait = frameTime - (clock.Elapsed - TimeSpan.FromSeconds(now));
            if (wait > TimeSpan.Zero)
                token.WaitHandle.WaitOne(wait);
        }
    }

    private static string Describe(ISoftwareEffect? boardEffect, LedController? board, ISoftwareEffect? ramEffect, RamLedController? ram)
    {
        static string Label(ISoftwareEffect effect) =>
            effect is TemperatureEffect { LastTemperature: float t } ? $"{effect.Name}, CPU {t:0} °C" : effect.Name;

        var parts = new List<string>();
        if (board is not null)
            parts.Add($"placa: {Label(boardEffect!)}");
        if (ram is not null)
            parts.Add(ReferenceEquals(ramEffect, boardEffect) && board is not null ? "memórias: sincronizadas" : $"memórias: {Label(ramEffect!)}");
        return string.Join(" · ", parts);
    }
}
