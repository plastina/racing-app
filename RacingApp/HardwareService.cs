using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using RacingMonitor;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.I2CSMBus.Interop.PawnIO;

namespace RacingApp;

public sealed record SensorReading(string Key, SensorType Type, string Label, float? Value);

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> and the board profile. Every hardware access in the app
/// (sensor updates and LED writes) must hold <see cref="Gate"/> so SMBus transactions never interleave.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private readonly Computer _computer;

    public HardwareService(string iniPath)
    {
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        IsPawnIoInstalled = PawnIo.IsInstalled;

        // Memory monitoring makes LibreHardwareMonitor register its PawnIO SMBus bridge (needed for the LEDs).
        _computer = new Computer { IsMotherboardEnabled = true, IsCpuEnabled = true, IsMemoryEnabled = true };
        _computer.Open();

        BoardManufacturer = _computer.SMBios.Board?.ManufacturerName?.Trim() ?? "";
        BoardName = _computer.SMBios.Board?.ProductName?.Trim() ?? "";
        Profile = BoardProfile.Load(iniPath, BoardName);
    }

    public object Gate { get; } = new();

    public bool IsAdmin { get; }

    public bool IsPawnIoInstalled { get; }

    public bool HasHardwareAccess => IsAdmin && IsPawnIoInstalled;

    public string BoardManufacturer { get; }

    public string BoardName { get; }

    public BoardProfile? Profile { get; }

    public IReadOnlyList<SensorReading> ReadSensors()
    {
        var readings = new List<SensorReading>();
        lock (Gate)
        {
            foreach (IHardware hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.Memory)
                    continue;

                hardware.Update();
                if (hardware.HardwareType == HardwareType.Cpu)
                {
                    foreach (ISensor sensor in hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature))
                        readings.Add(new SensorReading(sensor.Identifier.ToString(), sensor.SensorType, $"CPU {sensor.Name}", sensor.Value));
                }

                foreach (IHardware sub in hardware.SubHardware.Where(s => s.HardwareType == HardwareType.SuperIO))
                {
                    sub.Update();
                    foreach (ISensor sensor in sub.Sensors)
                        AddSuperIo(readings, sensor);
                }
            }
        }

        return readings;
    }

    /// <summary>PawnIO's SmbusPIIX4 module maps port 1 to the AMD auxiliary SMBus controller (0x0B20).</summary>
    public SMBusInterface? FindAuxBus() => FindPiix4Bus(1);

    /// <summary>Port 0 is the primary SMBus (0x0B00), where the DIMMs (SPD and RGB controllers) live.</summary>
    public SMBusInterface? FindMainBus() => FindPiix4Bus(0);

    private SMBusInterface? FindPiix4Bus(int port)
    {
        lock (Gate)
        {
            var matches = SMBusManager.RegisteredSMBuses
                .Where(b => b is SMBusPawnIO { PawnIOSMBusIdentifier: PawnIOSMBusIdentifier.Piix4 } && b.PortID == port)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }

    public void Dispose()
    {
        lock (Gate)
            _computer.Close();
    }

    private void AddSuperIo(List<SensorReading> readings, ISensor sensor)
    {
        string? label = sensor.SensorType switch
        {
            SensorType.Temperature => Profile?.Temperature(sensor.Index),
            SensorType.Fan => Profile?.Fan(sensor.Index),
            SensorType.Voltage => Profile?.Voltage(sensor.Index),
            _ => null,
        };

        if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Voltage))
            return;

        // With a board profile, only show the channels BIOSTAR actually wired up.
        if (Profile is not null && label is null)
            return;

        float? value = sensor.Value;
        if (value is not null && sensor.SensorType == SensorType.Voltage && Profile?.VoltageScale(sensor.Index) is double scale)
            value = (float)(value.Value * scale);

        readings.Add(new SensorReading(sensor.Identifier.ToString(), sensor.SensorType, label ?? sensor.Name, value));
    }
}
