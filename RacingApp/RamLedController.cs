using RAMSPDToolkit.I2CSMBus;

namespace RacingApp;

/// <summary>ENE DRAM mode (register 0x8021).</summary>
public sealed record RamLedEffect(string Name, byte Mode)
{
    // 1–3 and the "colorful" 4/6/10 are what RCGTII writes (0x4d0b70); the rest follow the usual ENE mode table.
    // All of them were confirmed visually on the AUDA0-E6K5-0101 DIMMs.
    public static readonly IReadOnlyList<RamLedEffect> All = new RamLedEffect[]
    {
        new("Estático", 1), new("Respiração", 2), new("Piscar", 3), new("Ciclo de cores", 4),
        new("Respiração colorida", 6), new("Piscar colorido", 10),
        new("Arco-íris", 5), new("Perseguição", 9), new("Onda colorida", 11),
    };

    public static string NameOf(byte mode) => All.FirstOrDefault(e => e.Mode == mode)?.Name ?? $"modo {mode}";

    public override string ToString() => Name;
}

public sealed record RamLedState(string ChipName, byte Mode, byte[] EffectColors)
{
    /// <summary>First LED colour (registers are stored R, B, G).</summary>
    public Rgb FirstColor => new(EffectColors[0], EffectColors[2], EffectColors[1]);
}

/// <summary>
/// ENE DRAM RGB controller shared by both DIMMs at 0x77 on the primary SMBus (chip AUDA0-E6K5-0101, 8 LEDs,
/// "v2" register map), as confirmed on hardware. Registers are reached through the ENE read/write pointer:
/// byte-swapped register address on command 0x00, then command 0x01 (write) or 0x81 (read).
/// Only the effect colours (0x8160–0x8177), the mode (0x8021) and the apply register (0x80A0) are ever written;
/// the SPD EEPROMs at 0x50–0x57 are never addressed. See RacingMonitor/docs/LED_PROTOCOL.md.
/// </summary>
public sealed class RamLedController
{
    public const string SupportedChip = "AUDA0-E6K5-0101";

    private const byte Address = 0x77;
    private const ushort NameRegister = 0x1000;
    private const ushort ModeRegister = 0x8021;
    private const ushort EffectColorsRegister = 0x8160;
    private const ushort ApplyRegister = 0x80A0;
    private const int LedCount = 8;

    public const int Leds = LedCount;

    private readonly SMBusInterface _bus;
    private readonly object _gate;
    private byte[]? _lastFrame;

    public RamLedController(SMBusInterface bus, object gate)
    {
        _bus = bus;
        _gate = gate;
    }

    /// <summary>Reads chip name, mode and colours; null when the controller does not answer.</summary>
    public RamLedState? ReadState()
    {
        lock (_gate)
        {
            if (_bus.i2c_smbus_read_byte(Address) < 0)
                return null;

            byte[]? name = Read(NameRegister, 16);
            byte[]? mode = Read(ModeRegister, 1);
            byte[]? colors = Read(EffectColorsRegister, LedCount * 3);
            if (name is null || mode is null || colors is null)
                return null;

            string chip = new(name.TakeWhile(c => c != 0).Select(c => c is >= 0x20 and < 0x7F ? (char)c : '.').ToArray());
            return new RamLedState(chip, mode[0], colors);
        }
    }

    /// <summary>Sets every LED of both DIMMs to <paramref name="color"/> with the given ENE mode.</summary>
    public bool Apply(Rgb color, byte mode) =>
        ApplyRaw(Enumerable.Range(0, LedCount).SelectMany(_ => new[] { color.R, color.B, color.G }).ToArray(), mode);

    /// <summary>
    /// Writes the raw effect-colour bytes (R, B, G per LED) and the mode, applies them and verifies by reading back.
    /// </summary>
    public bool ApplyRaw(byte[] rawColors, byte mode)
    {
        if (rawColors.Length != LedCount * 3)
            throw new ArgumentException($"Esperados {LedCount * 3} bytes.", nameof(rawColors));

        lock (_gate)
        {
            _lastFrame = null;
            if (!Write(EffectColorsRegister, rawColors)
                || !Write(ModeRegister, new[] { mode })
                || !Write(ApplyRegister, new byte[] { 0x01 }))
                return false;

            byte[]? colors = Read(EffectColorsRegister, rawColors.Length);
            byte[]? currentMode = Read(ModeRegister, 1);
            return colors is not null && colors.SequenceEqual(rawColors) && currentMode?[0] == mode;
        }
    }

    /// <summary>
    /// Per-frame update for software effects: one colour per LED (both DIMMs), writing only the bytes that changed
    /// since the last frame, then apply. No read-back. The DIMMs must already be in the static mode (see
    /// <see cref="Apply"/>).
    /// </summary>
    public bool WriteFrame(IReadOnlyList<Rgb> leds)
    {
        if (leds.Count != LedCount)
            throw new ArgumentException($"Esperados {LedCount} LEDs.", nameof(leds));

        byte[] raw = leds.SelectMany(c => new[] { c.R, c.B, c.G }).ToArray();
        lock (_gate)
        {
            bool changed = false;
            for (int i = 0; i < raw.Length; i++)
            {
                if (_lastFrame is not null && _lastFrame[i] == raw[i])
                    continue;
                if (!Write((ushort)(EffectColorsRegister + i), new[] { raw[i] }))
                {
                    _lastFrame = null;
                    return false;
                }

                changed = true;
            }

            if (changed && !Write(ApplyRegister, new byte[] { 0x01 }))
            {
                _lastFrame = null;
                return false;
            }

            _lastFrame = raw;
            return true;
        }
    }

    private bool Write(ushort register, byte[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (!SetPointer((ushort)(register + i)) || _bus.i2c_smbus_write_byte_data(Address, 0x01, values[i]) < 0)
                return false;
        }

        return true;
    }

    private byte[]? Read(ushort register, int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (!SetPointer((ushort)(register + i)))
                return null;

            int value = _bus.i2c_smbus_read_byte_data(Address, 0x81);
            if (value < 0)
                return null;
            data[i] = (byte)value;
        }

        return data;
    }

    private bool SetPointer(ushort register) =>
        _bus.i2c_smbus_write_word_data(Address, 0x00, (ushort)(((register << 8) & 0xFF00) | ((register >> 8) & 0x00FF))) >= 0;
}
