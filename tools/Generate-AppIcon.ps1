param(
    [string]$OutputPath = "IntuneWinPackager.App/Assets/IntuneWinPackager.ico"
)

Add-Type -AssemblyName System.Drawing

$iconCode = @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public sealed class IconImageData
{
    public int Size;
    public byte[] Data;
}

public static class IntuneWinPackagerIconGenerator
{
    public static void Generate(string outputPath)
    {
        int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<IconImageData> images = new List<IconImageData>();

        foreach (int size in sizes)
        {
            using (Bitmap bmp = DrawIcon(size))
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                images.Add(new IconImageData { Size = size, Data = ms.ToArray() });
            }
        }

        using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)images.Count);

            int offset = 6 + (16 * images.Count);
            foreach (IconImageData image in images)
            {
                bw.Write((byte)(image.Size >= 256 ? 0 : image.Size));
                bw.Write((byte)(image.Size >= 256 ? 0 : image.Size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(image.Data.Length);
                bw.Write(offset);
                offset += image.Data.Length;
            }

            foreach (IconImageData image in images)
            {
                bw.Write(image.Data);
            }
        }
    }

    private static Bitmap DrawIcon(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            g.Clear(Color.Transparent);

            float pad = size * 0.08f;
            RectangleF bgRect = new RectangleF(pad, pad, size - (2 * pad), size - (2 * pad));

            using (GraphicsPath shadowPath = RoundedRect(new RectangleF(bgRect.X + size * 0.02f, bgRect.Y + size * 0.03f, bgRect.Width, bgRect.Height), size * 0.2f))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(45, 0, 18, 44)))
            {
                g.FillPath(shadowBrush, shadowPath);
            }

            using (GraphicsPath bgPath = RoundedRect(bgRect, size * 0.2f))
            using (LinearGradientBrush bgBrush = new LinearGradientBrush(bgRect, Color.FromArgb(255, 22, 84, 152), Color.FromArgb(255, 44, 154, 214), 45f))
            {
                g.FillPath(bgBrush, bgPath);
                using (Pen borderPen = new Pen(Color.FromArgb(140, 235, 247, 255), Math.Max(1f, size * 0.01f)))
                {
                    g.DrawPath(borderPen, bgPath);
                }
            }

            RectangleF glossRect = new RectangleF(bgRect.X + size * 0.05f, bgRect.Y + size * 0.04f, bgRect.Width * 0.75f, bgRect.Height * 0.42f);
            using (GraphicsPath glossPath = RoundedRect(glossRect, size * 0.12f))
            using (LinearGradientBrush glossBrush = new LinearGradientBrush(glossRect, Color.FromArgb(75, 255, 255, 255), Color.FromArgb(5, 255, 255, 255), 90f))
            {
                g.FillPath(glossBrush, glossPath);
            }

            float boxW = size * 0.50f;
            float boxH = size * 0.30f;
            float boxX = (size - boxW) / 2f - size * 0.04f;
            float boxY = size * 0.40f;
            float topDepth = size * 0.12f;

            PointF[] topPts = new PointF[]
            {
                new PointF(boxX, boxY),
                new PointF(boxX + boxW, boxY),
                new PointF(boxX + boxW - (boxW * 0.12f), boxY - topDepth),
                new PointF(boxX + (boxW * 0.12f), boxY - topDepth)
            };

            using (LinearGradientBrush topBrush = new LinearGradientBrush(new PointF(boxX, boxY - topDepth), new PointF(boxX, boxY), Color.FromArgb(255, 255, 216, 151), Color.FromArgb(255, 242, 181, 95)))
            using (Pen outlinePen = new Pen(Color.FromArgb(180, 133, 88, 36), Math.Max(1f, size * 0.012f)))
            {
                g.FillPolygon(topBrush, topPts);
                g.DrawPolygon(outlinePen, topPts);
            }

            RectangleF frontRect = new RectangleF(boxX, boxY, boxW, boxH);
            using (LinearGradientBrush frontBrush = new LinearGradientBrush(frontRect, Color.FromArgb(255, 241, 176, 82), Color.FromArgb(255, 226, 146, 57), 90f))
            using (Pen outlinePen = new Pen(Color.FromArgb(190, 121, 77, 33), Math.Max(1f, size * 0.012f)))
            {
                g.FillRectangle(frontBrush, frontRect);
                g.DrawRectangle(outlinePen, frontRect.X, frontRect.Y, frontRect.Width, frontRect.Height);
            }

            using (Pen seamPen = new Pen(Color.FromArgb(150, 127, 81, 35), Math.Max(1f, size * 0.011f)))
            {
                float seamX = boxX + (boxW / 2f);
                g.DrawLine(seamPen, seamX, boxY - topDepth * 0.25f, seamX, boxY + boxH);
            }

            float shieldW = size * 0.31f;
            float shieldH = size * 0.36f;
            float shieldX = size * 0.56f;
            float shieldY = size * 0.47f;

            using (GraphicsPath shieldPath = CreateShieldPath(shieldX, shieldY, shieldW, shieldH))
            using (LinearGradientBrush shieldBrush = new LinearGradientBrush(new RectangleF(shieldX, shieldY, shieldW, shieldH), Color.FromArgb(255, 27, 169, 111), Color.FromArgb(255, 16, 124, 83), 90f))
            using (Pen shieldPen = new Pen(Color.FromArgb(180, 225, 255, 244), Math.Max(1f, size * 0.01f)))
            {
                g.FillPath(shieldBrush, shieldPath);
                g.DrawPath(shieldPen, shieldPath);
            }

            using (Pen checkPen = new Pen(Color.FromArgb(255, 255, 255, 255), Math.Max(1.5f, size * 0.06f)))
            {
                checkPen.StartCap = LineCap.Round;
                checkPen.EndCap = LineCap.Round;
                checkPen.LineJoin = LineJoin.Round;

                PointF p1 = new PointF(shieldX + shieldW * 0.24f, shieldY + shieldH * 0.56f);
                PointF p2 = new PointF(shieldX + shieldW * 0.43f, shieldY + shieldH * 0.74f);
                PointF p3 = new PointF(shieldX + shieldW * 0.77f, shieldY + shieldH * 0.36f);

                g.DrawLines(checkPen, new PointF[] { p1, p2, p3 });
            }
        }

        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2f;
        RectangleF arc = new RectangleF(rect.X, rect.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CreateShieldPath(float x, float y, float w, float h)
    {
        GraphicsPath path = new GraphicsPath();
        path.AddPolygon(new PointF[]
        {
            new PointF(x + w * 0.50f, y),
            new PointF(x + w * 0.93f, y + h * 0.18f),
            new PointF(x + w * 0.85f, y + h * 0.72f),
            new PointF(x + w * 0.50f, y + h),
            new PointF(x + w * 0.15f, y + h * 0.72f),
            new PointF(x + w * 0.07f, y + h * 0.18f)
        });
        path.CloseFigure();
        return path;
    }
}
"@

Add-Type -TypeDefinition $iconCode -Language CSharp -ReferencedAssemblies System.Drawing

$parent = Split-Path -Parent $OutputPath
if (-not (Test-Path $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$absoluteOutput = Join-Path (Resolve-Path $parent).Path (Split-Path -Leaf $OutputPath)
[IntuneWinPackagerIconGenerator]::Generate($absoluteOutput)
Write-Output "Generated icon: $absoluteOutput"
