using System.Diagnostics;

namespace PerformanceMonitor.Core;

/// <summary>Erklärungen zu häufigen Prozessen, die oft für Lags verantwortlich sind.</summary>
public static class KnownProcesses
{
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso", "svchost",
        "dwm", "fontdrvhost", "Memory Compression", "Secure System", "sihost", "MsMpEng", "NisSrv",
        "SecurityHealthService", "audiodg", "spoolsv", "WUDFHost", "Interrupts", "PerfPulse",
    };

    private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MsMpEng"] = "Microsoft Defender scannt gerade Dateien. Vollscans lassen sich in der Windows-Sicherheit auf Zeiten legen, in denen du den PC nicht nutzt.",
        ["NisSrv"] = "Netzwerkschutz von Microsoft Defender.",
        ["MpDefenderCoreService"] = "Kerndienst von Microsoft Defender.",
        ["SearchIndexer"] = "Die Windows-Suche indiziert Dateien. Das ist meist nach Updates oder vielen neuen Dateien kurzzeitig aktiv.",
        ["SearchProtocolHost"] = "Teil der Windows-Suchindizierung.",
        ["SearchFilterHost"] = "Teil der Windows-Suchindizierung.",
        ["TiWorker"] = "Windows Update installiert Komponenten. Am besten abwarten und danach neu starten.",
        ["TrustedInstaller"] = "Windows Update / Windows-Module-Installer ist aktiv.",
        ["MoUsoCoreWorker"] = "Windows Update sucht nach Updates oder lädt sie herunter.",
        ["usocoreworker"] = "Windows Update sucht nach Updates oder lädt sie herunter.",
        ["wuauclt"] = "Windows Update ist aktiv.",
        ["CompatTelRunner"] = "Windows-Telemetrie (Kompatibilitätsprüfung). Läuft gelegentlich im Hintergrund und kann viel Datenträgerlast erzeugen.",
        ["WmiPrvSE"] = "WMI-Provider-Host. Hohe Last wird meist von einem anderen Programm verursacht, das Systeminformationen abfragt.",
        ["svchost"] = "Dienst-Host von Windows. Enthält mehrere Systemdienste (z. B. Windows Update, Netzwerk).",
        ["dwm"] = "Desktop Window Manager (Fensterdarstellung). Hohe Last bei vielen Monitoren, Animationen oder Grafiktreiberproblemen.",
        ["System"] = "Windows-Kernel und Treiber. Dauerhaft hohe Last deutet oft auf einen fehlerhaften Treiber hin.",
        ["Memory Compression"] = "Windows komprimiert Arbeitsspeicher, weil der RAM knapp wird.",
        ["explorer"] = "Windows Explorer (Taskleiste und Dateimanager).",
        ["chrome"] = "Google Chrome – jeder Tab und jede Erweiterung ist ein eigener Prozess. Schliesse ungenutzte Tabs oder aktiviere „Speicher sparen“.",
        ["msedge"] = "Microsoft Edge – viele Tabs/Erweiterungen erhöhen die Last. Die Funktion „Effizienzmodus“ hilft.",
        ["firefox"] = "Mozilla Firefox – viele Tabs oder Erweiterungen erhöhen die Last.",
        ["opera"] = "Opera Browser – viele Tabs erhöhen die Last.",
        ["brave"] = "Brave Browser – viele Tabs erhöhen die Last.",
        ["msedgewebview2"] = "WebView2 – eingebetteter Browser, wird von Apps wie Teams, Widgets oder Outlook genutzt.",
        ["Discord"] = "Discord – Hardwarebeschleunigung und Overlays können Leistung kosten.",
        ["ms-teams"] = "Microsoft Teams – bekannt für hohen RAM-Verbrauch.",
        ["Teams"] = "Microsoft Teams – bekannt für hohen RAM-Verbrauch.",
        ["Slack"] = "Slack (Electron-App) – benötigt relativ viel Arbeitsspeicher.",
        ["Spotify"] = "Spotify – Hardwarebeschleunigung kann in den Einstellungen deaktiviert werden.",
        ["OneDrive"] = "OneDrive synchronisiert Dateien. Während der Synchronisierung entsteht Datenträger- und Netzwerklast.",
        ["Dropbox"] = "Dropbox synchronisiert Dateien.",
        ["GoogleDriveFS"] = "Google Drive synchronisiert Dateien.",
        ["steamwebhelper"] = "Steam-Oberfläche (Browser-basiert). Im Spiel kann das Steam-Overlay deaktiviert werden.",
        ["steam"] = "Steam-Client – Downloads und Shader-Vorberechnung erzeugen Last.",
        ["EpicGamesLauncher"] = "Epic Games Launcher – läuft oft unnötig im Hintergrund.",
        ["NVDisplay.Container"] = "NVIDIA-Treiberdienst.",
        ["nvcontainer"] = "NVIDIA-Container (GeForce Experience/App, Overlay).",
        ["RadeonSoftware"] = "AMD-Software (Radeon).",
        ["AvastSvc"] = "Avast Antivirus – Scans können das System stark bremsen.",
        ["avp"] = "Kaspersky Antivirus – Scans können das System stark bremsen.",
        ["bdservicehost"] = "Bitdefender – Scans können das System stark bremsen.",
        ["Code"] = "Visual Studio Code mit Erweiterungen.",
        ["devenv"] = "Visual Studio.",
        ["rider64"] = "JetBrains Rider – Indizierung erzeugt kurzzeitig viel Last.",
        ["java"] = "Java-Anwendung (z. B. Minecraft oder eine Entwicklungsumgebung).",
        ["javaw"] = "Java-Anwendung (z. B. Minecraft).",
        ["RuntimeBroker"] = "Verwaltet Berechtigungen von Store-Apps.",
        ["Widgets"] = "Windows-Widgets – können in den Taskleisten-Einstellungen deaktiviert werden.",
        ["PhoneExperienceHost"] = "„Smartphone-Link“ – kann deaktiviert werden, wenn du es nicht nutzt.",
        ["SystemSettings"] = "Windows-Einstellungen.",
        ["MsSense"] = "Microsoft Defender for Endpoint (Firmen-Sicherheitssoftware).",
    };

    public static bool IsCritical(string name) => Critical.Contains(name);

    public static string? GetHint(string name) => Hints.GetValueOrDefault(name);
}

public static class ProcessActions
{
    public static (int Ok, int Failed) KillByName(string name)
    {
        int ok = 0, failed = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try { p.Kill(); ok++; }
                catch { failed++; }
            }
        }
        return (ok, failed);
    }

    public static (int Ok, int Failed) Kill(IEnumerable<int> pids)
    {
        int ok = 0, failed = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                ok++;
            }
            catch { failed++; }
        }
        return (ok, failed);
    }

    public static int SetPriority(IEnumerable<int> pids, ProcessPriorityClass priority)
    {
        int failed = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.PriorityClass = priority;
            }
            catch { failed++; }
        }
        return failed;
    }

    public static void OpenFileLocation(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            Process.Start("explorer.exe", $"\"{path}\"");
    }

    public static void SearchWeb(string name)
    {
        var query = Uri.EscapeDataString($"{name}.exe Prozess");
        OpenUrl($"https://www.google.com/search?q={query}");
    }

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
