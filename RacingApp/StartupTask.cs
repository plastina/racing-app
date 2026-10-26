using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace RacingApp;

/// <summary>
/// Logon task that opens Racing Studio in the notification area (<c>RacingApp.exe --tray</c>) with highest
/// privileges. A Run-key entry would not work: the app requires administrator rights, so it would prompt for UAC
/// on every logon or be skipped. The task is defined in XML because the schtasks switches cannot turn off the
/// default 3-day run limit or the battery conditions, which would stop an app that stays open.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "Racing Studio - aplicar iluminação";

    // Not AppContext.BaseDirectory: in the single-file release that is the extraction folder, and the exe is
    // named RacingStudio.exe.
    private static string ExePath => Environment.ProcessPath!;

    public static bool Exists() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Creates (or replaces) the task; returns an error message on failure.</summary>
    public static string? Create()
    {
        string user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Abre o Racing Studio na bandeja e reaplica a iluminação ao entrar no Windows.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(ExePath)}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Path.GetTempPath(), "racing-studio-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F", out string output) == 0 ? null : output;
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Deletes the task; returns an error message on failure.</summary>
    public static string? Delete() =>
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out string output) == 0 ? null : output;

    private static int RunSchtasks(string arguments, out string output)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using Process process = Process.Start(info)!;
        output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
        process.WaitForExit();
        return process.ExitCode;
    }
}
