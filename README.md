# racing-app

A custom replacement for BIOSTAR's **RACING GT EVO** utility for the BIOSTAR B450GT motherboard.
It works without the vendor's `BS_RVSIO64.sys` driver, using
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 with the
[PawnIO](https://pawnio.eu) driver instead.

## Projects

| Project | Description |
|---|---|
| [`RacingApp`](RacingApp) | WPF app ("Racing Studio"): live monitoring of board temperatures, fans and voltages, plus control of the 5V ARGB LED header (colors and effects). |
| [`RacingMonitor`](RacingMonitor) | Read-only console prototype, probing tools and the LED protocol documentation ([docs/LED_PROTOCOL.md](RacingMonitor/docs/LED_PROTOCOL.md)). |

## Requirements

- Windows with the .NET 8 SDK / Desktop Runtime
- PawnIO installed: `winget install --id namazso.PawnIO --exact --source winget`
- Administrator privileges (the app manifest requests elevation)

## Build and run

```powershell
dotnet build REA.sln -c Release
.\RacingApp\bin\Release\net8.0-windows\win-x64\RacingApp.exe
```

See each project's README for details (currently written in Portuguese).

## Disclaimer

This writes to hardware controllers over SMBus. Use at your own risk.
