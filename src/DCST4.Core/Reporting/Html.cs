using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace DCST4.Core.Reporting
{
    /// <summary>
    /// The only way text gets into a report page. Element and attribute names come from this code alone, and every value,
    /// whether it is our own or came from a dataset or the system under test, such as an Error Comment, is encoded on the way in.
    /// </summary>
    public sealed class HtmlWriter
    {
        private readonly StringBuilder _text = new StringBuilder();

        /// <summary>Encodes for element content and for a quoted attribute value alike: &lt; &gt; &amp; " and '.</summary>
        public static string Encode(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            // A control character is not allowed in HTML and has no business in a report. Shown as U+FFFD, so that it is seen.
            StringBuilder cleaned = null;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var bad = (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c == '\uFFFE' || c == '\uFFFF';
                if (!bad && char.IsSurrogate(c))
                {
                    // A lone surrogate cannot be written as UTF-8.
                    var paired = char.IsHighSurrogate(c) ? i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) : i > 0 && char.IsHighSurrogate(value[i - 1]);
                    bad = !paired;
                }
                if (bad && cleaned == null) cleaned = new StringBuilder(value, 0, i, value.Length);
                if (cleaned != null) cleaned.Append(bad ? '\uFFFD' : c);
            }
            return WebUtility.HtmlEncode(cleaned == null ? value : cleaned.ToString());
        }

        /// <summary>Opens an element. The attributes are name and value in turn, and a null value leaves the attribute out.</summary>
        public HtmlWriter Open(string tag, params string[] attributes)
        {
            _text.Append('<').Append(Name(tag));
            for (var i = 0; i + 1 < attributes.Length; i += 2)
            {
                if (attributes[i + 1] == null) continue;
                _text.Append(' ').Append(Name(attributes[i])).Append("=\"").Append(Encode(attributes[i + 1])).Append('"');
            }
            _text.Append('>');
            return this;
        }

        public HtmlWriter Close(string tag)
        {
            _text.Append("</").Append(Name(tag)).Append('>');
            return this;
        }

        /// <summary>An element that has no content and no end tag, such as meta, or an SVG shape.</summary>
        public HtmlWriter Empty(string tag, params string[] attributes)
        {
            Open(tag, attributes);
            _text.Insert(_text.Length - 1, '/');
            return this;
        }

        public HtmlWriter Text(string value)
        {
            _text.Append(Encode(value));
            return this;
        }

        /// <summary>An element with text in it.</summary>
        public HtmlWriter Element(string tag, string text, params string[] attributes) => Open(tag, attributes).Text(text).Close(tag);

        public HtmlWriter Line()
        {
            _text.Append('\n');
            return this;
        }

        /// <summary>
        /// Only for the style sheet, which is a constant of this assembly. It must not hold anything that came from a result file.
        /// </summary>
        internal HtmlWriter Constant(string markup)
        {
            _text.Append(markup);
            return this;
        }

        public override string ToString() => _text.ToString();

        /// <summary>A name is one of ours, so anything but letters, digits, hyphen and colon is a bug here and not bad input.</summary>
        private static string Name(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("An element or attribute needs a name.");
            foreach (var c in name)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == ':')) throw new ArgumentException($"'{name}' is not a name.");
            }
            return name;
        }
    }

    /// <summary>How numbers and times are written in a report: the same everywhere, whatever the computer's language is.</summary>
    internal static class ReportFormat
    {
        public static string Number(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);
        public static string Number(long? value) => value.HasValue ? Number(value.Value) : string.Empty;
        public static string Plain(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Three significant figures below 100, none after the point above.</summary>
        public static string Decimal(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "-";
            var magnitude = Math.Abs(value);
            var format = magnitude >= 100 ? "#,0" : magnitude >= 10 ? "0.0" : magnitude >= 1 ? "0.00" : "0.###";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        public static string Ms(double value) => Decimal(value) + " ms";

        public static string Percent(double value, bool signed = false)
        {
            var text = Math.Abs(value) >= 100 ? value.ToString("#,0", CultureInfo.InvariantCulture) : value.ToString("0.0", CultureInfo.InvariantCulture);
            return (signed && value > 0 ? "+" : string.Empty) + text.Replace("-", "\u2212") + " %";
        }

        public static string Time(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

        public static string Duration(long milliseconds)
        {
            if (milliseconds < 1000) return Plain(milliseconds) + " ms";
            var span = TimeSpan.FromMilliseconds(milliseconds);
            if (span.TotalSeconds < 60) return span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            if (span.TotalHours < 1) return $"{span.Minutes} min {span.Seconds} s";
            return $"{(int)span.TotalHours} h {span.Minutes} min";
        }

        public static string YesNo(bool value) => value ? "yes" : "no";

        /// <summary>Keys in ordinal order: a dictionary read back from a file no longer has the comparer it was written with.</summary>
        public static IEnumerable<KeyValuePair<string, TValue>> Ordered<TValue>(IDictionary<string, TValue> values)
        {
            if (values == null) yield break;
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys) yield return new KeyValuePair<string, TValue>(key, values[key]);
        }
    }
}
