using System.IO;
using System.Text;

namespace RacingMonitor;

/// <summary>
/// Sensor labels and voltage dividers for one board, read from BIOSTAR's Config.ini format:
/// TEMPn / FANn are 1-based, VINn is 0-based, and VINn_R1/VINn_R2 describe a resistor divider.
/// </summary>
public sealed class BoardProfile
{
    private readonly Dictionary<string, string> _values;

    private BoardProfile(string name, Dictionary<string, string> values)
    {
        Name = name;
        _values = values;
    }

    public string Name { get; }

    public static BoardProfile? Load(string iniPath, string boardName)
    {
        if (!File.Exists(iniPath))
            return null;

        // The file mixes GBK/Korean labels; we only use ASCII keys and the English (_E) labels.
        var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inSection = false;
        foreach (string rawLine in File.ReadLines(iniPath, Encoding.Latin1))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (inSection)
                    break;
                inSection = string.Equals(line[1..^1], boardName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.IndexOf('=');
            if (inSection && eq > 0)
                section[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return section.Count == 0 ? null : new BoardProfile(boardName, section);
    }

    /// <summary>Label for a temperature sensor; <paramref name="index"/> is LibreHardwareMonitor's 0-based index.</summary>
    public string? Temperature(int index) => Label($"TEMP{index + 1}");

    /// <summary>Label for a fan tachometer; <paramref name="index"/> is LibreHardwareMonitor's 0-based index.</summary>
    public string? Fan(int index) => Label($"FAN{index + 1}");

    /// <summary>Label for a voltage input; <paramref name="index"/> is LibreHardwareMonitor's 0-based index.</summary>
    public string? Voltage(int index) => Label($"VIN{index}");

    /// <summary>
    /// Multiplier for VINn when Config.ini declares a divider: Vreal = Vpin * (R1 + R2) / R2.
    /// Returns null when the board file declares none.
    /// </summary>
    public double? VoltageScale(int index)
    {
        if (_values.TryGetValue($"VIN{index}_R1", out string? r1Text) &&
            _values.TryGetValue($"VIN{index}_R2", out string? r2Text) &&
            double.TryParse(r1Text, out double r1) &&
            double.TryParse(r2Text, out double r2) && r2 > 0)
        {
            return (r1 + r2) / r2;
        }

        return null;
    }

    private string? Label(string key) =>
        _values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;
}
