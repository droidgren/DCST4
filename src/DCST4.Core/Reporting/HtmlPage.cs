using DCST4.Core.Testing;
using System;

namespace DCST4.Core.Reporting
{
    /// <summary>
    /// The shell that the run report and the comparison share: one file with its style sheet inside, no script and nothing
    /// fetched from anywhere. The Content-Security-Policy is a second line of defence: should markup from a result file ever
    /// get past <see cref="HtmlWriter.Encode"/>, the browser still loads and runs nothing.
    /// </summary>
    internal static class HtmlPage
    {
        public const string ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:";

        // A constant of this assembly. Colours follow the roles of the chart palette: series 1 and 2 for the charts, and the
        // status colours for the result classes, each of which is always written out in words as well.
        private const string Style = @"
:root{color-scheme:light;--page:#f9f9f7;--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--muted:#898781;--grid:#e1e0d9;--axis:#c3c2b7;--border:rgba(11,11,11,.12);
--series1:#2a78d6;--series2:#eb6834;--good:#0ca30c;--warning:#fab219;--serious:#ec835a;--critical:#d03b3b;--neutral:#898781;
--good-bg:#e3f4e3;--warning-bg:#fdf0cf;--serious-bg:#fbe5db;--critical-bg:#f7dcdc;--neutral-bg:#ecebe7;--info-bg:#dfeafa}
@media (prefers-color-scheme:dark){:root{color-scheme:dark;--page:#0d0d0d;--surface:#1a1a19;--ink:#f0efec;--ink2:#c3c2b7;--muted:#898781;--grid:#2c2c2a;--axis:#383835;--border:rgba(255,255,255,.14);
--series1:#3987e5;--series2:#d95926;--good-bg:#173a17;--warning-bg:#4a3a0c;--serious-bg:#4a2a1c;--critical-bg:#4a1c1c;--neutral-bg:#2c2c2a;--info-bg:#1b2f4d}}
*{box-sizing:border-box}
body{margin:0;padding:24px;background:var(--page);color:var(--ink);font:14px/1.45 system-ui,-apple-system,'Segoe UI',sans-serif}
main{max-width:1280px;margin:0 auto}
h1{font-size:24px;margin:0 0 4px}
h2{font-size:18px;margin:0 0 12px}
h3{font-size:15px;margin:18px 0 8px}
p{margin:6px 0}
section{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:16px 20px;margin:16px 0}
nav{margin:12px 0;color:var(--ink2)}
nav a{margin-right:14px}
a{color:var(--series1)}
.sub{color:var(--ink2)}
.muted{color:var(--muted)}
.scroll{overflow-x:auto}
table{border-collapse:collapse;margin:8px 0;font-variant-numeric:tabular-nums}
th,td{text-align:left;vertical-align:top;padding:4px 10px;border-bottom:1px solid var(--grid)}
th{color:var(--ink2);font-weight:600;white-space:nowrap}
td.num,th.num{text-align:right;white-space:nowrap}
td.wrap{overflow-wrap:anywhere;max-width:640px}
td.id{white-space:nowrap}
code,.mono{font-family:Consolas,'Cascadia Mono',monospace;font-size:13px}
.kv th{font-weight:400;color:var(--ink2);padding-left:0}
.kv td,.kv th{border-bottom:none;padding-top:2px;padding-bottom:2px}
.badge{display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;font-weight:600;white-space:nowrap;border:1px solid var(--border)}
.r-pass{background:var(--good-bg)}.r-fail{background:var(--critical-bg)}.r-notsupported{background:var(--neutral-bg)}.r-expected{background:var(--info-bg)}
.r-timeout{background:var(--serious-bg)}.r-indeterminate{background:var(--warning-bg)}.r-toolerror{background:var(--warning-bg)}
.tiles{display:flex;flex-wrap:wrap;gap:10px;margin:8px 0}
.tile{border:1px solid var(--border);border-radius:8px;padding:8px 14px;min-width:120px}
.tile .value{font-size:22px;font-weight:600}
.tile .label{font-size:12px;color:var(--ink2)}
.verdict{font-size:16px;font-weight:600;padding:10px 14px;border-radius:8px;border:1px solid var(--border)}
.verdict.bad{background:var(--critical-bg)}.verdict.ok{background:var(--good-bg)}
.matrix th.syntax{font-weight:400;font-size:12px;white-space:nowrap;text-align:center;padding:4px 3px;min-width:44px}
.matrix td.cell{text-align:center;padding:2px 3px;font-size:12px;font-weight:600;border:1px solid var(--surface)}
.c-accepted{background:var(--good-bg)}.c-rejected{background:var(--neutral-bg);color:var(--ink2)}.c-broken{background:var(--critical-bg)}.c-unknown{background:var(--warning-bg)}
.legend{display:flex;flex-wrap:wrap;gap:6px 16px;margin:6px 0;color:var(--ink2);font-size:12px}
.legend .key{display:inline-block;min-width:22px;text-align:center;padding:0 4px;border-radius:3px;font-weight:600;margin-right:4px}
.swatch{display:inline-block;width:14px;height:3px;border-radius:2px;vertical-align:middle;margin-right:5px}
.flag{font-weight:600}
tr.regression td{background:var(--critical-bg)}
tr.improved td{background:var(--good-bg)}
details{margin:6px 0}
summary{cursor:pointer;color:var(--ink2)}
.charts{display:flex;flex-wrap:wrap;gap:16px}
figure{margin:8px 0}
figcaption{font-weight:600;margin-bottom:2px}
svg{display:block;max-width:100%;height:auto}
svg text{font:11px system-ui,-apple-system,'Segoe UI',sans-serif;fill:var(--muted)}
svg text.lab{fill:var(--ink2)}
svg .grid{stroke:var(--grid);stroke-width:1}
svg .axis{stroke:var(--axis);stroke-width:1}
svg .s1{stroke:var(--series1)}svg .s2{stroke:var(--series2)}svg .sbad{stroke:var(--critical)}
svg .f1{fill:var(--series1)}svg .f2{fill:var(--series2)}svg .fbad{fill:var(--critical)}
svg .line{fill:none;stroke-width:2;stroke-linejoin:round;stroke-linecap:round}
svg .dot{stroke:var(--surface);stroke-width:2}
svg .shade{fill:var(--muted);opacity:.12}
svg .hit{fill:transparent}
svg .mark{stroke:var(--ink2);stroke-width:1}
footer{color:var(--muted);margin:16px 0;font-size:12px}
@media print{body{padding:0;background:#fff}section{break-inside:avoid-page;border:none;padding:8px 0}details>summary{list-style:none}nav{display:none}}
";

        /// <summary>Starts the page and leaves it open inside main. End it with <see cref="End"/>.</summary>
        public static HtmlWriter Begin(string title)
        {
            var html = new HtmlWriter();
            html.Constant("<!DOCTYPE html>\n");
            html.Open("html", "lang", "en").Line();
            html.Open("head").Line();
            html.Empty("meta", "charset", "utf-8").Line();
            html.Empty("meta", "http-equiv", "Content-Security-Policy", "content", ContentSecurityPolicy).Line();
            html.Empty("meta", "name", "viewport", "content", "width=device-width, initial-scale=1").Line();
            html.Empty("meta", "name", "generator", "content", "DCST4 " + ProductInfo.Version).Line();
            html.Element("title", title).Line();
            html.Open("style").Constant(Style).Close("style").Line();
            html.Close("head").Line();
            html.Open("body").Line().Open("main").Line();
            return html;
        }

        public static string End(HtmlWriter html)
        {
            // The one link that leaves the page. It is a constant of this assembly, and it is followed only when someone
            // clicks it: the page still loads nothing.
            html.Open("footer").Text("Generated by " + ProductInfo.FullName + " " + ProductInfo.Version + " at " + ReportFormat.Time(DateTime.UtcNow) + ", ")
                .Element("a", ProductInfo.ProjectUrl, "href", ProductInfo.ProjectUrl, "rel", "noopener noreferrer")
                .Text(". This file is complete in itself: it loads nothing from anywhere and holds no script.").Close("footer").Line();
            html.Close("main").Line().Close("body").Line().Close("html").Line();
            return html.ToString();
        }

        public static string ResultClass(TestResultClass result)
        {
            switch (result)
            {
                case TestResultClass.PASS: return "r-pass";
                case TestResultClass.FAIL: return "r-fail";
                case TestResultClass.NOT_SUPPORTED: return "r-notsupported";
                case TestResultClass.EXPECTED_FAILURE: return "r-expected";
                case TestResultClass.TIMEOUT: return "r-timeout";
                case TestResultClass.INDETERMINATE: return "r-indeterminate";
                default: return "r-toolerror";
            }
        }

        /// <summary>The result class as a badge. The name is always written out, so the colour never carries it alone.</summary>
        public static HtmlWriter Badge(this HtmlWriter html, TestResultClass result) =>
            html.Element("span", result.ToString(), "class", "badge " + ResultClass(result));

        public static HtmlWriter Badge(this HtmlWriter html, TestResultClass? result) =>
            result.HasValue ? html.Badge(result.Value) : html.Element("span", "not run", "class", "muted");

        /// <summary>A row of a table of names and values. Nothing is written when the value is empty.</summary>
        public static HtmlWriter Pair(this HtmlWriter html, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return html;
            return html.Open("tr").Element("th", name).Element("td", value, "class", "wrap").Close("tr").Line();
        }

        public static HtmlWriter HeaderRow(this HtmlWriter html, params string[] names)
        {
            html.Open("thead").Open("tr");
            foreach (var name in names)
            {
                // A name that ends in # is a column of numbers.
                var number = name.EndsWith("#", StringComparison.Ordinal);
                html.Element("th", number ? name.Substring(0, name.Length - 1) : name, "class", number ? "num" : null);
            }
            return html.Close("tr").Close("thead").Line();
        }

        public static HtmlWriter Cell(this HtmlWriter html, string text, string cssClass = null) => html.Element("td", text, "class", cssClass);
        public static HtmlWriter Num(this HtmlWriter html, string text) => html.Element("td", text, "class", "num");
    }
}
