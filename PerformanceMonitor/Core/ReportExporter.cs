using System.Net;
using System.Text;

namespace PerformanceMonitor.Core;

/// <summary>Erstellt einen eigenständigen HTML-Bericht eines Lag-Scans.</summary>
public static class ReportExporter
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string ToHtml(ScanResult r, SystemSummary? sys)
    {
        string scoreColor = r.Score >= 85 ? "#22c55e" : r.Score >= 65 ? "#22d3ee" : r.Score >= 40 ? "#f59e0b" : "#ef4444";
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>PerfPulse Bericht – {{r.Time:dd.MM.yyyy HH:mm}}</title>
            <style>
            body{margin:0;background:#0e1016;color:#e7eaf3;font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            .wrap{max-width:960px;margin:0 auto;padding:32px 20px}
            h1{font-size:26px;margin:0 0 4px} h2{font-size:17px;margin:32px 0 12px}
            .muted{color:#8c94aa}
            .card{background:#181c29;border:1px solid #252b3d;border-radius:14px;padding:18px 20px;margin-bottom:12px}
            .hero{display:flex;gap:24px;align-items:center}
            .score{width:110px;height:110px;border-radius:50%;border:10px solid {{scoreColor}};display:flex;flex-direction:column;align-items:center;justify-content:center;flex:none}
            .score b{font-size:34px;line-height:1}
            .f{border-left:4px solid #555;}
            .s3{border-left-color:#ef4444}.s2{border-left-color:#f59e0b}.s1{border-left-color:#60a5fa}.s0{border-left-color:#22c55e}
            .tag{display:inline-block;font-size:11px;padding:1px 8px;border-radius:99px;background:#252b3d;color:#8c94aa;margin-left:8px}
            table{width:100%;border-collapse:collapse} td,th{text-align:left;padding:8px 10px;border-bottom:1px solid #252b3d} th{color:#8c94aa;font-weight:500}
            td.n{text-align:right;font-variant-numeric:tabular-nums}
            </style></head><body><div class="wrap">
            <div class="card hero"><div class="score"><b>{{r.Score}}</b><span class="muted">{{E(r.Rating)}}</span></div>
            <div><h1>{{E(r.Headline)}}</h1><div class="muted">PerfPulse Lag-Scan vom {{r.Time:dd.MM.yyyy, HH:mm}} Uhr</div><p>{{E(r.Summary)}}</p></div></div>
            """);

        if (sys != null)
        {
            sb.Append("<h2>System</h2><div class=\"card\"><table>");
            void Row(string k, string v) => sb.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");
            Row("Betriebssystem", $"{sys.OsName} {sys.OsVersion}");
            Row("Prozessor", $"{sys.CpuName} ({sys.Cores} Kerne / {sys.Threads} Threads)");
            Row("Grafik", sys.GpuShort);
            Row("Arbeitsspeicher", $"{Fmt.Bytes(sys.RamTotal)} {sys.RamDetails}");
            if (sys.Model.Length > 0) Row("Gerät", $"{sys.Manufacturer} {sys.Model}");
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Ergebnisse</h2>");
        foreach (var f in r.Findings)
        {
            sb.Append($"<div class=\"card f s{(int)f.Severity}\"><b>{E(f.Title)}</b><span class=\"tag\">{E(f.Category)}</span>");
            sb.Append($"<div class=\"muted\">{E(f.Detail)}</div>");
            if (f.HasRecommendation) sb.Append($"<div style=\"margin-top:6px\">💡 {E(f.Recommendation)}</div>");
            sb.Append("</div>");
        }

        sb.Append("<h2>Hauptverursacher</h2><div class=\"card\"><table><tr><th>Programm</th><th>Ø CPU</th><th>Spitze</th><th>RAM</th><th>Datenträger</th><th>GPU</th></tr>");
        foreach (var c in r.Culprits)
            sb.Append($"<tr><td><b>{E(c.DisplayName)}</b><br><span class=\"muted\">{E(c.SubText)}</span></td><td class=\"n\">{E(c.CpuText)}</td><td class=\"n\">{E(c.PeakText)}</td><td class=\"n\">{E(c.RamText)}</td><td class=\"n\">{E(c.IoText)}</td><td class=\"n\">{E(c.GpuText)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("<p class=\"muted\" style=\"margin-top:32px\">Erstellt mit PerfPulse ").Append(E(App.Version)).Append("</p></div></body></html>");
        return sb.ToString();
    }
}
