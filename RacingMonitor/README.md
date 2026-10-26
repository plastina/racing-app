# RacingMonitor

Protótipo **somente leitura** de monitoramento da placa BIOSTAR B450GT (e outras do
`Config.ini` da BIOSTAR), usando [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
0.9.6 com o driver **PawnIO** no lugar do `BS_RVSIO64.sys` da BIOSTAR.

## Requisitos

1. .NET 8 SDK.
2. **PawnIO** instalado — baixe o instalador oficial em <https://pawnio.eu>.
   Sem ele, o LibreHardwareMonitor não consegue ler o Super I/O (fans, tensões, temperaturas
   da placa) nem os MSRs da CPU.
3. Executar como **administrador**.

## Como rodar

```powershell
dotnet build -c Release
# num terminal aberto como administrador:
.\bin\Release\net8.0-windows\win-x64\RacingMonitor.exe
```

Opções:

| Opção | Efeito |
|---|---|
| `--once` | Faz uma leitura e sai |
| `--all` | Mostra também os canais do Super I/O que a BIOSTAR não usa na placa |
| `--board <nome>` | Força a seção do `BoardConfig.ini` (padrão: modelo lido do SMBIOS) |
| `--ini <caminho>` | Usa outro arquivo de perfil |
| `--interval <ms>` | Intervalo de atualização (mínimo 250 ms) |

## Teste dos LEDs (somente leitura)

```powershell
# 1) lista os barramentos SMBus que o PawnIO expõe (não lê nada se não identificar 0x0B20 pelo nome)
.\bin\Release\net8.0-windows\win-x64\RacingMonitor.exe --leds
# 2) lê os registradores do LED MCU (0x30) no barramento escolhido da lista (na B450GT: 1 = auxiliar 0x0B20)
.\bin\Release\net8.0-windows\win-x64\RacingMonitor.exe --leds --led-bus <índice>
```

`--leds` só faz transações SMBus "read byte data" no LED MCU `0x30`; se `0x97` não responder, para sem
ler os demais registradores. `--leds --scan` lista, só lendo, quem responde no SMBus auxiliar.

`--leds --led-write-test` (ou `testar-cor-2fases.cmd`) é o **único modo que escreve**: confere se cada conector está
ligado no modo estático lido antes, faz duas fases (12V: cor + regrava `0x9F`; 5V: cor + `0xA4`=1), 12V vermelho e 5V verde por 5 s cada, e restaura os bytes
originais, inclusive se houver erro ou Ctrl+C. O protocolo está em [docs/LED_PROTOCOL.md](docs/LED_PROTOCOL.md).

## Como os nomes são mapeados

`BoardConfig.ini` é uma cópia do `Config.ini` do RACING GT EVO. Para a placa detectada:

- `TEMPn` → sensor de temperatura do Super I/O de índice `n-1` (`Temperature #n` no LHM)
- `FANn` → fan de índice `n-1` (`Fan #n`)
- `VINn` → tensão de índice `n` (`Voltage #n+1`)
- `VINn_R1` / `VINn_R2` → divisor resistivo: `V = Vpino × (R1 + R2) / R2`
  (ex.: na B450GT, +12 V usa ×6 e +5 V usa ×2,47)

Canais sem nome no `.ini` ficam ocultos (use `--all` para vê-los).

> Ainda não verificado com PawnIO instalado: se o LibreHardwareMonitor já aplicar uma correção
> própria às tensões desta placa, o divisor do `.ini` seria aplicado duas vezes. A coluna
> "pino … V × …" mostra o valor bruto para conferir com a BIOS.
