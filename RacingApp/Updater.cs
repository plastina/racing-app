using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RacingApp;

/// <summary>A release newer than the running app.</summary>
public sealed record UpdateInfo(Version Version, string ExeUrl, string ChecksumUrl);

/// <summary>
/// Self-update from the GitHub releases published by <c>.github/workflows/ci.yml</c> (asset <c>RacingStudio.exe</c>
/// plus its <c>.sha256</c>). The running exe is replaced in place: Windows lets a running exe be renamed but not
/// overwritten, so it becomes <c>.old</c> and the new one takes its name, and the logon task keeps pointing at it.
/// </summary>
public static class Updater
{
    private const string Repository = "plastina/racing-app";
    private const string ExeAsset = "RacingStudio.exe";
    private const string UpdatedFromArgument = "--updated-from";

    /// <summary>Stamped by the release workflow; 0.0.0 is a local build, which never updates itself.</summary>
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version!);

    // After CurrentVersion: static initializers run in declaration order and the User-Agent uses it.
    private static readonly HttpClient Http = CreateClient();

    public static bool IsReleaseBuild => CurrentVersion > new Version(0, 0, 0);

    private static string ExePath => Environment.ProcessPath!;
    private static string OldPath => ExePath + ".old";
    private static string NewPath => ExePath + ".new";

    /// <summary>Returns the latest release if it is newer than this one; throws if GitHub can't be reached.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsReleaseBuild)
            return null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Stream body = await Http.GetStreamAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
        JsonElement release = json.RootElement;

        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out Version? latest) || Normalize(latest) <= CurrentVersion)
            return null;

        string? exe = null, checksum = null;
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();
            if (name == ExeAsset)
                exe = url;
            else if (name == ExeAsset + ".sha256")
                checksum = url;
        }

        // A release still uploading its assets (or without a checksum) is skipped rather than trusted.
        return exe is null || checksum is null ? null : new UpdateInfo(Normalize(latest), exe, checksum);
    }

    /// <summary>Downloads the new exe, checks it against the published SHA-256 and swaps it in for the running one.</summary>
    public static async Task InstallAsync(UpdateInfo update)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        string expected = (await Http.GetStringAsync(update.ChecksumUrl, timeout.Token))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

        try
        {
            await using (FileStream file = File.Create(NewPath))
            await using (Stream download = await Http.GetStreamAsync(update.ExeUrl, timeout.Token))
                await download.CopyToAsync(file, timeout.Token);

            string actual;
            await using (FileStream file = File.OpenRead(NewPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("o arquivo baixado não confere com o checksum publicado.");
        }
        catch
        {
            File.Delete(NewPath);
            throw;
        }

        File.Delete(OldPath);
        File.Move(ExePath, OldPath);
        try
        {
            File.Move(NewPath, ExePath);
        }
        catch
        {
            File.Move(OldPath, ExePath);
            throw;
        }
    }

    /// <summary>Starts the updated exe, which waits for this process to exit before taking over.</summary>
    public static void StartUpdated() =>
        Process.Start(new ProcessStartInfo(ExePath, $"{UpdatedFromArgument} {Environment.ProcessId}") { UseShellExecute = true });

    /// <summary>
    /// Called first thing at startup. After an update, waits for the previous process (it still holds the
    /// single-instance mutex and is restoring the fans and lighting), then deletes the exe it left behind.
    /// </summary>
    public static void FinishPendingUpdate(string[] args)
    {
        int index = Array.IndexOf(args, UpdatedFromArgument);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int pid))
        {
            try
            {
                using Process previous = Process.GetProcessById(pid);
                previous.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        try
        {
            File.Delete(OldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked; the next start tries again.
        }
    }

    private static HttpClient CreateClient()
    {
        // The download timeout is per call; GitHub's API rejects requests without a User-Agent.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RacingStudio/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
