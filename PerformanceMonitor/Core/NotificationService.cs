using System.Text.Json;

namespace PerformanceMonitor.Core;

public sealed record AppNotification(string Id, string Title, string Message, string Level, string? Link, string? LinkText);

/// <summary>Lädt die im Admin-Bereich der Website geschriebenen Mitteilungen.</summary>
public static class NotificationService
{
    private static List<AppNotification> _all = [];

    /// <summary>Aktive Mitteilungen, die der Benutzer noch nicht geschlossen hat.</summary>
    public static IReadOnlyList<AppNotification> Visible =>
        _all.Where(n => !App.Settings.DismissedNotifications.Contains(n.Id)).ToList();

    public static event Action? Changed;

    /// <summary>Lädt die Mitteilungen neu und gibt die zurück, die der Benutzer noch nie gesehen hat.</summary>
    public static async Task<IReadOnlyList<AppNotification>> RefreshAsync()
    {
        if (UpdateService.ServerUrl is not string server) return [];
        try
        {
            using var http = UpdateService.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"{server}/api/notifications.php?v={App.Version}"));
            _all = doc.RootElement.GetProperty("notifications").EnumerateArray()
                .Select(n => new AppNotification(
                    n.GetProperty("id").GetString() ?? "",
                    n.GetProperty("title").GetString() ?? "",
                    n.GetProperty("message").GetString() ?? "",
                    n.GetProperty("level").GetString() ?? "info",
                    n.GetProperty("link").GetString() is string l && Uri.TryCreate(l, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? l : null,
                    n.GetProperty("linkText").GetString()))
                .Where(n => n.Id != "")
                .ToList();
        }
        catch
        {
            return []; // offline – bisherige Mitteilungen behalten
        }

        var settings = App.Settings;
        var ids = _all.Select(n => n.Id).ToHashSet();
        var fresh = _all.Where(n => !settings.SeenNotifications.Contains(n.Id) && !settings.DismissedNotifications.Contains(n.Id)).ToList();
        // Gelöschte Mitteilungen nicht ewig merken
        settings.SeenNotifications = settings.SeenNotifications.Where(ids.Contains).Concat(fresh.Select(n => n.Id)).ToList();
        settings.DismissedNotifications = settings.DismissedNotifications.Where(ids.Contains).ToList();
        settings.Save();
        Changed?.Invoke();
        return fresh;
    }

    public static void Dismiss(string id)
    {
        if (!App.Settings.DismissedNotifications.Contains(id)) App.Settings.DismissedNotifications.Add(id);
        App.Settings.Save();
        Changed?.Invoke();
    }
}
