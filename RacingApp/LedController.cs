using RAMSPDToolkit.I2CSMBus;

namespace RacingApp;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public sealed record LedState(bool Enabled, byte Mode, Rgb Color, byte Speed);

/// <summary>5V effect as used by RCGTII's effect buttons; the register value is 0x80 | <see cref="Code"/>.</summary>
public sealed record LedEffect(string Name, byte Code)
{
    // Codes from the RACING GT EVO click handlers (Img_P4_S_xx_*). Music (10) needs audio input and is left out.
    public static readonly IReadOnlyList<LedEffect> All = new LedEffect[]
    {
        new("Estático", 9), new("Respiração", 7), new("Piscar", 8), new("Arco-íris", 5), new("Onda", 3),
        new("Meteoro", 1), new("Relâmpago", 2), new("Céu estrelado", 4), new("Aurora", 6),
    };

    public byte RegisterValue => (byte)(0x80 | Code);

    public override string ToString() => Name;
}

/// <summary>
/// 5V ARGB header of the BIOSTAR B450GT LED MCU (SMBus 0x30 on the AMD auxiliary bus), as confirmed on hardware:
/// colour in 0xA7/0xA8/0xA9, mode in 0xA6 and effect speed in 0xAB take effect after writing 0x01 to 0xA4.
/// See RacingMonitor/docs/LED_PROTOCOL.md. Only those registers are ever written.
/// </summary>
public sealed class LedController
{
    private const byte Address = 0x30;
    private const byte EnableRegister = 0xA5;   // bit 7 = header on
    private const byte ModeRegister = 0xA6;     // 0x80 | mode
    private const byte RedRegister = 0xA7;      // then G = 0xA8, B = 0xA9
    private const byte ApplyRegister = 0xA4;
    private const byte SpeedRegister = 0xAB;    // 1..255 as RCGTII's speed slider writes it; smaller = faster

    private readonly SMBusInterface _bus;
    private readonly object _gate;

    public LedController(SMBusInterface bus, object gate)
    {
        _bus = bus;
        _gate = gate;
    }

    /// <summary>Reads the current 5V state, or null when the MCU does not answer.</summary>
    public LedState? ReadState()
    {
        lock (_gate)
        {
            int enable = _bus.i2c_smbus_read_byte_data(Address, EnableRegister);
            int mode = _bus.i2c_smbus_read_byte_data(Address, ModeRegister);
            int speed = _bus.i2c_smbus_read_byte_data(Address, SpeedRegister);
            Rgb? color = ReadColor();
            if (enable < 0 || mode < 0 || speed < 0 || color is null)
                return null;

            return new LedState((enable & 0x80) != 0, (byte)mode, color.Value, (byte)speed);
        }
    }

    /// <summary>
    /// Writes the colour, the raw mode register value (0x80 | effect code) and the speed register value, applies
    /// them and returns true when all of them read back unchanged.
    /// </summary>
    public bool Apply(Rgb color, byte modeRegisterValue, byte speed)
    {
        lock (_gate)
        {
            byte[] bytes = { color.R, color.G, color.B };
            for (int i = 0; i < bytes.Length; i++)
            {
                if (_bus.i2c_smbus_write_byte_data(Address, (byte)(RedRegister + i), bytes[i]) < 0)
                    return false;
            }

            if (_bus.i2c_smbus_write_byte_data(Address, ModeRegister, modeRegisterValue) < 0)
                return false;

            if (_bus.i2c_smbus_write_byte_data(Address, SpeedRegister, speed) < 0)
                return false;

            if (_bus.i2c_smbus_write_byte_data(Address, ApplyRegister, 0x01) < 0)
                return false;

            return ReadColor() == color
                   && _bus.i2c_smbus_read_byte_data(Address, ModeRegister) == modeRegisterValue
                   && _bus.i2c_smbus_read_byte_data(Address, SpeedRegister) == speed;
        }
    }

    private Rgb? ReadColor()
    {
        int r = _bus.i2c_smbus_read_byte_data(Address, RedRegister);
        int g = _bus.i2c_smbus_read_byte_data(Address, (byte)(RedRegister + 1));
        int b = _bus.i2c_smbus_read_byte_data(Address, (byte)(RedRegister + 2));
        return r < 0 || g < 0 || b < 0 ? null : new Rgb((byte)r, (byte)g, (byte)b);
    }
}
