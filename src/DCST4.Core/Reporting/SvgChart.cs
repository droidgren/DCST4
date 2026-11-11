using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Reporting
{
    internal struct ChartPoint
    {
        public ChartPoint(double x, double y) { X = x; Y = y; }
        public readonly double X;
        public readonly double Y;
    }

    internal sealed class ChartSeries
    {
        public string Name;
        /// <summary>1, 2 or bad: which of the page's series colours the line takes.</summary>
        public string Colour = "1";
        /// <summary>Runs of points that are joined. A new run starts where the line must not be joined, as between ramp steps.</summary>
        public List<List<ChartPoint>> Runs = new List<List<ChartPoint>>();
    }

    /// <summary>A stretch of the x axis with a name, such as the warm-up, or a line across it when it has no width.</summary>
    internal sealed class ChartBand
    {
        public double From;
        public double To;
        public string Label;
    }

    /// <summary>What a reader sees when the pointer rests on a stretch of the x axis. The report has no script, so this is the SVG title.</summary>
    internal sealed class ChartHover
    {
        public double From;
        public double To;
        public string Text;
    }

    internal sealed class LineChart
    {
        public string Title;
        public string XLabel;
        public List<ChartSeries> Series = new List<ChartSeries>();
        public List<ChartBand> Bands = new List<ChartBand>();
        public List<ChartHover> Hovers = new List<ChartHover>();
    }

    internal sealed class ColumnChart
    {
        public string Title;
        public string XLabel;
        public string Colour = "1";
        public List<string> Categories = new List<string>();
        public List<double> Values = new List<double>();
        /// <summary>What is written above each column.</summary>
        public List<string> ValueLabels = new List<string>();
        public List<string> Hovers = new List<string>();
        /// <summary>The column to point out, or -1.</summary>
        public int Marked = -1;
        public string MarkedLabel;
    }

    /// <summary>
    /// Line and column charts as inline SVG, with no script and no font or image from outside. One y axis per chart: two
    /// measures of different scale are two charts side by side. Colours come from the page's style sheet, and text is never
    /// in a series colour.
    /// </summary>
    internal static class SvgChart
    {
        private const double Width = 600;
        private const double Height = 230;
        private const double Left = 56;
        private const double Right = 16;
        private const double Top = 18;
        private const double Bottom = 40;

        private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>A step of 1, 2 or 5 times a power of ten that gives about <paramref name="count"/> ticks up to <paramref name="max"/>.</summary>
        internal static double NiceStep(double max, int count)
        {
            if (!(max > 0) || double.IsInfinity(max)) return 1;
            var raw = max / Math.Max(1, count);
            var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            var fraction = raw / power;
            var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
            return nice * power;
        }

        private static string Tick(double value, double step)
        {
            if (value >= 10000) return (value / 1000).ToString(step >= 1000 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + "K";
            return value.ToString(step >= 1 ? "#,0" : step >= 0.1 ? "0.0" : "0.00", CultureInfo.InvariantCulture);
        }

        private static void Frame(HtmlWriter html, string title, string label, double yMax, double yStep)
        {
            html.Open("svg", "viewBox", $"0 0 {N(Width)} {N(Height)}", "width", N(Width), "height", N(Height), "role", "img", "aria-label", title).Line();
            for (var value = 0.0; value <= yMax + yStep / 1000; value += yStep)
            {
                var y = Top + (Height - Top - Bottom) * (1 - value / yMax);
                html.Empty("line", "class", value == 0 ? "axis" : "grid", "x1", N(Left), "x2", N(Width - Right), "y1", N(y), "y2", N(y));
                html.Element("text", Tick(value, yStep), "x", N(Left - 6), "y", N(y + 4), "text-anchor", "end");
            }
            if (label != null) html.Element("text", label, "class", "lab", "x", N(Left + (Width - Left - Right) / 2), "y", N(Height - 6), "text-anchor", "middle");
            html.Line();
        }

        private static void Caption(HtmlWriter html, string title, IEnumerable<ChartSeries> legend)
        {
            html.Open("figcaption").Text(title).Close("figcaption");
            var series = legend?.ToList();
            // One series needs no legend: the caption already says what is plotted.
            if (series == null || series.Count < 2) return;
            html.Open("div", "class", "legend");
            foreach (var s in series)
            {
                html.Open("span").Open("svg", "class", "swatch", "viewBox", "0 0 14 3", "width", "14", "height", "3")
                    .Empty("rect", "class", "f" + s.Colour, "width", "14", "height", "3", "rx", "1.5").Close("svg").Text(s.Name).Close("span");
            }
            html.Close("div");
        }

        public static void Write(HtmlWriter html, LineChart chart)
        {
            var points = chart.Series.SelectMany(s => s.Runs).SelectMany(r => r).ToList();
            html.Open("figure");
            Caption(html, chart.Title, chart.Series);
            if (points.Count == 0)
            {
                html.Element("p", "Nothing was measured.", "class", "muted").Close("figure").Line();
                return;
            }

            var xMin = Math.Min(points.Min(p => p.X), chart.Bands.Select(b => b.From).DefaultIfEmpty(double.MaxValue).Min());
            var xMax = Math.Max(points.Max(p => p.X), xMin + 1);
            var yStep = NiceStep(points.Max(p => p.Y), 4);
            var yMax = Math.Max(yStep, Math.Ceiling(points.Max(p => p.Y) / yStep - 1e-9) * yStep);
            var plotWidth = Width - Left - Right;
            var plotHeight = Height - Top - Bottom;
            Func<double, double> px = x => Left + plotWidth * (x - xMin) / (xMax - xMin);
            Func<double, double> py = y => Top + plotHeight * (1 - Math.Min(y, yMax) / yMax);

            Frame(html, chart.Title, chart.XLabel, yMax, yStep);

            foreach (var band in chart.Bands)
            {
                var from = px(Math.Max(band.From, xMin));
                var to = px(Math.Min(band.To, xMax));
                if (to - from >= 1) html.Empty("rect", "class", "shade", "x", N(from), "y", N(Top), "width", N(to - from), "height", N(plotHeight));
                else html.Empty("line", "class", "mark", "x1", N(from), "x2", N(from), "y1", N(Top), "y2", N(Top + plotHeight));
                if (band.Label != null) html.Element("text", band.Label, "x", N(from + 3), "y", N(Top - 5));
            }

            var xStep = Math.Max(1, NiceStep(xMax - xMin, 6));
            for (var value = Math.Ceiling(xMin / xStep) * xStep; value <= xMax + xStep / 1000; value += xStep)
            {
                html.Element("text", Tick(value, xStep), "x", N(px(value)), "y", N(Top + plotHeight + 14), "text-anchor", "middle");
            }
            html.Line();

            foreach (var series in chart.Series)
            {
                foreach (var run in series.Runs.Where(r => r.Count > 0))
                {
                    if (run.Count == 1)
                    {
                        html.Empty("circle", "class", "dot f" + series.Colour, "cx", N(px(run[0].X)), "cy", N(py(run[0].Y)), "r", "4");
                        continue;
                    }
                    html.Empty("polyline", "class", "line s" + series.Colour, "points", string.Join(" ", run.Select(p => N(px(p.X)) + "," + N(py(p.Y)))));
                }
                html.Line();
            }

            // Above the lines, so that the pointer finds them anywhere in the column of one x value.
            foreach (var hover in chart.Hovers)
            {
                var from = px(Math.Max(hover.From, xMin));
                var to = px(Math.Min(hover.To, xMax));
                if (to <= from) continue;
                html.Open("rect", "class", "hit", "x", N(from), "y", N(Top), "width", N(to - from), "height", N(plotHeight)).Element("title", hover.Text).Close("rect");
            }
            html.Close("svg").Close("figure").Line();
        }

        public static void Write(HtmlWriter html, ColumnChart chart)
        {
            html.Open("figure");
            Caption(html, chart.Title, null);
            if (chart.Values.Count == 0)
            {
                html.Element("p", "Nothing was measured.", "class", "muted").Close("figure").Line();
                return;
            }

            var highest = chart.Values.Max();
            var yStep = NiceStep(highest, 4);
            var yMax = Math.Max(yStep, Math.Ceiling(highest / yStep - 1e-9) * yStep);
            // Room above the tallest column for its value.
            if (highest > yMax * 0.9) yMax += yStep;
            var plotWidth = Width - Left - Right;
            var plotHeight = Height - Top - Bottom;
            var slot = plotWidth / chart.Values.Count;
            var bar = Math.Min(24, slot * 0.6);

            Frame(html, chart.Title, chart.XLabel, yMax, yStep);

            for (var i = 0; i < chart.Values.Count; i++)
            {
                var centre = Left + slot * (i + 0.5);
                var height = plotHeight * Math.Max(0, chart.Values[i]) / yMax;
                var top = Top + plotHeight - height;
                var radius = Math.Min(4, Math.Min(bar / 2, height));
                // Rounded at the data end, square at the baseline.
                var path = $"M{N(centre - bar / 2)},{N(Top + plotHeight)} V{N(top + radius)} Q{N(centre - bar / 2)},{N(top)} {N(centre - bar / 2 + radius)},{N(top)} " +
                           $"H{N(centre + bar / 2 - radius)} Q{N(centre + bar / 2)},{N(top)} {N(centre + bar / 2)},{N(top + radius)} V{N(Top + plotHeight)} Z";
                if (height > 0) html.Empty("path", "class", "f" + chart.Colour, "d", path);
                if (i < chart.ValueLabels.Count) html.Element("text", chart.ValueLabels[i], "class", "lab", "x", N(centre), "y", N(top - 5), "text-anchor", "middle");
                html.Element("text", chart.Categories[i], "x", N(centre), "y", N(Top + plotHeight + 14), "text-anchor", "middle");
                if (i == chart.Marked && chart.MarkedLabel != null)
                {
                    html.Element("text", chart.MarkedLabel, "class", "lab", "x", N(centre), "y", N(Top + plotHeight + 26), "text-anchor", "middle");
                }
                if (i < chart.Hovers.Count)
                {
                    html.Open("rect", "class", "hit", "x", N(centre - slot / 2), "y", N(Top), "width", N(slot), "height", N(plotHeight)).Element("title", chart.Hovers[i]).Close("rect");
                }
                html.Line();
            }
            html.Close("svg").Close("figure").Line();
        }
    }
}
