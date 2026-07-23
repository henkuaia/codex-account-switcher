[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputPath = Join-Path $scriptDirectory '..\src\CodexAccountSwitcher\Assets\CodexAccountSwitcher.ico'
}

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class ProductIconGenerator
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    public static void Generate(string outputPath)
    {
        var frames = new byte[Sizes.Length][];
        for (var index = 0; index < Sizes.Length; index++)
        {
            frames[index] = Render(Sizes[index]);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
        using (var stream = File.Create(outputPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)Sizes.Length);

            var offset = 6 + (16 * Sizes.Length);
            for (var index = 0; index < Sizes.Length; index++)
            {
                var size = Sizes[index];
                writer.Write(size == 256 ? (byte)0 : (byte)size);
                writer.Write(size == 256 ? (byte)0 : (byte)size);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frames[index].Length);
                writer.Write(offset);
                offset += frames[index].Length;
            }

            foreach (var frame in frames)
            {
                writer.Write(frame);
            }
        }
    }

    private static byte[] Render(int size)
    {
        using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            var scale = size / 256f;
            using (var background = Rounded(4 * scale, 4 * scale, 248 * scale, 248 * scale, 48 * scale))
            using (var backgroundBrush = new LinearGradientBrush(
                new PointF(16 * scale, 12 * scale),
                new PointF(238 * scale, 246 * scale),
                Color.FromArgb(38, 98, 116),
                Color.FromArgb(28, 68, 83)))
            {
                graphics.FillPath(backgroundBrush, background);
            }

            DrawAccountCard(graphics, scale, 24, 38, Color.White);
            DrawAccountCard(graphics, scale, 142, 38, Color.FromArgb(226, 242, 238));

            using (var arrowPen = new Pen(Color.FromArgb(248, 199, 91), Math.Max(1.4f, 15 * scale)))
            {
                arrowPen.StartCap = LineCap.Round;
                arrowPen.EndCap = LineCap.Round;
                graphics.DrawLine(arrowPen, 48 * scale, 171 * scale, 204 * scale, 171 * scale);
                graphics.DrawLine(arrowPen, 208 * scale, 211 * scale, 52 * scale, 211 * scale);
            }

            using (var arrowBrush = new SolidBrush(Color.FromArgb(248, 199, 91)))
            {
                graphics.FillPolygon(arrowBrush, new[]
                {
                    new PointF(204 * scale, 147 * scale),
                    new PointF(238 * scale, 171 * scale),
                    new PointF(204 * scale, 195 * scale),
                });
                graphics.FillPolygon(arrowBrush, new[]
                {
                    new PointF(52 * scale, 187 * scale),
                    new PointF(18 * scale, 211 * scale),
                    new PointF(52 * scale, 235 * scale),
                });
            }

            using (var output = new MemoryStream())
            {
                bitmap.Save(output, ImageFormat.Png);
                return output.ToArray();
            }
        }
    }

    private static void DrawAccountCard(Graphics graphics, float scale, float x, float y, Color fill)
    {
        using (var card = Rounded(x * scale, y * scale, 90 * scale, 104 * scale, 20 * scale))
        using (var cardBrush = new SolidBrush(fill))
        using (var profileBrush = new SolidBrush(Color.FromArgb(38, 98, 116)))
        {
            graphics.FillPath(cardBrush, card);
            graphics.FillEllipse(
                profileBrush,
                (x + 31) * scale,
                (y + 19) * scale,
                28 * scale,
                28 * scale);
            graphics.FillEllipse(
                profileBrush,
                (x + 20) * scale,
                (y + 53) * scale,
                50 * scale,
                35 * scale);
        }
    }

    private static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(radius * 2, width), height);
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
'@

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
[ProductIconGenerator]::Generate($resolvedOutput)
Write-Host "Generated icon: $resolvedOutput"
