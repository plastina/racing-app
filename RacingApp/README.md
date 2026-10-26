# Racing Studio (RacingApp)

App WPF para a BIOSTAR B450GT, substituto próprio do RACING GT EVO, sem o driver `BS_RVSIO64.sys`:
usa **LibreHardwareMonitorLib 0.9.6 + PawnIO**.

## Recursos

- **Monitoramento** (atualiza a cada 1 s): temperaturas, fans e tensões da placa com os nomes e divisores
  do `Config.ini` da BIOSTAR (via `RacingMonitor/BoardProfile.cs`), mais as temperaturas da CPU.
- **LEDs do conector 5V (ARGB)**: lê cor e efeito atuais ao abrir; sliders R/G/B, campo hex, cores rápidas
  e **efeitos** (estático, respiração, piscar, arco-íris, onda, meteoro, relâmpago, céu estrelado, aurora),
  com **velocidade**; **Aplicar nos LEDs** e **Restaurar original**. Escreve só a cor (`0xA7`–`0xA9`), o modo
  (`0xA6`), a velocidade (`0xAB`) e o "aplicar" (`0xA4`) do controlador `0x30` no SMBus auxiliar, conforme
  [../RacingMonitor/docs/LED_PROTOCOL.md](../RacingMonitor/docs/LED_PROTOCOL.md).

## Requisitos

- Windows, .NET 8 Desktop Runtime, **PawnIO** instalado (`winget install --id namazso.PawnIO --exact --source winget`).
- O executável pede **administrador** ao abrir (manifesto `requireAdministrator`).

## Compilar e abrir

```powershell
dotnet build REA.sln -c Release
.\RacingApp\bin\Release\net8.0-windows\win-x64\RacingApp.exe
```

## Arquitetura

| Arquivo | Papel |
|---|---|
| `HardwareService.cs` | Abre o `Computer` do LHM (placa, CPU e memória — esta última ativa a ponte SMBus do PawnIO), lê sensores e localiza o SMBus auxiliar (PawnIO PIIX4 porta 1). |
| `LedController.cs` | Lê o estado do conector 5V e aplica cores, verificando lendo de volta. |
| `MainWindow.xaml(.cs)` | Interface; leituras e escritas rodam fora da thread da UI. |

Todo acesso ao hardware passa por `HardwareService.Gate`, para que atualizações de sensores e escritas nos
LEDs nunca intercalem transações no SMBus.

## Ainda não implementado

Efeito "Music" (depende do áudio do PC); brilho (registrador `0xAA`, 1–16, já identificado);
conector 12V (nada visível ligado nesta máquina); sincronização com as memórias RGB (controlador ENE).
