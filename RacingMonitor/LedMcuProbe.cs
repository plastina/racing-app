using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.Windows.Driver;

namespace RacingMonitor;

/// <summary>
/// Probe of BIOSTAR's "LED MCU" (see docs/LED_PROTOCOL.md).
/// <c>--leds</c> and <c>--scan</c> only read. <c>--led-write-test</c> is the single mode that writes: it changes
/// the 12V colour registers temporarily and always restores the original bytes.
/// </summary>
public static class LedMcuProbe
{
    // LED_IIC1..4 default 0x60 in 8-bit form (the fallback MCU the app uses when 0x58 is absent).
    private const byte McuAddress = 0x30;

    private static readonly (byte Register, string Meaning)[] Registers =
    {
        (0x97, "12V: liga/desliga (bit1)"),
        (0x9C, "12V: R"),
        (0x9D, "12V: G"),
        (0x9E, "12V: B"),
        (0x9F, "12V: modo (0x80 + código, bit4 preservado)"),
        (0xA4, "5V: aplicar (app grava 0x01)"),
        (0xA5, "5V: liga/desliga (bit7)"),
        (0xA6, "5V: modo (0x80 + modo)"),
        (0xA7, "5V: R"),
        (0xA8, "5V: G"),
        (0xA9, "5V: B"),
    };

    public static int Run(string[] args)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("O teste de LEDs precisa ser executado como administrador.");
            return 1;
        }

        // RAMSPDToolkit-NDD ships no driver of its own: LibreHardwareMonitor registers its PawnIO bridge
        // (with the SmbusPIIX4 module) and detects the SMBuses when memory monitoring is enabled.
        var computer = new Computer { IsMemoryEnabled = true };
        computer.Open();

        try
        {
            Console.WriteLine($"Driver do RAMSPDToolkit: {DriverManager.DriverImplementation}");
            PrintMemoryHardware(computer);
            IReadOnlyList<SMBusInterface> buses = SMBusManager.RegisteredSMBuses;

            Console.WriteLine($"Barramentos SMBus detectados: {buses.Count}");
            for (int i = 0; i < buses.Count; i++)
                Console.WriteLine($"  [{i}] {Describe(buses[i])}");

            if (args.Contains("--scan-ram"))
                return ScanRamRgb(buses);

            if (args.Contains("--scan"))
                return ScanAuxBus(buses);

            if (args.Contains("--led-write-test"))
                return WriteTestTwoPhases(buses);

            SMBusInterface? bus = SelectBus(buses, args, out string reason);
            if (bus is null)
            {
                Console.WriteLine($"\nNenhuma leitura feita: {reason}");
                return 2;
            }

            Console.WriteLine($"\nLendo LED MCU em 0x{McuAddress:X2} no barramento: {Describe(bus)} ({reason})");
            return ReadRegisters(bus);
        }
        finally
        {
            computer.Close();
        }
    }

    private static SMBusInterface? SelectBus(IReadOnlyList<SMBusInterface> buses, string[] args, out string reason)
    {
        int i = Array.IndexOf(args, "--led-bus");
        if (i >= 0 && i + 1 < args.Length)
        {
            if (int.TryParse(args[i + 1], out int index) && index >= 0 && index < buses.Count)
            {
                reason = "escolhido com --led-bus";
                return buses[index];
            }

            reason = $"índice inválido em --led-bus ({args[i + 1]}).";
            return null;
        }

        // Only pick a bus automatically when its name unambiguously identifies the AMD auxiliary SMBus.
        var aux = buses.Where(b => (b.DeviceName ?? "").Contains("B20", StringComparison.OrdinalIgnoreCase)).ToList();
        if (aux.Count == 1)
        {
            reason = "identificado como 0x0B20 pelo nome";
            return aux[0];
        }

        reason = "não foi possível identificar o barramento 0x0B20 com certeza. " +
                 "Veja a lista acima e rode de novo com --led-bus <índice>.";
        return null;
    }

    /// <summary>
    /// Lists which 7-bit addresses ACK on the AMD auxiliary SMBus (0x0B20). PawnIO's SmbusPIIX4 module maps
    /// port 1 to that controller. Uses only SMBus "receive byte" (no command byte is sent), never writes.
    /// </summary>
    /// <summary>PawnIO's SmbusPIIX4 module maps port 1 to the AMD auxiliary SMBus controller (0x0B20).</summary>
    private static SMBusInterface? FindAuxBus(IReadOnlyList<SMBusInterface> buses) => FindPiix4Bus(buses, 1);

    /// <summary>Port 0 is the primary SMBus (0x0B00), where the DIMMs (SPD and RGB controllers) live.</summary>
    private static SMBusInterface? FindPiix4Bus(IReadOnlyList<SMBusInterface> buses, int port)
    {
        var matches = buses.Where(b => b is SMBusPawnIO { PawnIOSMBusIdentifier: RAMSPDToolkit.I2CSMBus.Interop.PawnIO.PawnIOSMBusIdentifier.Piix4 }
                                       && b.PortID == port).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Lists which DRAM RGB controller addresses ACK on the primary SMBus. Only 0x70–0x77 (the ENE DRAM range
    /// RCGTII probes) are touched, with SMBus "receive byte" only: the SPD EEPROMs at 0x50–0x57 are never addressed.
    /// </summary>
    private static int ScanRamRgb(IReadOnlyList<SMBusInterface> buses)
    {
        SMBusInterface? main = FindPiix4Bus(buses, 0);
        if (main is null)
        {
            Console.WriteLine("\nVarredura cancelada: não encontrei exatamente um barramento PIIX4 na porta 0 (principal 0x0B00).");
            return 2;
        }

        Console.WriteLine($"\nVarrendo (só leitura, receive byte) 0x70–0x77 em: {Describe(main)}");
        var found = new List<string>();
        for (byte address = 0x70; address <= 0x77; address++)
        {
            int value = main.i2c_smbus_read_byte(address);
            if (value >= 0)
                found.Add($"0x{address:X2} (8 bits 0x{address << 1:X2}) respondeu, byte = 0x{value:X2}");
        }

        Console.WriteLine(found.Count == 0 ? "Nenhum controlador de RGB de memória respondeu." : $"Responderam: {found.Count}");
        foreach (string line in found)
            Console.WriteLine($"  {line}");
        return 0;
    }

    private static int ScanAuxBus(IReadOnlyList<SMBusInterface> buses)
    {
        SMBusInterface? aux = FindAuxBus(buses);
        if (aux is null)
        {
            Console.WriteLine("\nVarredura cancelada: não encontrei exatamente um barramento PIIX4 na porta 1 (auxiliar 0x0B20).");
            return 2;
        }

        Console.WriteLine($"\nVarrendo (só leitura, receive byte) 0x08–0x77 em: {Describe(aux)}");
        var found = new List<string>();
        for (byte address = 0x08; address <= 0x77; address++)
        {
            int value = aux.i2c_smbus_read_byte(address);
            if (value >= 0)
                found.Add($"0x{address:X2} (8 bits 0x{address << 1:X2}) respondeu, byte = 0x{value:X2}");
        }

        Console.WriteLine(found.Count == 0 ? "Nenhum dispositivo respondeu." : $"Dispositivos que responderam: {found.Count}");
        foreach (string line in found)
            Console.WriteLine($"  {line}");
        return 0;
    }

    /// <summary>
    /// Write test in two phases, each one turning a single header red for a few seconds and then restoring the
    /// exact original colour bytes:
    /// 1. 12V: colour 0x9C–0x9E, then rewrite 0x9F with the value it already had (possible implicit "apply").
    /// 2. 5V: colour 0xA7–0xA9, then 0x01 to 0xA4 ("apply", as RCGTII does).
    /// A phase only runs if its header reads as enabled and in the static mode seen before.
    /// </summary>
    private static int WriteTestTwoPhases(IReadOnlyList<SMBusInterface> buses)
    {
        SMBusInterface? bus = FindAuxBus(buses);
        if (bus is null)
        {
            Console.WriteLine("\nTeste cancelado: não encontrei exatamente um barramento PIIX4 na porta 1 (auxiliar 0x0B20).");
            return 2;
        }

        Console.WriteLine($"\nTeste de escrita em duas fases em 0x{McuAddress:X2} no barramento: {Describe(bus)}");

        // Ctrl+C must not skip the restores below.
        ConsoleCancelEventHandler keepRunning = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += keepRunning;
        try
        {
            int enable12 = bus.i2c_smbus_read_byte_data(McuAddress, 0x97);
            int mode12 = bus.i2c_smbus_read_byte_data(McuAddress, 0x9F);
            Console.WriteLine($"\n=== FASE 1: conector 12V (0x97=0x{enable12:X2}, 0x9F=0x{mode12:X2}) ===");
            if (enable12 < 0 || mode12 != 0x81 || (enable12 & 0x02) == 0)
            {
                Console.WriteLine("Fase 1 cancelada: 12V não está ligado em modo estático (0x9F=0x81). Nada foi escrito.");
                return 3;
            }

            int rc = ColorPhase(bus, new byte[] { 0x9C, 0x9D, 0x9E }, new byte[] { 0xFF, 0x00, 0x00 }, "VERMELHO",
                () => bus.i2c_smbus_write_byte_data(McuAddress, 0x9F, (byte)mode12), "regrava 0x9F=0x81");
            if (rc != 0)
                return rc;

            int enable5 = bus.i2c_smbus_read_byte_data(McuAddress, 0xA5);
            int mode5 = bus.i2c_smbus_read_byte_data(McuAddress, 0xA6);
            Console.WriteLine($"\n=== FASE 2: conector 5V (0xA5=0x{enable5:X2}, 0xA6=0x{mode5:X2}) ===");
            if (enable5 < 0 || mode5 != 0x89 || (enable5 & 0x80) == 0)
            {
                Console.WriteLine("Fase 2 cancelada: 5V não está ligado no modo lido antes (0xA6=0x89). Nada foi escrito.");
                return 3;
            }

            // A different colour per phase tells which header drives the visible LEDs without relying on timing.
            Thread.Sleep(2000);
            return ColorPhase(bus, new byte[] { 0xA7, 0xA8, 0xA9 }, new byte[] { 0x00, 0xFF, 0x00 }, "VERDE",
                () => bus.i2c_smbus_write_byte_data(McuAddress, 0xA4, 0x01), "aplicar 0xA4=0x01");
        }
        finally
        {
            Console.CancelKeyPress -= keepRunning;
        }
    }

    /// <summary><paramref name="test"/> colour for a few seconds on <paramref name="colorRegs"/>, then the original bytes; returns 0 only if restored.</summary>
    private static int ColorPhase(SMBusInterface bus, byte[] colorRegs, byte[] test, string testName, Func<int> apply, string applyLabel)
    {
        const int holdMs = 5000;

        int[] original = colorRegs.Select(r => bus.i2c_smbus_read_byte_data(McuAddress, r)).ToArray();
        if (original.Any(v => v < 0))
        {
            Console.WriteLine("Leitura da cor falhou. Nada foi escrito.");
            return 3;
        }

        Console.WriteLine($"Cor atual: {Hex(original)}");
        bool restored = true;
        try
        {
            for (int i = 0; i < colorRegs.Length; i++)
            {
                int rc = bus.i2c_smbus_write_byte_data(McuAddress, colorRegs[i], test[i]);
                if (rc < 0)
                {
                    Console.WriteLine($"Escrita em 0x{colorRegs[i]:X2} falhou (código 0x{rc:X8}); restaurando.");
                    return 4;
                }
            }

            int applied = apply();
            int[] after = colorRegs.Select(r => bus.i2c_smbus_read_byte_data(McuAddress, r)).ToArray();
            Console.WriteLine($"Escrito {testName} + {applyLabel} ({(applied < 0 ? "falhou" : "ok")}); lido de volta: {Hex(after)}");
            Console.WriteLine($">>> OLHE OS LEDs AGORA: esta fase deixa o conector {testName} por {holdMs / 1000} segundos...");
            Thread.Sleep(holdMs);
        }
        finally
        {
            for (int i = 0; i < colorRegs.Length; i++)
                restored &= bus.i2c_smbus_write_byte_data(McuAddress, colorRegs[i], (byte)original[i]) >= 0;
            restored &= apply() >= 0;

            int[] check = colorRegs.Select(r => bus.i2c_smbus_read_byte_data(McuAddress, r)).ToArray();
            restored &= check.SequenceEqual(original);
            Console.WriteLine($"Restaurado: {Hex(check)} " +
                              (restored ? "(igual ao original)" : "(ATENÇÃO: diferente do original — rode o teste de novo ou reinicie o PC)"));
        }

        return restored ? 0 : 5;
    }

    private static string Hex(int[] rgb) => "#" + string.Concat(rgb.Select(v => v < 0 ? "??" : v.ToString("X2")));

    private static int ReadRegisters(SMBusInterface bus)
    {
        var values = new Dictionary<byte, int>();

        // Presence check first: if 0x97 does not answer, stop instead of issuing more transactions.
        int first = bus.i2c_smbus_read_byte_data(McuAddress, Registers[0].Register);
        if (first < 0)
        {
            string meaning = first == unchecked((int)0x800701B1) ? "dispositivo não respondeu" : "erro";
            Console.WriteLine($"Sem resposta em 0x{McuAddress:X2} ({meaning}, código 0x{first:X8}). Nenhum outro registrador foi lido.");
            return 3;
        }

        values[Registers[0].Register] = first;
        foreach (var (register, _) in Registers.Skip(1))
            values[register] = bus.i2c_smbus_read_byte_data(McuAddress, register);

        Console.WriteLine();
        foreach (var (register, meaning) in Registers)
        {
            int v = values[register];
            string text = v < 0 ? $"erro {v}" : $"0x{v:X2}  {Convert.ToString(v, 2).PadLeft(8, '0')}";
            Console.WriteLine($"  0x{register:X2}  {text,-20} {meaning}");
        }

        Console.WriteLine("\nInterpretação (segundo docs/LED_PROTOCOL.md, ainda não confirmada):");
        Console.WriteLine($"  12V: {OnOff(values[0x97], 0x02)}  cor {Rgb(values, 0x9C)}  modo {Mode(values[0x9F])}");
        Console.WriteLine($"  5V:  {OnOff(values[0xA5], 0x80)}  cor {Rgb(values, 0xA7)}  modo {Mode(values[0xA6])}");
        return 0;
    }

    /// <summary>
    /// Shows what LibreHardwareMonitor already read from the DIMM SPD EEPROMs while opening. If DIMMs appear here,
    /// SMBus reads through PawnIO work, without this probe issuing any extra transaction.
    /// </summary>
    private static void PrintMemoryHardware(Computer computer)
    {
        Console.WriteLine("Memória vista pelo LibreHardwareMonitor (leitura de SPD feita por ele ao abrir):");
        bool any = false;
        foreach (IHardware hardware in computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory))
        {
            hardware.Update();
            any = true;
            string sensors = string.Join(", ", hardware.Sensors.Take(4).Select(s => $"{s.Name}={s.Value?.ToString("0.#") ?? "—"}"));
            Console.WriteLine($"  {hardware.Name}  [{sensors}]");
        }

        if (!any)
            Console.WriteLine("  (nenhuma)");
        Console.WriteLine();
    }

    private static string Describe(SMBusInterface bus)
    {
        string kind = bus is SMBusPawnIO pawn ? $"PawnIO/{pawn.PawnIOSMBusIdentifier}" : bus.GetType().Name;
        return $"{kind}  \"{bus.DeviceName}\"  porta {bus.PortID}  PCI {bus.PCIVendor:X4}:{bus.PCIDevice:X4}";
    }

    private static string OnOff(int value, int mask) =>
        value < 0 ? "(erro de leitura)" : (value & mask) != 0 ? "ligado" : "desligado";

    private static string Rgb(Dictionary<byte, int> values, byte red)
    {
        int r = values[red], g = values[(byte)(red + 1)], b = values[(byte)(red + 2)];
        return r < 0 || g < 0 || b < 0 ? "(erro de leitura)" : $"#{r:X2}{g:X2}{b:X2}";
    }

    private static string Mode(int value) =>
        value < 0 ? "(erro de leitura)" : $"0x{value & 0x7F:X2}{((value & 0x80) != 0 ? " (bit7 ativo)" : "")}";
}
