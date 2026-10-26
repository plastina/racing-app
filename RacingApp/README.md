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

- **Interface de iluminação**: abas "Placa (5V)" e "Memórias RAM", cada uma com seu `LedEditor`
  (cor, efeito e, na placa, velocidade), e a opção **Sincronizar memórias com a placa** (padrão): as
  memórias seguem a cor da placa e o efeito equivalente (`LedEffect.RamMode`). Um único Aplicar/Restaurar.
- **Memórias RAM**: cor e efeito (estático, respiração, piscar, ciclo de cores, respiração/piscar coloridos,
  arco-íris, perseguição, onda colorida — todos conferidos visualmente) nos dois pentes pelo controlador ENE
  `AUDA0-E6K5-0101` em `0x77` do SMBus principal
  (`RamLedController.cs`). Escreve só as cores do efeito (`0x8160`–`0x8177`), o modo (`0x8021`) e o
  "aplicar" (`0x80A0`); nunca endereça a EEPROM SPD (`0x50`–`0x57`). Só é habilitado se o chip for
  exatamente esse modelo; "Restaurar original" volta ao modo e às cores lidos ao abrir o app.

- **Efeitos do app** (marcados "(app)" na mesma lista de efeitos de cada aba; `SoftwareEffects.cs`):
  calculados pelo próprio app e enviados ~20 vezes por segundo (só os bytes que mudaram; sem leitura de
  volta) — **cor pela temperatura** da CPU (azul → verde → vermelho na faixa escolhida), **gradiente** de
  2–3 cores (nas memórias, cada LED defasado) e, nos 8 LEDs de cada pente, **luz correndo**, **vai e vem**
  e **preenchimento**. Ao escolher um deles, o `LedEditor` mostra só os controles que ele usa. Placa e
  memórias podem rodar efeitos diferentes; sincronizado, as memórias seguem o mesmo efeito da placa.
  Aplicar de novo troca o efeito; Restaurar para. Só funcionam com o app aberto e não entram no logon
  (ao fechar, volta a última configuração salva).
- **Bandeja do Windows** (`TrayIcon.cs`): o botão fechar só esconde a janela; o ícone na área de
  notificação reabre (clique) ou sai de vez (botão direito → Sair). Ao sair ou ao desligar o Windows, um
  efeito do app em andamento é parado e a última configuração salva volta para os LEDs.
- **Instância única** (`App.xaml.cs`): abrir o app de novo só traz a janela que já está rodando.
- **Abrir com o Windows**: cada Aplicar/Restaurar salva em `%AppData%\RacingStudio\settings.json`
  (`LightingSettings.cs`) o estado das abas (efeito, cores, ciclo, faixa de temperatura...) e os valores
  gravados nos chips. A opção "Abrir o Racing Studio com o Windows (na bandeja)" cria/remove a tarefa agendada
  **"Racing Studio - aplicar iluminação"** (`StartupTask.cs`, definida em XML: ao fazer logon do usuário,
  sessão interativa, privilégios mais altos, 10 s de atraso, sem limite de 3 dias e sem condições de bateria),
  que roda `RacingApp.exe --tray`: abre escondido na bandeja e reaplica a última configuração, inclusive efeitos
  do app. Uma entrada comum em "Executar" não serve, porque o app exige administrador.
- `RacingApp.exe --apply-saved` continua disponível: sem janela, reaplica só os valores dos chips e sai
  (log em `%AppData%\RacingStudio\last-startup.log`, `StartupApplier.cs`).
- **Ícone**: `app.ico` (anel arco-íris com centro lilás), gerado por script.

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
conector 12V (nada visível ligado nesta máquina); velocidade e brilho dos efeitos das memórias RAM.
