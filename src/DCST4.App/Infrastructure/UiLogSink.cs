using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace DCST4.Infrastructure
{
    /// <summary>
    /// Collects log lines for the Log tab. Takes this program's own messages and anything at Warning or above. fo-dicom's
    /// per-PDU detail stays in the log file only. Lines are picked up by a timer on the UI thread, so a fast run does not
    /// flood the dispatcher.
    /// </summary>
    public sealed class UiLogSink : ILogEventSink
    {
        private static readonly MessageTemplateTextFormatter MessageFormatter = new MessageTemplateTextFormatter("{Message:lj}");

        public static UiLogSink Instance { get; } = new UiLogSink();

        private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level < LogEventLevel.Information) return;
            if (logEvent.Level < LogEventLevel.Warning && !IsOwnMessage(logEvent)) return;

            string message;
            using (var writer = new StringWriter())
            {
                // Same as the log file: :lj renders strings without quotes.
                MessageFormatter.Format(logEvent, writer);
                message = writer.ToString();
            }

            var line = $"{logEvent.Timestamp:HH:mm:ss.fff} [{Short(logEvent.Level)}] {message}";
            if (logEvent.Exception != null) line += " | " + logEvent.Exception.GetBaseException().Message;
            _lines.Enqueue(line);
        }

        public List<string> Drain()
        {
            var drained = new List<string>();
            while (_lines.TryDequeue(out var line)) drained.Add(line);
            return drained;
        }

        private static bool IsOwnMessage(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue("SourceContext", out var value)) return false;
            return value.ToString().Trim('"').StartsWith("DCST4", StringComparison.Ordinal);
        }

        private static string Short(LogEventLevel level)
        {
            switch (level)
            {
                case LogEventLevel.Warning: return "WRN";
                case LogEventLevel.Error: return "ERR";
                case LogEventLevel.Fatal: return "FTL";
                default: return "INF";
            }
        }
    }
}
