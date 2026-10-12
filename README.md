# PerfPulse – Performance Monitor & Lag-Analyse

WPF-App (.NET 10) für Windows 10/11, die live misst, was dein PC gerade tut, und mit einem Lag-Scan herausfindet, was ihn ausbremst.

## Download / Installation

Fertiger Installer: **[neueste Version unter Releases](../../releases/latest)** → `PerfPulse-Setup-<version>.exe` herunterladen und ausführen.

Der Installer ist self-contained (kein .NET nötig). Beim ersten Start zeigt Windows SmartScreen evtl. eine Warnung, weil der Installer nicht signiert ist → „Weitere Informationen" → „Trotzdem ausführen".

## Funktionen

| Bereich | Was es macht |
|---|---|
| **Dashboard** | Live-Diagramme für CPU (inkl. Takt), RAM, GPU, Datenträger und Netzwerk, aktivste Prozesse, Energieplan-Wahl, Boost-Modus |
| **Prozesse** | Alle Prozesse mit CPU/RAM/Disk/GPU (Heatmap wie im Task-Manager), Suche, Gruppierung nach Programm, Beenden, Priorität, Speicherort, Websuche |
| **Lag-Scan** | Misst 10/20/40 s lang und prüft: CPU-Last und -Spitzen, Drosselung, RAM-Druck und Auslagerung, Datenträgerlast, GPU-Limit, Treiber-Latenz (DPC/Interrupts), Energiesparplan, Akkubetrieb, Speicherplatz, HDD als Systemlaufwerk, Laufzeit seit Neustart, Autostart-Menge, Temperatur (als Admin). Ergebnis: Score 0–100, Hauptverursacher mit Erklärungen, Lösungen mit 1-Klick-Aktionen, Verlauf und HTML-Bericht |
| **Temperatur** | Liest die ACPI-Thermalzonen ohne Adminrechte und ohne Treiber (Prozessor, Grafik, Akku, Ladeelektronik, Gehäuseoberfläche, Chipsatz …), die SSD als Admin. Bauteile ohne Sensor werden aus der Auslastung geschätzt und mit ≈ gekennzeichnet. Ein isometrisches Modell des Geräts (Laptop oder Desktop) lässt sich animiert zwischen zusammengebaut und zerlegt umschalten und zeigt Temperatur-Hotspots, Beschriftungen und Details beim Überfahren mit der Maus. Dazu kommen Drosselungserkennung, Analyse mit Empfehlungen und ein Wärmetest (30 s Volllast plus Abkühlung) |
| **Hintergrund** | Erkennt Programme ohne sichtbares Fenster (Updater, Launcher, Tray-Tools, Browser-Reste …) und bewertet sie als unnötig, optional oder wichtig. Sie lassen sich einzeln oder gesammelt beenden und aus dem Autostart nehmen |
| **Speicheranalyse** | Grosse Dateien (mit Alter und Hinweisen zu Installern, Archiven, Abbildern …) sowie Duplikate per SHA-256 inklusive Hardlink-Erkennung. Gelöscht wird in den Papierkorb, und aus jeder Gruppe bleibt immer mindestens eine Datei erhalten. Programm-, Entwickler- und Systemordner werden übersprungen |
| **Autostart** | Autostart-Einträge (Registry und Autostart-Ordner) ein- und ausschalten, auf dieselbe Art wie der Task-Manager |
| **Bereinigung** | Über 25 Bereiche, angezeigt werden nur die auf dem PC vorhandenen: Windows (Temp, INetCache, Miniaturansichten, Fehlerberichte, Speicherabbilder, Update-Cache, Übermittlungsoptimierung, Protokolle, Shader-Cache, Papierkorb), alle Browser einzeln, Apps und Spiele (Discord, Teams, Slack, Spotify, Steam, Epic, VS Code, NVIDIA/AMD-Shader) sowie Entwicklung (NuGet, npm, Yarn, pip, Gradle). Mit Schnellauswahl, einstellbarem Mindestalter und der Option, betroffene Programme automatisch zu schliessen |
| **Systeminfo** | Betriebssystem, CPU, GPU, RAM-Bestückung, Gerät/BIOS, Laufwerke mit SSD/HDD-Erkennung |
| **Infobereich** | Läuft im Infobereich weiter und warnt per Benachrichtigung bei dauerhaft hoher CPU- oder RAM-Last (mit Hauptverursacher) |

## Entwickeln

```powershell
dotnet run --project PerformanceMonitor
```

## Installer bauen

Voraussetzung: [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.0.0
```

Ergebnis: `dist\PerfPulse-Setup-1.0.0.exe`. Der Installer ist self-contained, auf dem Zielrechner muss also kein .NET installiert sein.

Das Icon und die Installer-Grafiken werden von `tools\make-assets.ps1` erzeugt (beim Build automatisch, überspringen mit `-SkipAssets`).

## Daten

Einstellungen und Scan-Verlauf liegen in `%AppData%\PerfPulse`.
