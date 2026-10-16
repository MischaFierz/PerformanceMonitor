using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PerformanceMonitor.Core;

public enum UpdateState { Idle, Checking, UpToDate, Paused, Downloading, Installing, Failed }

/// <summary>Sucht beim Start nach einer neueren Version und installiert sie still.
/// Quelle ist die PerfPulse-Website (Admin-Bereich), GitHub-Releases dienen als Ausweichquelle.</summary>
public static class UpdateService
{
    private const string GitHubLatestUrl = "https://api.github.com/repos/MischaFierz/PerformanceMonitor/releases/latest";

    private sealed record ReleaseInfo(Version Version, string? Url, string? Sha256, bool Paused);

    public static UpdateState State { get; private set; } = UpdateState.Idle;
    public static string? LatestVersion { get; private set; }
    public static event Action? Changed;

    /// <summary>Adresse der PerfPulse-Website: aus settings.json oder beim Build gesetzt (build.ps1 -UpdateServer).</summary>
    public static string? ServerUrl
    {
        get
        {
            var url = App.Settings.UpdateServer;
            if (string.IsNullOrWhiteSpace(url))
                url = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "UpdateServer")?.Value;
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        }
    }

    /// <summary>Nur Installationen aus dem Setup aktualisieren (nicht beim Start aus dem Quellcode).</summary>
    public static bool IsInstalled =>
        Environment.ProcessPath is string exe && File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "unins000.exe"));

    internal static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PerfPulse/{App.Version}");
        return http;
    }

    private static void Set(UpdateState state, string? latest = null)
    {
        State = state;
        if (latest != null) LatestVersion = latest;
        Changed?.Invoke();
    }

    /// <summary>Prüft auf Updates; ist eine neuere Version vorhanden und <paramref name="install"/> gesetzt,
    /// wird sie heruntergeladen und der Installer gestartet. Gibt true zurück, wenn die App beendet werden soll.</summary>
    public static async Task<bool> CheckAndInstallAsync(bool install, bool startMinimized)
    {
        try
        {
            Set(UpdateState.Checking);
            using var http = CreateClient();
            var release = await FromServerAsync(http) ?? await FromGitHubAsync(http);
            if (release == null) { Set(UpdateState.Failed); return false; }

            var latest = release.Version.ToString(3);
            if (release.Version <= Version.Parse(App.Version)) { Set(UpdateState.UpToDate, latest); return false; }
            if (release.Paused) { Set(UpdateState.Paused, latest); return false; }
            if (!install || !IsInstalled || release.Url == null) { Set(UpdateState.Idle, latest); return false; }

            Set(UpdateState.Downloading, latest);
            var dir = Path.Combine(Path.GetTempPath(), "PerfPulse-Update");
            Directory.CreateDirectory(dir);
            var setup = Path.Combine(dir, $"PerfPulse-Setup-{latest}.exe");
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(setup);
                await response.Content.CopyToAsync(file);
            }

            if (release.Sha256 != null)
            {
                await using var stream = File.OpenRead(setup);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    stream.Close();
                    File.Delete(setup);
                    Set(UpdateState.Failed);
                    return false;
                }
            }

            Set(UpdateState.Installing);
            // Benutzer-Installation (ohne Adminrechte) beibehalten
            var scope = Environment.ProcessPath!.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
                ? "/CURRENTUSER" : "/ALLUSERS";
            var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- {scope} /Relaunch={(startMinimized ? "minimized" : "normal")}";
            Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = true });
            return true;
        }
        catch
        {
            // Offline, UAC-Abfrage abgebrochen o. Ä. – beim nächsten Start erneut versuchen
            Set(UpdateState.Failed);
            return false;
        }
    }

    private static async Task<ReleaseInfo?> FromServerAsync(HttpClient http)
    {
        if (ServerUrl is not string server) return null;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{server}/api/update.php?v={App.Version}"));
            var root = doc.RootElement;
            if (root.GetProperty("version").GetString() is not string v || !Version.TryParse(v, out var version)) return null;
            return new ReleaseInfo(version,
                root.GetProperty("url").GetString(),
                root.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                root.TryGetProperty("paused", out var paused) && paused.GetBoolean());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ReleaseInfo?> FromGitHubAsync(HttpClient http)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var version)) return null;

        var asset = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() is string n
                                 && n.StartsWith("PerfPulse-Setup", StringComparison.OrdinalIgnoreCase)
                                 && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object) return new ReleaseInfo(version, null, null, false);
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        return new ReleaseInfo(version,
            asset.GetProperty("browser_download_url").GetString(),
            digest?.StartsWith("sha256:") == true ? digest[7..] : null,
            false);
    }
}
