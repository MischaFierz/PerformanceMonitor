# Erzeugt App-Icon (.ico), Logo (.png) und Installer-Grafiken (.bmp) für PerfPulse.
# Aufruf: powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$appAssets = Join-Path $root 'PerformanceMonitor\Assets'
$setupAssets = Join-Path $root 'installer\assets'
New-Item -ItemType Directory -Force $appAssets, $setupAssets | Out-Null

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

public static class AssetGen
{
    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Zeichnet das Icon (abgerundetes Quadrat mit Verlauf + Puls-Linie) in das Rechteck.
    public static void DrawIcon(Graphics g, RectangleF rect)
    {
        float size = rect.Width;
        using (var path = RoundRect(rect, size * 0.24f))
        using (var brush = new LinearGradientBrush(rect, Color.Cyan, Color.Purple, 45f))
        {
            var blend = new ColorBlend();
            blend.Colors = new[] { Color.FromArgb(0x22, 0xD3, 0xEE), Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x8B, 0x5C, 0xF6), Color.FromArgb(0xD9, 0x46, 0xEF) };
            blend.Positions = new[] { 0f, 0.38f, 0.72f, 1f };
            brush.InterpolationColors = blend;
            g.FillPath(brush, path);

            // Glanz oben
            if (size >= 24)
            {
                var state = g.Save();
                g.SetClip(path);
                var glossRect = new RectangleF(rect.X - size * 0.2f, rect.Y - size * 0.55f, size * 1.4f, size * 1.0f);
                using (var gloss = new LinearGradientBrush(glossRect, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillEllipse(gloss, glossRect);
                g.Restore(state);
            }
        }

        // Puls-/Herzschlag-Linie
        float[,] rel = { { 0.15f, 0.57f }, { 0.33f, 0.57f }, { 0.42f, 0.33f }, { 0.53f, 0.76f }, { 0.62f, 0.45f }, { 0.68f, 0.57f }, { 0.85f, 0.57f } };
        var pts = new PointF[rel.GetLength(0)];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF(rect.X + rel[i, 0] * size, rect.Y + rel[i, 1] * size);

        float w = Math.Max(1.5f, size * 0.078f);
        if (size >= 32)
        {
            using (var glow = new Pen(Color.FromArgb(55, 255, 255, 255), w * 2.4f))
            {
                glow.LineJoin = LineJoin.Round; glow.StartCap = LineCap.Round; glow.EndCap = LineCap.Round;
                g.DrawLines(glow, pts);
            }
        }
        using (var pen = new Pen(Color.White, w))
        {
            pen.LineJoin = LineJoin.Round; pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
            g.DrawLines(pen, pts);
        }
    }

    static Graphics Prepare(Bitmap bmp)
    {
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    public static Bitmap RenderIcon(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Prepare(bmp))
        {
            g.Clear(Color.Transparent);
            float pad = size <= 16 ? 0f : size * 0.03f;
            DrawIcon(g, new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad));
        }
        return bmp;
    }

    public static void WriteIco(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = RenderIcon(s))
                images.Add(s >= 256 ? Png(bmp) : Dib(bmp));

        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(images[i].Length); bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
    }

    static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int maskStride = ((w + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            for (int y = h - 1; y >= 0; y--) bw.Write(px, y * data.Stride, w * 4);
            bw.Write(new byte[maskStride * h]);
            bw.Flush();
            return ms.ToArray();
        }
    }

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    public static void SavePng(string path, int size)
    {
        using (var b = RenderIcon(size)) b.Save(path, ImageFormat.Png);
    }

    // Grosses Bild links im Installer (Willkommens-/Abschlussseite)
    public static void WizardImage(string path, int w, int h)
    {
        using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (var g = Prepare(bmp))
        {
            var rect = new Rectangle(0, 0, w, h);
            using (var bg = new LinearGradientBrush(rect, Color.FromArgb(0x0E, 0x10, 0x16), Color.FromArgb(0x24, 0x1B, 0x4F), 75f))
                g.FillRectangle(bg, rect);

            // dezente Puls-Linie im Hintergrund
            float s = w / 164f;
            var line = new PointF[] {
                new PointF(0, h * 0.78f), new PointF(w * 0.30f, h * 0.78f), new PointF(w * 0.40f, h * 0.70f),
                new PointF(w * 0.52f, h * 0.88f), new PointF(w * 0.62f, h * 0.74f), new PointF(w * 0.70f, h * 0.78f), new PointF(w, h * 0.78f) };
            using (var pen = new Pen(Color.FromArgb(70, 0x22, 0xD3, 0xEE), 2.2f * s)) { pen.LineJoin = LineJoin.Round; g.DrawLines(pen, line); }

            float iconSize = w * 0.52f;
            DrawIcon(g, new RectangleF((w - iconSize) / 2, h * 0.20f, iconSize, iconSize));

            using (var f = new Font("Segoe UI Semibold", 15f * s, GraphicsUnit.Pixel))
            using (var f2 = new Font("Segoe UI", 9.5f * s, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.White))
            using (var sub = new SolidBrush(Color.FromArgb(0x9A, 0xA3, 0xBA)))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString("PerfPulse", f, white, new RectangleF(0, h * 0.20f + iconSize + 12 * s, w, 30 * s), fmt);
                g.DrawString("Performance Monitor", f2, sub, new RectangleF(0, h * 0.20f + iconSize + 34 * s, w, 20 * s), fmt);
            }
            bmp.Save(path, ImageFormat.Bmp);
        }
    }

    // Kleines Bild oben rechts im Installer
    public static void WizardSmall(string path, int size)
    {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb))
        using (var g = Prepare(bmp))
        {
            g.Clear(Color.White);
            float pad = size * 0.06f;
            DrawIcon(g, new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad));
            bmp.Save(path, ImageFormat.Bmp);
        }
    }
}
'@

[AssetGen]::WriteIco((Join-Path $appAssets 'app.ico'), [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256))
[AssetGen]::SavePng((Join-Path $appAssets 'logo.png'), 256)
[AssetGen]::SavePng((Join-Path $setupAssets 'preview.png'), 512)
foreach ($pair in @(@(164, 314), @(205, 393), @(246, 471), @(328, 628))) {
    [AssetGen]::WizardImage((Join-Path $setupAssets "wizard-$($pair[0]).bmp"), $pair[0], $pair[1])
}
foreach ($s in 55, 69, 83, 110) {
    [AssetGen]::WizardSmall((Join-Path $setupAssets "small-$s.bmp"), $s)
}
Write-Host "Assets erzeugt:" -ForegroundColor Green
Get-ChildItem $appAssets, $setupAssets | ForEach-Object { "  $($_.FullName)" }
