# racing-app

A custom replacement for BIOSTAR's **RACING GT EVO** utility for the BIOSTAR B450GT motherboard.
It works without the vendor's `BS_RVSIO64.sys` driver, using
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 with the
[PawnIO](https://pawnio.eu) driver instead.

## Projects

| Project | Description |
|---|---|
| [`RacingApp`](RacingApp) | WPF app ("Racing Studio"): live monitoring of board temperatures, fans and voltages, fan speed control (fixed or temperature curve), plus control of the 5V ARGB LED header (colors and effects). |
| [`RacingMonitor`](RacingMonitor) | Read-only console prototype, probing tools and the LED protocol documentation ([docs/LED_PROTOCOL.md](RacingMonitor/docs/LED_PROTOCOL.md)). |

## Download

Grab `RacingStudio.exe` from the [latest release](https://github.com/plastina/racing-app/releases/latest). It is a
single self-contained file (no .NET install needed). It checks for a newer release every time it starts and, if
you accept, replaces itself (after checking the published SHA-256) and restarts.

Every push to `main` that touches the app publishes a new release
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)). The version is `<version.txt>.<run number>`; edit
[`version.txt`](version.txt) to move to 0.2, 1.0 and so on.

## Requirements

- Windows; the .NET 8 SDK only to build from source
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
