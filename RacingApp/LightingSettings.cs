using System.IO;
using System.Text.Json;

namespace RacingApp;

/// <summary>
/// Last lighting configuration that was applied successfully, stored as raw register values so it can be
/// re-applied without the UI (see <see cref="StartupApplier"/>).
/// </summary>
public sealed class LightingSettings
{
    public bool Sync { get; set; } = true;

    public bool HasBoard { get; set; }

    public string BoardColor { get; set; } = "#000000";

    /// <summary>Raw 0xA6 value (0x80 | effect code).</summary>
    public byte BoardMode { get; set; }

    /// <summary>Raw 0xAB value (1..255, smaller = faster).</summary>
    public byte BoardSpeed { get; set; }

    public bool HasRam { get; set; }

    public string RamColor { get; set; } = "#000000";

    /// <summary>Raw ENE 0x8021 mode.</summary>
    public byte RamMode { get; set; }

    /// <summary>What each tab showed at the last Apply (including app effects), to resume it when the app starts.</summary>
    public LedEditorState? BoardUi { get; set; }

    public LedEditorState? RamUi { get; set; }

    /// <summary>Fan headers as last applied; resumed whenever the app starts (the BIOS controls them otherwise).</summary>
    public List<FanSettings>? Fans { get; set; }

    public static string FolderPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RacingStudio");

    public static string FilePath { get; } = Path.Combine(FolderPath, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LightingSettings? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LightingSettings>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static Rgb ParseColor(string hex)
    {
        uint value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}

/// <summary>Everything a <see cref="LedEditor"/> shows, by effect name so it survives list changes.</summary>
public sealed class LedEditorState
{
    public string? Effect { get; set; }

    public string Color { get; set; } = "#000000";

    public byte Speed { get; set; } = 50;

    public string TempMin { get; set; } = "40";

    public string TempMax { get; set; } = "80";

    public string Gradient1 { get; set; } = "#0050FF";

    public string Gradient2 { get; set; } = "#FF00C8";

    public string Gradient3 { get; set; } = "#00FFB4";

    public double Cycle { get; set; } = 4;
}
