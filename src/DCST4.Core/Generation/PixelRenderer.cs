using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace DCST4.Core.Generation
{
    public sealed class PixelRenderSpec
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        /// <summary>8 or 16.</summary>
        public int BitsAllocated { get; set; }
        /// <summary>White in the image maps to this stored value.</summary>
        public int MaxStoredValue { get; set; }
        /// <summary>Text burned into the top left corner, one entry per line. Null or empty draws no text box.</summary>
        public IReadOnlyList<string> Lines { get; set; }
        /// <summary>Position in the stack, 0 for the first image.</summary>
        public int ImageIndex { get; set; }
        public int ImagesInSeries { get; set; } = 1;
    }

    public sealed class RenderedPixels
    {
        /// <summary>Monochrome little endian: one byte per pixel at 8 bits allocated, otherwise two.</summary>
        public byte[] Data { get; set; }
        public int MinValue { get; set; }
        public int MaxValue { get; set; }
    }

    /// <summary>
    /// Draws the visible test image. It is deliberately not image-like: it must be obvious at a glance that this is
    /// a test image, and any burned-in text must be easy to compare with the tags in a PACS viewer.
    /// </summary>
    public static class PixelRenderer
    {
        private const string FontFamilyName = "Consolas";

        public static RenderedPixels Render(PixelRenderSpec spec)
        {
            using (var bitmap = new Bitmap(spec.Columns, spec.Rows, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    // Grayscale anti-aliasing only: ClearType would produce colour fringes.
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    DrawBackground(g, spec);
                    DrawSliceMarker(g, spec);
                    DrawGrayscaleBar(g, spec);
                    DrawWatermark(g, spec);
                    DrawTextBlock(g, spec);
                }
                return ToPixels(bitmap, spec);
            }
        }

        private static void DrawBackground(Graphics g, PixelRenderSpec spec)
        {
            g.Clear(Color.FromArgb(24, 24, 24));
            var step = Math.Max(16, Math.Min(spec.Rows, spec.Columns) / 8);
            using (var pen = new Pen(Color.FromArgb(70, 70, 70), 1))
            {
                for (var x = step; x < spec.Columns; x += step) g.DrawLine(pen, x, 0, x, spec.Rows);
                for (var y = step; y < spec.Rows; y += step) g.DrawLine(pen, 0, y, spec.Columns, y);
            }
        }

        /// <summary>A sphere cut by the slice plane: its size follows the position in the stack, so scrolling shows it growing and shrinking.</summary>
        private static void DrawSliceMarker(Graphics g, PixelRenderSpec spec)
        {
            var size = Math.Min(spec.Rows, spec.Columns);
            var position = spec.ImagesInSeries > 1 ? 2.0 * spec.ImageIndex / (spec.ImagesInSeries - 1) - 1.0 : 0.0;
            var radius = (float)(size * 0.32 * Math.Sqrt(Math.Max(0.02, 1 - position * position)));
            var centerX = spec.Columns / 2f;
            var centerY = spec.Rows / 2f;
            using (var brush = new SolidBrush(Color.FromArgb(120, 120, 120)))
            using (var pen = new Pen(Color.FromArgb(200, 200, 200), Math.Max(1f, size / 256f)))
            {
                g.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2, radius * 2);
                g.DrawEllipse(pen, centerX - radius, centerY - radius, radius * 2, radius * 2);
            }
        }

        private static void DrawGrayscaleBar(Graphics g, PixelRenderSpec spec)
        {
            const int steps = 16;
            var height = Math.Max(4, spec.Rows / 24);
            var top = spec.Rows - height;
            for (var i = 0; i < steps; i++)
            {
                var left = spec.Columns * i / steps;
                var right = spec.Columns * (i + 1) / steps;
                var level = 255 * i / (steps - 1);
                using (var brush = new SolidBrush(Color.FromArgb(level, level, level)))
                {
                    g.FillRectangle(brush, left, top, right - left, height);
                }
            }
        }

        private static void DrawWatermark(Graphics g, PixelRenderSpec spec)
        {
            const string text = "TEST IMAGE";
            var diagonal = (float)Math.Sqrt(spec.Rows * spec.Rows + spec.Columns * spec.Columns);
            var emSize = Math.Max(8f, diagonal / text.Length * 1.1f);
            using (var font = new Font(FontFamilyName, emSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            {
                var state = g.Save();
                g.TranslateTransform(spec.Columns / 2f, spec.Rows / 2f);
                g.RotateTransform((float)(-Math.Atan2(spec.Rows, spec.Columns) * 180 / Math.PI));
                var measured = g.MeasureString(text, font);
                g.DrawString(text, font, brush, -measured.Width / 2, -measured.Height / 2);
                g.Restore(state);
            }
        }

        private static void DrawTextBlock(Graphics g, PixelRenderSpec spec)
        {
            var lines = spec.Lines ?? new string[0];
            if (lines.Count == 0) return;

            var margin = Math.Max(2f, Math.Min(spec.Rows, spec.Columns) / 64f);
            var available = spec.Columns - 3 * margin;
            var emSize = Math.Max(7f, Math.Min(spec.Rows, spec.Columns) / 34f);

            // Shrink until the longest line fits, down to the smallest size that can still be read.
            using (var format = StringFormat.GenericTypographic)
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                while (emSize > 6f)
                {
                    using (var font = new Font(FontFamilyName, emSize, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        if (lines.Max(l => g.MeasureString(l, font, PointF.Empty, format).Width) <= available) break;
                    }
                    emSize -= 0.5f;
                }

                using (var font = new Font(FontFamilyName, emSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var background = new SolidBrush(Color.Black))
                using (var white = new SolidBrush(Color.White))
                {
                    var lineHeight = font.GetHeight(g);
                    var blockWidth = Math.Min(available, lines.Max(l => g.MeasureString(l, font, PointF.Empty, format).Width)) + margin;
                    g.FillRectangle(background, 0, 0, blockWidth + margin, lineHeight * lines.Count + 2 * margin);

                    for (var i = 0; i < lines.Count; i++)
                    {
                        g.DrawString(lines[i], font, white, margin, margin + i * lineHeight, format);
                    }
                }
            }
        }

        private static RenderedPixels ToPixels(Bitmap bitmap, PixelRenderSpec spec)
        {
            var bytesPerPixel = spec.BitsAllocated <= 8 ? 1 : 2;
            var data = new byte[spec.Rows * spec.Columns * bytesPerPixel];
            var min = int.MaxValue;
            var max = int.MinValue;

            var rectangle = new Rectangle(0, 0, spec.Columns, spec.Rows);
            var locked = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[Math.Abs(locked.Stride)];
                var target = 0;
                for (var y = 0; y < spec.Rows; y++)
                {
                    Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length);
                    for (var x = 0; x < spec.Columns; x++)
                    {
                        // Everything drawn is gray, so all three channels hold the same level.
                        var level = row[x * 3 + 1];
                        var value = (level * spec.MaxStoredValue + 127) / 255;
                        if (value < min) min = value;
                        if (value > max) max = value;
                        if (bytesPerPixel == 1)
                        {
                            data[target++] = (byte)value;
                        }
                        else
                        {
                            data[target++] = (byte)value;
                            data[target++] = (byte)(value >> 8);
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(locked);
            }

            return new RenderedPixels { Data = data, MinValue = min, MaxValue = max };
        }
    }
}
