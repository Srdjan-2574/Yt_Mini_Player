# Generates the app icons (src\YtMiniPlayer.ico, lite\src\YtMiniLite.ico) in every size Windows uses.
# The build scripts embed them into the .exe files. Re-run only if the artwork changes.
# Usage: powershell -ExecutionPolicy Bypass -File .\scripts\make-icons.ps1
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class AppIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    // Red circle with a white play triangle (YtMiniPlayer) or a music note (YtMiniLite), on a 64x64 grid.
    static Bitmap Draw(int size, bool note)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size / 64f, size / 64f);
            using (var red = new SolidBrush(Color.FromArgb(255, 0, 51)))
                g.FillEllipse(red, 2, 2, 60, 60);
            if (note)
            {
                g.FillEllipse(Brushes.White, 18, 38, 14, 11);
                g.FillRectangle(Brushes.White, 29, 16, 4, 28);
                g.FillPolygon(Brushes.White, new[] { new PointF(29, 16), new PointF(46, 22), new PointF(46, 28), new PointF(33, 23) });
            }
            else
            {
                g.FillPolygon(Brushes.White, new[] { new PointF(25, 18), new PointF(25, 46), new PointF(47, 32) });
            }
        }
        return bmp;
    }

    // 32-bit DIB entry (BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask), the classic ICO format.
    static byte[] Dib(Bitmap bmp)
    {
        int n = bmp.Width;
        int maskStride = ((n + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(n * n * 4 + maskStride * n); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            w.Write(new byte[maskStride * n]); // alpha channel does the transparency
            return ms.ToArray();
        }
    }

    public static void Save(string path, bool note)
    {
        var images = new List<byte[]>();
        foreach (int size in Sizes)
            using (Bitmap bmp = Draw(size, note))
            {
                if (size < 256)
                {
                    images.Add(Dib(bmp));
                    continue;
                }
                using (var png = new MemoryStream()) // 256 px is stored as PNG, as Windows expects
                {
                    bmp.Save(png, ImageFormat.Png);
                    images.Add(png.ToArray());
                }
            }

        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i])); w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (byte[] image in images) w.Write(image);
        }
    }
}
'@

[AppIcon]::Save("$root\src\YtMiniPlayer.ico", $false)
[AppIcon]::Save("$root\lite\src\YtMiniLite.ico", $true)
Get-Item "$root\src\YtMiniPlayer.ico", "$root\lite\src\YtMiniLite.ico" | ForEach-Object { "{0} ({1:N0} KB)" -f $_.FullName, ($_.Length / 1KB) }
