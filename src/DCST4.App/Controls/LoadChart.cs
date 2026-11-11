using DCST4.Core.Performance;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DCST4.Controls
{
    /// <summary>
    /// The live chart of a load test, drawn with DrawingContext so that no chart package is needed: stored and failed per
    /// second on top, latency p50 and p95 below, one y-axis each, on a shared axis of seconds. The warm-up is shaded and
    /// ramp steps are marked. Hovering shows the values of one second. It redraws when <see cref="Version"/> changes,
    /// which the view model does at most once a second.
    /// </summary>
    public sealed class LoadChart : FrameworkElement
    {
        // The reference palette of the dataviz method: categorical slots 1 and 2, the status colour for failures, chart ink.
        private static readonly Brush Surface = Frozen("#fcfcfb");
        private static readonly Brush Ink = Frozen("#0b0b0b");
        private static readonly Brush Secondary = Frozen("#52514e");
        private static readonly Brush Muted = Frozen("#898781");
        private static readonly Brush WarmUp = Frozen("#f0efec");
        private static readonly Pen Grid = FrozenPen("#e1e0d9", 1);
        private static readonly Pen Axis = FrozenPen("#c3c2b7", 1);
        private static readonly Pen StepLine = FrozenPen("#c3c2b7", 1, true);
        private static readonly Pen Crosshair = FrozenPen("#898781", 1);
        private static readonly Color Blue = (Color)ColorConverter.ConvertFromString("#2a78d6");
        private static readonly Color Orange = (Color)ColorConverter.ConvertFromString("#eb6834");
        private static readonly Color Critical = (Color)ColorConverter.ConvertFromString("#d03b3b");
        private static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private const double Left = 52;
        private const double Right = 12;
        private const double Top = 22;
        private const double Bottom = 22;
        private const double Gap = 30;
        /// <summary>A long soak is drawn as at most this many points, each the mean, or for latency the highest, of its seconds.</summary>
        private const int MaxPoints = 600;

        private double? _hoverX;

        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
            nameof(Series), typeof(IReadOnlyList<LoadSecond>), typeof(LoadChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
            nameof(Version), typeof(int), typeof(LoadChart), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<LoadSecond> Series
        {
            get => (IReadOnlyList<LoadSecond>)GetValue(SeriesProperty);
            set => SetValue(SeriesProperty, value);
        }

        /// <summary>Changed by the view model after it added seconds, so that the chart draws them.</summary>
        public int Version
        {
            get => (int)GetValue(VersionProperty);
            set => SetValue(VersionProperty, value);
        }

        public LoadChart()
        {
            ClipToBounds = true;
            MinHeight = 160;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _hoverX = e.GetPosition(this).X;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hoverX = null;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            dc.DrawRectangle(Surface, null, new Rect(0, 0, width, height));

            var all = Series?.ToList() ?? new List<LoadSecond>();
            var plotWidth = width - Left - Right;
            var panelHeight = (height - Top - Bottom - Gap) / 2;
            if (plotWidth < 40 || panelHeight < 30) return;

            var rows = Downsample(all);
            var lastSecond = Math.Max(10, all.Count == 0 ? 0 : all[all.Count - 1].Second + 1);
            Func<double, double> x = second => Left + second / lastSecond * plotWidth;

            var top = new Rect(Left, Top, plotWidth, panelHeight);
            var bottom = new Rect(Left, Top + panelHeight + Gap, plotWidth, panelHeight);

            // A partial last second is left out of the lines, so it does not set the scale either: a few slow images in it
            // would otherwise look like a trend. A second without responses has no latency.
            Func<LoadSecond, bool> whole = r => !r.Partial;
            Func<LoadSecond, bool> answered = r => r.Stored > 0 && !r.Partial;
            var counted = rows.Where(whole).ToList();
            var timed = rows.Where(answered).ToList();

            ShadeWarmUp(dc, all, x, top, true);
            ShadeWarmUp(dc, all, x, bottom, false);

            var throughputMax = NiceMax(counted.Count == 0 ? 0 : counted.Max(r => Math.Max(r.Stored, r.Failed)));
            var latencyMax = NiceMax(timed.Count == 0 ? 0 : timed.Max(r => r.LatencyP95Ms));
            DrawPanel(dc, top, throughputMax, "Instances per second", "0");
            DrawPanel(dc, bottom, latencyMax, "Latency of a C-STORE, ms", latencyMax < 10 ? "0.0" : "0");
            DrawSteps(dc, all, x, top, bottom);
            DrawTimeAxis(dc, bottom, lastSecond, x);

            DrawLine(dc, rows, r => r.Stored, whole, top, throughputMax, x, Blue);
            if (rows.Any(r => r.Failed > 0)) DrawLine(dc, rows, r => r.Failed, whole, top, throughputMax, x, Critical);
            DrawLine(dc, rows, r => r.LatencyP50Ms, answered, bottom, latencyMax, x, Blue);
            DrawLine(dc, rows, r => r.LatencyP95Ms, answered, bottom, latencyMax, x, Orange);

            DrawLegend(dc, top, new[] { Tuple.Create("stored", Blue), Tuple.Create("failed", Critical) }, all.Any(r => r.WarmUp));
            DrawLegend(dc, bottom, new[] { Tuple.Create("p50", Blue), Tuple.Create("p95", Orange) });

            if (all.Count == 0)
            {
                Text(dc, "The chart fills in once a second while a load test runs.", Muted, 12, new Point(Left + 12, Top + panelHeight / 2 - 8));
                return;
            }
            DrawHover(dc, all, x, top, bottom, lastSecond, plotWidth);
        }

        /// <summary>Every second while it fits, otherwise buckets: the mean of the counts and the highest latency, which a mean would hide.</summary>
        public static List<LoadSecond> Downsample(List<LoadSecond> all)
        {
            if (all.Count <= MaxPoints) return all;
            var size = (int)Math.Ceiling(all.Count / (double)MaxPoints);
            var result = new List<LoadSecond>();
            for (var i = 0; i < all.Count; i += size)
            {
                var bucket = all.Skip(i).Take(size).ToList();
                result.Add(new LoadSecond
                {
                    Second = bucket[bucket.Count / 2].Second,
                    Step = bucket[0].Step,
                    Partial = bucket.All(b => b.Partial),
                    Stored = (long)Math.Round(bucket.Average(b => b.Stored)),
                    Failed = (long)Math.Round(bucket.Average(b => b.Failed)),
                    LatencyP50Ms = bucket.Average(b => b.LatencyP50Ms),
                    LatencyP95Ms = bucket.Max(b => b.LatencyP95Ms),
                });
            }
            return result;
        }

        /// <summary>The warm-up is drawn but not in the summary. The band is named in the top panel when there is room for the word.</summary>
        private void ShadeWarmUp(DrawingContext dc, List<LoadSecond> all, Func<double, double> x, Rect panel, bool label)
        {
            foreach (var run in Runs(all, r => r.WarmUp))
            {
                var from = x(run.Item1);
                var to = x(run.Item2 + 1);
                dc.DrawRectangle(WarmUp, null, new Rect(from, panel.Top, Math.Max(0, to - from), panel.Height));
                if (!label) continue;
                var text = Format("warm-up", Muted, 10);
                if (text.Width + 6 <= to - from) dc.DrawText(text, new Point(from + 3, panel.Bottom - text.Height - 2));
            }
        }

        /// <summary>Consecutive seconds where the condition holds, as first and last second.</summary>
        private static IEnumerable<Tuple<int, int>> Runs(List<LoadSecond> all, Func<LoadSecond, bool> condition)
        {
            int? start = null;
            var previous = 0;
            foreach (var row in all)
            {
                if (condition(row))
                {
                    if (start == null) start = row.Second;
                    previous = row.Second;
                }
                else if (start != null)
                {
                    yield return Tuple.Create(start.Value, previous);
                    start = null;
                }
            }
            if (start != null) yield return Tuple.Create(start.Value, previous);
        }

        private void DrawPanel(DrawingContext dc, Rect panel, double max, string title, string format)
        {
            Text(dc, title, Secondary, 12, new Point(panel.Left, panel.Top - 18));
            for (var i = 0; i <= 4; i++)
            {
                var y = panel.Bottom - panel.Height * i / 4;
                dc.DrawLine(i == 0 ? Axis : Grid, new Point(panel.Left, y), new Point(panel.Right, y));
                var label = (max * i / 4).ToString(format, CultureInfo.InvariantCulture);
                var text = Format(label, Muted, 10);
                dc.DrawText(text, new Point(panel.Left - 6 - text.Width, y - text.Height / 2));
            }
        }

        private void DrawSteps(DrawingContext dc, List<LoadSecond> all, Func<double, double> x, Rect top, Rect bottom)
        {
            for (var i = 0; i < all.Count; i++)
            {
                var row = all[i];
                if (row.Step == 0 || (i > 0 && all[i - 1].Step == row.Step)) continue;
                var at = x(row.Second);
                if (i > 0)
                {
                    dc.DrawLine(StepLine, new Point(at, top.Top), new Point(at, top.Bottom));
                    dc.DrawLine(StepLine, new Point(at, bottom.Top), new Point(at, bottom.Bottom));
                }
                var open = all.Where(r => r.Step == row.Step).Max(r => r.OpenAssociations);
                var label = $"step {row.Step.ToString(CultureInfo.InvariantCulture)}, {open.ToString(CultureInfo.InvariantCulture)} assoc.";
                Text(dc, label, Muted, 10, new Point(at + 3, top.Top + 2));
            }
        }

        private void DrawTimeAxis(DrawingContext dc, Rect panel, double lastSecond, Func<double, double> x)
        {
            var step = NiceStep(lastSecond / 6);
            for (var s = 0.0; s <= lastSecond; s += step)
            {
                var text = Format(Seconds(s), Muted, 10);
                dc.DrawText(text, new Point(x(s) - text.Width / 2, panel.Bottom + 4));
            }
        }

        private static string Seconds(double s)
        {
            if (s >= 3600) return (s / 3600).ToString("0.#", CultureInfo.InvariantCulture) + " h";
            if (s >= 120) return (s / 60).ToString("0.#", CultureInfo.InvariantCulture) + " min";
            return s.ToString("0", CultureInfo.InvariantCulture) + " s";
        }

        /// <summary>
        /// One line per run of seconds that are drawn: it breaks where a ramp step changes, since the associations were
        /// opened again in between, and where a second is left out. A point standing alone gets a dot.
        /// </summary>
        private static void DrawLine(DrawingContext dc, List<LoadSecond> rows, Func<LoadSecond, double> value, Func<LoadSecond, bool> include, Rect panel, double max, Func<double, double> x, Color color)
        {
            if (rows.Count == 0 || max <= 0) return;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();

            var segment = new List<Point>();
            Action flush = () =>
            {
                if (segment.Count == 1) dc.DrawEllipse(brush, null, segment[0], 4, 4);
                if (segment.Count > 1)
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(segment[0], false, false);
                        context.PolyLineTo(segment.Skip(1).ToList(), true, true);
                    }
                    geometry.Freeze();
                    dc.DrawGeometry(null, pen, geometry);
                }
                segment.Clear();
            };

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (i > 0 && row.Step != rows[i - 1].Step) flush();
                if (!include(row))
                {
                    flush();
                    continue;
                }
                // A point sits in the middle of its second.
                segment.Add(new Point(x(row.Second + 0.5), panel.Bottom - Math.Min(1, value(row) / max) * panel.Height));
            }
            flush();
        }

        /// <summary>The series of a panel, right-aligned above it, and with <paramref name="warmUpKey"/> a swatch that names the shaded band.</summary>
        private void DrawLegend(DrawingContext dc, Rect panel, IEnumerable<Tuple<string, Color>> items, bool warmUpKey = false)
        {
            var right = panel.Right;
            foreach (var item in items.Reverse())
            {
                var text = Format(item.Item1, Secondary, 11);
                right -= text.Width;
                dc.DrawText(text, new Point(right, panel.Top - 18));
                right -= 18;
                var pen = new Pen(new SolidColorBrush(item.Item2), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, new Point(right, panel.Top - 10), new Point(right + 12, panel.Top - 10));
                right -= 14;
            }
            if (!warmUpKey) return;

            var key = Format("warm-up, not in the summary", Secondary, 11);
            right -= key.Width + 6;
            dc.DrawText(key, new Point(right, panel.Top - 18));
            right -= 16;
            dc.DrawRectangle(WarmUp, Axis, new Rect(right, panel.Top - 15, 12, 10));
        }

        private void DrawHover(DrawingContext dc, List<LoadSecond> all, Func<double, double> x, Rect top, Rect bottom, double lastSecond, double plotWidth)
        {
            if (_hoverX == null || _hoverX < Left || _hoverX > Left + plotWidth) return;
            var second = (int)Math.Floor((_hoverX.Value - Left) / plotWidth * lastSecond);
            var row = all.LastOrDefault(r => r.Second <= second);
            if (row == null) return;

            var at = x(row.Second + 0.5);
            dc.DrawLine(Crosshair, new Point(at, top.Top), new Point(at, top.Bottom));
            dc.DrawLine(Crosshair, new Point(at, bottom.Top), new Point(at, bottom.Bottom));

            var lines = new[]
            {
                $"{Seconds(row.Second)}{(row.Step > 0 ? ", step " + row.Step.ToString(CultureInfo.InvariantCulture) : string.Empty)}{(row.WarmUp ? ", warm-up" : string.Empty)}",
                $"stored {row.Stored.ToString(CultureInfo.InvariantCulture)}, failed {row.Failed.ToString(CultureInfo.InvariantCulture)}",
                $"{row.MegabytesPerSecond.ToString("0.0", CultureInfo.InvariantCulture)} MB/s, {row.OpenAssociations.ToString(CultureInfo.InvariantCulture)} associations",
                $"p50 {row.LatencyP50Ms.ToString("0.0", CultureInfo.InvariantCulture)} ms, p95 {row.LatencyP95Ms.ToString("0.0", CultureInfo.InvariantCulture)} ms",
            };
            var texts = lines.Select((l, i) => Format(l, i == 0 ? Ink : Secondary, 11)).ToList();
            var boxWidth = texts.Max(t => t.Width) + 16;
            var boxHeight = texts.Sum(t => t.Height) + 10;
            var boxX = at + 10 + boxWidth > Left + plotWidth ? at - 10 - boxWidth : at + 10;
            var box = new Rect(boxX, top.Top + 6, boxWidth, boxHeight);
            dc.DrawRoundedRectangle(Brushes.White, FrozenPen("#e1e0d9", 1), box, 4, 4);
            var y = box.Top + 5;
            foreach (var text in texts)
            {
                dc.DrawText(text, new Point(box.Left + 8, y));
                y += text.Height;
            }
        }

        /// <summary>1, 2 or 5 times a power of ten, at least the value, so the axis has round labels.</summary>
        public static double NiceMax(double value)
        {
            if (value <= 0) return 1;
            var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (var factor in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            {
                if (factor * power >= value) return factor * power;
            }
            return 10 * power;
        }

        private static double NiceStep(double value)
        {
            var steps = new[] { 1.0, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };
            return steps.FirstOrDefault(s => s >= value) > 0 ? steps.First(s => s >= value) : 14400;
        }

        private void Text(DrawingContext dc, string text, Brush brush, double size, Point at) => dc.DrawText(Format(text, brush, size), at);

        private FormattedText Format(string text, Brush brush, double size) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(string hex, double thickness, bool dashed = false)
        {
            var pen = new Pen(Frozen(hex), thickness);
            if (dashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            return pen;
        }
    }
}
