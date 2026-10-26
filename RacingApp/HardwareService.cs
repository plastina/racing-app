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

    /// <summary>
    /// The Super I/O's PWM outputs that LibreHardwareMonitor can drive. With a board profile only the headers
    /// BIOSTAR labels are listed; control #n drives the header whose tachometer is fan #n.
    /// </summary>
    public IReadOnlyList<FanChannel> FindFanControls()
    {
        var channels = new List<FanChannel>();
        lock (Gate)
        {
            foreach (IHardware sub in _computer.Hardware.SelectMany(h => h.SubHardware).Where(s => s.HardwareType == HardwareType.SuperIO))
            {
                foreach (ISensor sensor in sub.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control is not null))
                {
                    string? label = Profile?.Fan(sensor.Index);
                    if (Profile is not null && label is null)
                        continue;

                    ISensor? fan = sub.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == sensor.Index);
                    label ??= fan?.Name ?? sensor.Name;
                    bool isCpu = label.Contains("CPU", StringComparison.OrdinalIgnoreCase);
                    channels.Add(new FanChannel(sensor.Index, label, sensor.Identifier.ToString(), fan?.Identifier.ToString(),
                                                isCpu, sensor.Control));
                }
            }
        }

        return channels;
    }

    /// <summary>Sets a header's duty in percent, or hands it back to the BIOS with null.</summary>
    public void SetFanDuty(FanChannel channel, float? percent)
    {
        lock (Gate)
        {
            if (percent is float value)
                channel.Control.SetSoftware(Math.Clamp(value, channel.Control.MinSoftwareValue, channel.Control.MaxSoftwareValue));
            else
                channel.Control.SetDefault();
        }
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
            // Duty of the PWM output that drives fan #n (read by the fan control panel, not listed as a sensor).
            SensorType.Control => Profile?.Fan(sensor.Index),
            _ => null,
        };

        if (sensor.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Voltage or SensorType.Control))
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
