using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Fences.Core;

/// <summary>A newer release than the one running.</summary>
internal sealed record Update(Version Version, string InstallerUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer build and installs it.
///
/// The repository is public, so this needs no credentials: an unauthenticated request to the
/// releases API is enough. Updates are applied by handing the downloaded installer the silent
/// switch and stepping out of its way — Inno Setup then replaces the files this app is running
/// from, which it cannot do while the app is still alive.
/// </summary>
internal static class Updater
{
    private const string LatestRelease = "https://api.github.com/repos/limburatorul/Fences/releases/latest";

    public static Version Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// GitHub rejects API requests that do not identify themselves. Declared after
    /// <see cref="Current"/> because static initialisers run in declaration order.
    /// </summary>
    private static readonly ProductInfoHeaderValue Agent = new("Fences", Current.ToString(3));

    /// <summary>Compares on major.minor.patch only; the assembly's fourth component is noise.</summary>
    private static Version Release(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>Returns the newer release, or null when this build is current or the check failed.</summary>
    public static async Task<Update?> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.Add(Agent);

            await using var stream = await client.GetStreamAsync(LatestRelease, cancellation);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
            var release = json.RootElement;

            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;

            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (Release(version) <= Release(Current)) return null;

            string? installer = release.GetProperty("assets").EnumerateArray()
                .Select(asset => asset.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(url => url is not null && url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (installer is null) return null;

            string notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            return new Update(version, installer, notes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException)
        {
            return null;    // offline, rate-limited, or no release yet — nothing to tell the user
        }
    }

    /// <summary>
    /// Downloads the installer and starts it silently. Returns once it is running, so the caller
    /// must shut the app down immediately afterwards.
    /// </summary>
    public static async Task<bool> InstallAsync(Update update, CancellationToken cancellation = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"Fences-{update.Version}-setup.exe");

        try
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                client.DefaultRequestHeaders.UserAgent.Add(Agent);
                await using var source = await client.GetStreamAsync(update.InstallerUrl, cancellation);
                await using var file = File.Create(path);
                await source.CopyToAsync(file, cancellation);
            }

            Process.Start(new ProcessStartInfo(path)
            {
                // VERYSILENT keeps the progress window away; the app restarts itself when done.
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                      or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
