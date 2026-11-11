using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DCST4.Core.Performance
{
    /// <summary>
    /// A CSV file that grows while the run goes on, so that what was measured survives a crash. UTF-8 without BOM, invariant
    /// culture. <see cref="Flush"/> with <c>toDisk</c> also asks Windows to write it through to the disk.
    /// </summary>
    public sealed class MetricsFileWriter : IDisposable
    {
        public static readonly string[] SeriesColumns =
        {
            "second", "step", "warm_up", "partial", "open_associations", "attempts", "stored", "failed", "bytes", "mb_per_s",
            "latency_p50_ms", "latency_p95_ms", "latency_max_ms", "client_cpu_percent",
        };

        public static readonly string[] WindowColumns =
        {
            "window", "start_second", "end_second", "attempts", "stored", "stored_per_s", "mb_per_s",
            "latency_p50_ms", "latency_p95_ms", "latency_p99_ms", "latency_max_ms", "not_stored", "association_problems",
        };

        private readonly FileStream _file;
        private readonly StreamWriter _writer;
        private readonly object _sync = new object();

        public MetricsFileWriter(string path, IEnumerable<string> columns)
        {
            Path = path;
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_file, new UTF8Encoding(false)) { NewLine = "\r\n" };
            _writer.WriteLine(string.Join(",", columns));
            Flush(false);
        }

        public string Path { get; }

        public void WriteSecond(LoadSecond row) => WriteRow(new object[]
        {
            row.Second, row.Step, row.WarmUp ? 1 : 0, row.Partial ? 1 : 0, row.OpenAssociations, row.Attempts, row.Stored, row.Failed, row.Bytes,
            row.MegabytesPerSecond, row.LatencyP50Ms, row.LatencyP95Ms, row.LatencyMaxMs, row.ClientCpuPercent,
        });

        public void WriteWindow(SoakWindow window)
        {
            var s = window.Summary;
            WriteRow(new object[]
            {
                window.Window, window.StartSecond, window.EndSecond, s.Attempts, s.Stored, s.StoredPerSecond, s.MegabytesPerSecond,
                s.Latency.P50Ms, s.Latency.P95Ms, s.Latency.P99Ms, s.Latency.MaxMs, s.Errors.NotStored, s.Errors.AssociationRejected + s.Errors.ConnectFailed,
            });
        }

        private void WriteRow(IEnumerable<object> values)
        {
            lock (_sync) _writer.WriteLine(string.Join(",", values.Select(Format)));
        }

        public void Flush(bool toDisk)
        {
            lock (_sync)
            {
                _writer.Flush();
                _file.Flush(toDisk);
            }
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case double d: return d.ToString("0.###", CultureInfo.InvariantCulture);
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try
                {
                    _writer.Flush();
                    _file.Flush(true);
                }
                catch (IOException)
                {
                }
                _writer.Dispose();
            }
        }
    }
}
