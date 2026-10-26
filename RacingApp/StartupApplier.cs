using System.IO;
using System.Text;

namespace RacingApp;

/// <summary>
/// Headless mode (<c>RacingApp.exe --apply-saved</c>): re-applies the saved lighting and exits. Used by the
/// logon task, because the LED chips fall back to the BIOS configuration after a power cycle.
/// </summary>
public static class StartupApplier
{
    private const int Attempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    public static string LogPath { get; } = Path.Combine(LightingSettings.FolderPath, "last-startup.log");

    public static async Task<int> RunAsync()
    {
        var log = new List<string> { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} --apply-saved" };
        try
        {
            LightingSettings? settings = LightingSettings.Load();
            if (settings is null || (!settings.HasBoard && !settings.HasRam))
            {
                log.Add("Nenhuma configuração salva; nada a aplicar.");
                return 0;
            }

            using var hardware = new HardwareService(Path.Combine(AppContext.BaseDirectory, "BoardConfig.ini"));
            if (!hardware.HasHardwareAccess)
            {
                log.Add("Sem administrador ou PawnIO; nada foi aplicado.");
                return 1;
            }

            bool boardDone = !settings.HasBoard, ramDone = !settings.HasRam;
            for (int attempt = 1; attempt <= Attempts && !(boardDone && ramDone); attempt++)
            {
                if (attempt > 1)
                    await Task.Delay(RetryDelay);

                if (!boardDone && hardware.FindAuxBus() is { } aux)
                {
                    Rgb color = LightingSettings.ParseColor(settings.BoardColor);
                    boardDone = new LedController(aux, hardware.Gate).Apply(color, settings.BoardMode, settings.BoardSpeed);
                    log.Add($"Tentativa {attempt}: placa {color} modo 0x{settings.BoardMode:X2} velocidade 0x{settings.BoardSpeed:X2} → {(boardDone ? "ok" : "falhou")}");
                }

                if (!ramDone && hardware.FindMainBus() is { } main)
                {
                    var ram = new RamLedController(main, hardware.Gate);
                    RamLedState? state = ram.ReadState();
                    if (state?.ChipName != RamLedController.SupportedChip)
                    {
                        log.Add($"Tentativa {attempt}: memórias com chip \"{state?.ChipName ?? "?"}\"; não aplicado.");
                        continue;
                    }

                    Rgb color = LightingSettings.ParseColor(settings.RamColor);
                    ramDone = ram.Apply(color, settings.RamMode);
                    log.Add($"Tentativa {attempt}: memórias {color} modo {settings.RamMode} → {(ramDone ? "ok" : "falhou")}");
                }
            }

            return boardDone && ramDone ? 0 : 2;
        }
        catch (Exception ex)
        {
            log.Add($"Erro: {ex}");
            return 3;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(LightingSettings.FolderPath);
                File.WriteAllLines(LogPath, log, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging is best effort; the lighting result does not depend on it.
            }
        }
    }
}
