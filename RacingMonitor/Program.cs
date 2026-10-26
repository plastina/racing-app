using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using RacingMonitor;

// Read-only prototype: temperatures, fan speeds and voltages through LibreHardwareMonitor + PawnIO.
// Usage: RacingMonitor [--once] [--all] [--board <name>] [--ini <path>] [--interval <ms>]
//        RacingMonitor --leds [--led-bus <index>]   (read-only LED MCU probe)

bool once = args.Contains("--once");
bool showAll = args.Contains("--all");
string iniPath = Option("--ini") ?? Path.Combine(AppContext.BaseDirectory, "BoardConfig.ini");
int intervalMs = int.TryParse(Option("--interval"), out int ms) ? Math.Max(250, ms) : 1000;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--leds"))
{
    Environment.ExitCode = LedMcuProbe.Run(args);
    return;
}

bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
bool hardwareAccess = isAdmin && PawnIo.IsInstalled;
Console.WriteLine($"Administrador: {(isAdmin ? "sim" : "NÃO")}");
Console.WriteLine($"PawnIO instalado: {(PawnIo.IsInstalled ? $"sim ({PawnIo.Version})" : "NÃO")}");
if (!hardwareAccess)
{
    Console.WriteLine("Aviso: sem administrador + PawnIO não há acesso ao Super I/O nem aos MSRs da CPU;");
    Console.WriteLine("       fans/tensões da placa não aparecem e os valores da CPU abaixo NÃO são confiáveis.");
}

var computer = new Computer
{
    IsMotherboardEnabled = true,
    IsCpuEnabled = true,
};
computer.Open();

string boardName = Option("--board") ?? computer.SMBios.Board?.ProductName?.Trim() ?? "";
BoardProfile? profile = BoardProfile.Load(iniPath, boardName);
Console.WriteLine($"Placa: {computer.SMBios.Board?.ManufacturerName} {boardName}" +
                  (profile is null ? " (sem perfil no Config.ini — usando nomes do LHM)" : " (perfil do Config.ini carregado)"));

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

try
{
    do
    {
        var readings = Read(computer, profile, showAll);
        if (!once)
            Console.Clear();
        Print(readings);
        if (!hardwareAccess)
            Console.WriteLine("\n(leituras sem acesso ao hardware — rode como administrador com PawnIO instalado)");
        if (once)
            break;
        Console.WriteLine($"\nAtualizando a cada {intervalMs} ms — Ctrl+C para sair.");
        try { await Task.Delay(intervalMs, cancel.Token); } catch (TaskCanceledException) { }
    }
    while (!cancel.IsCancellationRequested);
}
finally
{
    computer.Close();
}

return;

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static List<Reading> Read(Computer computer, BoardProfile? profile, bool showAll)
{
    var readings = new List<Reading>();
    foreach (IHardware hardware in computer.Hardware)
    {
        hardware.Update();
        foreach (IHardware sub in hardware.SubHardware)
        {
            sub.Update();
            bool isSuperIo = sub.HardwareType == HardwareType.SuperIO;
            foreach (ISensor sensor in sub.Sensors)
                Add(readings, sub.Name, sensor, isSuperIo ? profile : null, showAll);
        }

        foreach (ISensor sensor in hardware.Sensors)
            Add(readings, hardware.Name, sensor, null, showAll);
    }

    return readings;
}

static void Add(List<Reading> readings, string source, ISensor sensor, BoardProfile? profile, bool showAll)
{
    if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Voltage or SensorType.Control))
        return;

    string? boardLabel = sensor.SensorType switch
    {
        SensorType.Temperature => profile?.Temperature(sensor.Index),
        SensorType.Fan => profile?.Fan(sensor.Index),
        SensorType.Voltage => profile?.Voltage(sensor.Index),
        _ => null,
    };

    // With a board profile, only show the channels BIOSTAR actually wired up (unless --all).
    if (profile is not null && boardLabel is null && sensor.SensorType != SensorType.Control && !showAll)
        return;

    float? value = sensor.Value;
    string note = "";
    if (value is not null && sensor.SensorType == SensorType.Voltage && profile?.VoltageScale(sensor.Index) is double scale)
    {
        note = $"pino {value:0.000} V × {scale:0.###}";
        value = (float)(value.Value * scale);
    }

    readings.Add(new Reading(source, sensor.SensorType, boardLabel ?? sensor.Name, value, note));
}

static void Print(List<Reading> readings)
{
    foreach (var group in readings.GroupBy(r => r.Type).OrderBy(g => g.Key))
    {
        Console.WriteLine($"\n== {Title(group.Key)}");
        foreach (Reading r in group)
        {
            string value = r.Value is null ? "—" : $"{r.Value.Value.ToString(Format(r.Type))} {Unit(r.Type)}";
            Console.WriteLine($"  {r.Label,-28} {value,12}   [{r.Source}] {r.Note}");
        }
    }

    if (readings.Count == 0)
        Console.WriteLine("\nNenhum sensor encontrado.");
}

static string Title(SensorType type) => type switch
{
    SensorType.Temperature => "Temperaturas",
    SensorType.Fan => "Fans",
    SensorType.Control => "Controle de fan (PWM)",
    SensorType.Voltage => "Tensões",
    _ => type.ToString(),
};

static string Unit(SensorType type) => type switch
{
    SensorType.Temperature => "°C",
    SensorType.Fan => "RPM",
    SensorType.Control => "%",
    SensorType.Voltage => "V",
    _ => "",
};

static string Format(SensorType type) => type == SensorType.Voltage ? "0.000" : "0";

internal sealed record Reading(string Source, SensorType Type, string Label, float? Value, string Note);
