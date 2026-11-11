using DCST4.Core.Network;
using FellowOakDicom;
using FellowOakDicom.Network.Tls;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Performance
{
    /// <summary>What one load run, ramp step or soak does.</summary>
    internal sealed class LoadRunOptions
    {
        public DicomEndpoint Endpoint;
        public NetworkOptions Network;
        public InstancePool Pool;
        public string UidRoot;
        public string RunNonce;
        public int Associations;
        public int InstancesPerAssociation;
        /// <summary>0 runs until the budget is used up.</summary>
        public double DurationSeconds;
        /// <summary>The most C-STOREs this run may start.</summary>
        public int Budget;
        public double RatePerSecond;
        public uint MaxPdu;
        public double WarmUpSeconds;
        /// <summary>The ramp step, counted from 1, or 0.</summary>
        public int Step;
        /// <summary>Added to the worker numbers, so that the workers of two ramp steps never share UIDs.</summary>
        public int WorkerOffset;
        /// <summary>Added to the seconds of the series, so that the steps of a ramp follow each other.</summary>
        public int SecondOffset;
        /// <summary>0 takes no soak snapshots.</summary>
        public double SnapshotSeconds;
        public Action<LoadSecond> OnSecond;
        public Action<SoakWindow> OnWindow;
        /// <summary>A stored instance and when its response arrived, for the time-to-available poller.</summary>
        public Action<StampedInstance, long> OnStored;
    }

    internal enum LoadFatal { None, NotSupported, ConnectFailed, Timeout, ToolError }

    internal sealed class LoadRunOutcome
    {
        public LoadSummary Summary;
        public MetricsCollector Collector;
        public List<SoakWindow> Windows = new List<SoakWindow>();
        /// <summary>duration, count, cancelled, workers-stopped or the fatal reason.</summary>
        public string StoppedBy;
        public bool Cancelled;
        public LoadFatal Fatal;
        public string FatalMessage;
        public List<NegotiatedContext> Contexts = new List<NegotiatedContext>();
        /// <summary>What went wrong first, with its raw status and Error Comment, and in which second. Null if nothing did.</summary>
        public string FirstProblem;
        public SortedSet<ushort> Statuses = new SortedSet<ushort>();
        public int Seconds;
    }

    /// <summary>
    /// Sends the pool on N associations at once, each with its own worker and one C-STORE outstanding at a time, so the
    /// latency of a C-STORE holds no time spent waiting inside DCST4. Nothing is logged per instance.
    ///
    /// A cancel stops new requests at once, lets an outstanding one finish for up to <see cref="CancelGraceMs"/> and then
    /// releases. Whatever is still open after that is aborted. The end of the duration lets outstanding requests finish
    /// within the response timeout, since their responses are part of the result.
    /// </summary>
    internal sealed class LoadEngine
    {
        public const int CancelGraceMs = 2500;
        private const int ReleaseTimeoutMs = 2000;
        private const int ReconnectDelayMs = 1000;
        private const int MaxConsecutiveOpenFailures = 10;
        private const int LoggedFailures = 10;

        private static readonly ILogger Logger = Log.ForContext<LoadEngine>();

        private readonly LoadRunOptions _options;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly CancellationTokenSource _hard = new CancellationTokenSource();
        private readonly LoadRunOutcome _outcome = new LoadRunOutcome();
        private readonly object _sync = new object();
        private RateGate _gate;
        private MetricsCollector _collector;
        private ITlsInitiator _tls;
        private int _claimed;
        private int _openFailures;
        private int _everAccepted;
        private int _loggedFailures;
        private volatile string _stoppedBy;
        private long _lastWindowTicks;

        public LoadEngine(LoadRunOptions options)
        {
            _options = options;
        }

        public async Task<LoadRunOutcome> RunAsync(CancellationToken cancel)
        {
            var start = Stopwatch.GetTimestamp();
            _collector = new MetricsCollector(start, _options.WarmUpSeconds, _options.Step);
            _outcome.Collector = _collector;
            _gate = new RateGate(_options.RatePerSecond);

            if (_options.Endpoint.UseTls)
            {
                try
                {
                    _tls = DicomClientBuilder.CreateTlsInitiator(_options.Endpoint);
                }
                catch (InvalidOperationException ex)
                {
                    _outcome.Fatal = LoadFatal.ToolError;
                    _outcome.FatalMessage = ex.Message;
                    _outcome.StoppedBy = "tool-error";
                    _outcome.Summary = _collector.Summarize();
                    return _outcome;
                }
            }

            using (cancel.Register(OnCancel))
            using (var tickerStop = new CancellationTokenSource())
            {
                if (_options.DurationSeconds > 0) _stop.CancelAfter(TimeSpan.FromSeconds(_options.DurationSeconds));
                using (_stop.Token.Register(() => { if (_stoppedBy == null) _stoppedBy = "duration"; }))
                {
                    var ticker = Task.Run(() => TickAsync(start, tickerStop.Token));
                    var workers = Enumerable.Range(0, _options.Associations).Select(i => Task.Run(() => WorkerAsync(i))).ToList();
                    await Task.WhenAll(workers).ConfigureAwait(false);
                    tickerStop.Cancel();
                    try { await ticker.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }

            var end = Stopwatch.GetTimestamp();
            foreach (var row in _collector.CloseAll(end, 0, _options.SecondOffset)) _options.OnSecond?.Invoke(row);
            // A last window much shorter than the others would give a throughput from a few samples, so it is left out. The series has it.
            if (_options.SnapshotSeconds > 0 && end - _lastWindowTicks >= (long)(_options.SnapshotSeconds / 2 * Stopwatch.Frequency)) CloseWindow(end);

            _outcome.Cancelled = cancel.IsCancellationRequested;
            _outcome.StoppedBy = _outcome.Cancelled ? "cancelled" : _outcome.Fatal != LoadFatal.None ? _outcome.StoppedBy : _stoppedBy ?? "workers-stopped";
            _outcome.Summary = _collector.Summarize();
            _outcome.Seconds = _collector.SecondOf(end) + 1;
            _stop.Dispose();
            _hard.Dispose();
            return _outcome;
        }

        private void OnCancel()
        {
            _stoppedBy = "cancelled";
            try
            {
                _stop.Cancel();
                _hard.CancelAfter(CancelGraceMs);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void StopAll(string reason)
        {
            if (_stoppedBy == null) _stoppedBy = reason;
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void Fatal(LoadFatal kind, string message)
        {
            lock (_sync)
            {
                if (_outcome.Fatal != LoadFatal.None) return;
                _outcome.Fatal = kind;
                _outcome.FatalMessage = message;
                _outcome.StoppedBy = kind == LoadFatal.NotSupported ? "not-supported" : kind == LoadFatal.Timeout ? "timeout" : "connect-failed";
            }
            Logger.Warning("Load run stopped: {reason}", message);
            StopAll(_outcome.StoppedBy);
        }

        private bool TryClaim()
        {
            if (_stop.IsCancellationRequested) return false;
            if (Interlocked.Increment(ref _claimed) <= _options.Budget) return true;
            StopAll("count");
            return false;
        }

        private void Unclaim() => Interlocked.Decrement(ref _claimed);

        // ---------------------------------------------------------------- ticking

        private async Task TickAsync(long start, CancellationToken token)
        {
            var process = Process.GetCurrentProcess();
            var lastCpu = process.TotalProcessorTime;
            var lastWall = Stopwatch.GetTimestamp();
            _lastWindowTicks = start;
            for (var second = 1; ; second++)
            {
                var due = start + second * Stopwatch.Frequency + Stopwatch.Frequency / 20;
                var wait = due - Stopwatch.GetTimestamp();
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), token).ConfigureAwait(false);

                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var wall = Stopwatch.GetTimestamp();
                var cpuPercent = (cpu - lastCpu).TotalSeconds / ((double)(wall - lastWall) / Stopwatch.Frequency) / Environment.ProcessorCount * 100;
                lastCpu = cpu;
                lastWall = wall;

                foreach (var row in _collector.CloseSecondsBefore(second, cpuPercent, _options.SecondOffset)) _options.OnSecond?.Invoke(row);

                if (_options.SnapshotSeconds > 0 && wall - _lastWindowTicks >= (long)(_options.SnapshotSeconds * Stopwatch.Frequency) - Stopwatch.Frequency / 10)
                {
                    _lastWindowTicks = wall;
                    CloseWindow(wall);
                }
            }
        }

        private void CloseWindow(long now)
        {
            SoakWindow window;
            lock (_sync)
            {
                var summary = _collector.CloseWindow(now);
                var previous = _outcome.Windows.LastOrDefault();
                var startSecond = previous == null ? 0 : previous.EndSecond;
                var endSecond = _collector.SecondOf(now);
                if (previous != null && summary.Attempts == 0 && endSecond <= startSecond) return;
                window = new SoakWindow { Window = _outcome.Windows.Count + 1, StartSecond = startSecond, EndSecond = Math.Max(endSecond, startSecond + 1), Summary = summary };
                _outcome.Windows.Add(window);
            }
            _options.OnWindow?.Invoke(window);
        }

        // ---------------------------------------------------------------- one association at a time

        /// <summary>
        /// One instance is claimed before an association is opened, so that a worker never opens an association it then has
        /// nothing to send on. The claim is given back if it is not used.
        /// </summary>
        private async Task WorkerAsync(int index)
        {
            var held = false;
            try
            {
                await WorkerLoopAsync(index, () => held, value => held = value).ConfigureAwait(false);
            }
            finally
            {
                if (held) Unclaim();
            }
        }

        private async Task WorkerLoopAsync(int index, Func<bool> isHeld, Action<bool> setHeld)
        {
            var stamper = new UidStamper(_options.UidRoot, _options.RunNonce, _options.WorkerOffset + index);
            var pool = _options.Pool.Instances;
            var cursor = index % pool.Count;
            var consecutiveFailures = 0;
            var responseTimeout = TimeSpan.FromSeconds(_options.Network.ResponseTimeoutSeconds > 0 ? _options.Network.ResponseTimeoutSeconds : 60);
            var associationTimeout = TimeSpan.FromSeconds(_options.Network.AssociationTimeoutSeconds > 0 ? _options.Network.AssociationTimeoutSeconds : 30);

            while (!_stop.IsCancellationRequested)
            {
                if (!isHeld())
                {
                    if (!TryClaim()) return;
                    setHeld(true);
                }

                var connectStart = Stopwatch.GetTimestamp();
                RawAssociation raw;
                try
                {
                    raw = await RawAssociation.ConnectAsync(_options.Endpoint, _options.Network, _tls, _hard.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is TimeoutException || IsNetworkFailure(ex))
                {
                    if (_hard.IsCancellationRequested) return;
                    _collector.RecordFailure(LoadFailure.ConnectFailed, Stopwatch.GetTimestamp());
                    if (!await OpenFailedAsync(++consecutiveFailures, ex is TimeoutException ? LoadFatal.Timeout : LoadFatal.ConnectFailed, NetworkErrors.GetUserMessage(ex)).ConfigureAwait(false)) return;
                    continue;
                }

                var opened = false;
                var broken = false;
                try
                {
                    using (raw.CloseOn(_hard.Token))
                    {
                        var connected = Stopwatch.GetTimestamp();
                        var answer = await raw.AssociateAsync(Contexts(), _options.MaxPdu, associationTimeout, _hard.Token).ConfigureAwait(false);
                        if (!answer.Accepted)
                        {
                            var rejected = answer.Rejection != null;
                            _collector.RecordFailure(rejected ? LoadFailure.AssociationRejected : LoadFailure.ConnectFailed, Stopwatch.GetTimestamp());
                            var message = rejected ? "The association was rejected: " + answer.Rejection
                                : answer.TimedOut ? "The A-ASSOCIATE-RQ got no answer." : "The system " + answer.Ending.Describe() + " instead of answering the A-ASSOCIATE-RQ.";
                            raw.Dispose();
                            if (!await OpenFailedAsync(++consecutiveFailures, answer.TimedOut ? LoadFatal.Timeout : LoadFatal.ConnectFailed, message).ConfigureAwait(false)) return;
                            continue;
                        }

                        var accepted = Stopwatch.GetTimestamp();
                        _collector.AssociationOpened(connected - connectStart, accepted - connectStart);
                        opened = true;
                        consecutiveFailures = 0;
                        Interlocked.Exchange(ref _everAccepted, 1);

                        var contextIds = new Dictionary<string, byte>(StringComparer.Ordinal);
                        foreach (var context in raw.Contexts.Where(c => c.Accepted && c.AcceptedTransferSyntaxUid == _options.Pool.Syntax.UID.UID)) contextIds[context.AbstractSyntaxUid] = context.Id;
                        lock (_sync)
                        {
                            if (_outcome.Contexts.Count == 0) _outcome.Contexts.AddRange(raw.Negotiation());
                        }

                        var missing = _options.Pool.SopClasses.FirstOrDefault(s => !contextIds.ContainsKey(s));
                        if (missing != null)
                        {
                            Fatal(LoadFatal.NotSupported, $"The system did not accept {DicomUID.Parse(missing).Name} in {_options.Pool.Syntax.UID.Name}, which the mix {_options.Pool.Mix.Id} sends.");
                            await raw.ReleaseAsync(TimeSpan.FromMilliseconds(ReleaseTimeoutMs), CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        var pduLength = raw.PeerMaxPdu == 0 ? _options.MaxPdu : Math.Min(_options.MaxPdu, raw.PeerMaxPdu);
                        var sent = 0;
                        while (!_stop.IsCancellationRequested && (_options.InstancesPerAssociation == 0 || sent < _options.InstancesPerAssociation))
                        {
                            if (!isHeld())
                            {
                                if (!TryClaim()) break;
                                setHeld(true);
                            }
                            try
                            {
                                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            setHeld(false);

                            var item = pool[cursor];
                            cursor = (cursor + 1) % pool.Count;
                            var stamped = stamper.Stamp(item);

                            var firstByte = Stopwatch.GetTimestamp();
                            long lastByte;
                            try
                            {
                                await raw.SendStoreCommandAsync(contextIds[stamped.SopClassUid], stamped.SopClassUid, stamped.SopInstanceUid).ConfigureAwait(false);
                                await raw.SendDatasetAsync(contextIds[stamped.SopClassUid], stamped.Parts, pduLength).ConfigureAwait(false);
                                lastByte = Stopwatch.GetTimestamp();
                            }
                            catch (Exception ex) when (IsNetworkFailure(ex))
                            {
                                if (_hard.IsCancellationRequested) return;
                                _collector.RecordFailure(LoadFailure.AssociationLost, Stopwatch.GetTimestamp());
                                LogFailure("The association broke while sending: " + NetworkErrors.GetUserMessage(ex));
                                broken = true;
                                break;
                            }

                            var reply = await raw.ReadResponseAsync(responseTimeout, _hard.Token).ConfigureAwait(false);
                            if (reply.TimedOut)
                            {
                                _collector.RecordFailure(LoadFailure.NoResponse, Stopwatch.GetTimestamp());
                                LogFailure($"No C-STORE response within {responseTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.");
                                broken = true;
                                break;
                            }
                            if (reply.Ending != null || !reply.Status.HasValue)
                            {
                                _collector.RecordFailure(LoadFailure.AssociationLost, reply.Ending?.AtTicks ?? Stopwatch.GetTimestamp());
                                LogFailure("The system " + (reply.Ending?.Describe() ?? "sent a response without a status") + " while a C-STORE was outstanding.");
                                broken = true;
                                break;
                            }

                            var status = reply.Status.Value;
                            _collector.RecordResponse(firstByte, lastByte, reply.ArrivedTicks, status, stamped.Bytes);
                            lock (_sync) _outcome.Statuses.Add(status);
                            var state = StoreStatus.Classify(status);
                            if (state == StoreState.Success || state == StoreState.Warning) _options.OnStored?.Invoke(stamped, reply.ArrivedTicks);
                            else LogFailure($"C-STORE status 0x{status.ToString("X4", CultureInfo.InvariantCulture)} ({StoreStatus.Describe(status)}), " +
                                (string.IsNullOrWhiteSpace(reply.ErrorComment) ? "no Error Comment." : $"Error Comment \"{reply.ErrorComment.Trim()}\"."));
                            sent++;
                        }

                        if (broken)
                        {
                            if (!raw.EndedByPeer) await raw.AbortAsync("The C-STORE did not complete").ConfigureAwait(false);
                        }
                        else if (!raw.EndedByPeer && !raw.IsClosed)
                        {
                            await raw.ReleaseAsync(TimeSpan.FromMilliseconds(ReleaseTimeoutMs), CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (_hard.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsNetworkFailure(ex))
                {
                    if (_hard.IsCancellationRequested) return;
                    LogFailure("The association failed: " + NetworkErrors.GetUserMessage(ex));
                }
                finally
                {
                    raw.Dispose();
                    if (opened) _collector.AssociationClosed();
                }
            }
        }

        /// <summary>Waits before trying again. False when the worker should give up: the system was never reached, or kept refusing.</summary>
        private async Task<bool> OpenFailedAsync(int consecutive, LoadFatal kind, string message)
        {
            LogFailure(message);
            if (Volatile.Read(ref _everAccepted) == 0 && Interlocked.Increment(ref _openFailures) >= _options.Associations)
            {
                Fatal(kind, message);
                return false;
            }
            if (consecutive >= MaxConsecutiveOpenFailures) return false;
            try
            {
                await Task.Delay(ReconnectDelayMs, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            return true;
        }

        private List<RawContext> Contexts()
        {
            var contexts = new List<RawContext>();
            byte id = 1;
            foreach (var sopClass in _options.Pool.SopClasses)
            {
                contexts.Add(new RawContext { Id = id, AbstractSyntaxUid = sopClass, TransferSyntaxUids = { _options.Pool.Syntax.UID.UID } });
                id += 2;
            }
            return contexts;
        }

        private void LogFailure(string message)
        {
            // The first problem is kept with when it happened, so that a result can be read without the log.
            var second = _collector?.SecondOf(Stopwatch.GetTimestamp()) ?? 0;
            Interlocked.CompareExchange(ref _outcome.FirstProblem, $"{message} In second {second.ToString(CultureInfo.InvariantCulture)} of the run.", null);
            var count = Interlocked.Increment(ref _loggedFailures);
            if (count <= LoggedFailures) Logger.Warning("Load: {message}", message);
            if (count == LoggedFailures) Logger.Warning("Load: further problems are counted in the result and not logged");
        }

        private static bool IsNetworkFailure(Exception ex) =>
            ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException
            || ex is System.Security.Authentication.AuthenticationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException;
    }

    /// <summary>
    /// The thread pool and GC settings of a load run, applied for its duration and restored after. A TLS handshake blocks a
    /// pool thread, so with many associations the pool would otherwise wait for new threads and inflate the setup times.
    /// SustainedLowLatency keeps blocking full collections out of the measurement.
    /// </summary>
    internal sealed class ClientTuning : IDisposable
    {
        private readonly GCLatencyMode _mode;
        private readonly int _worker;
        private readonly int _completion;
        private readonly bool _raised;
        private readonly int[] _gcAtStart;
        private readonly List<double> _cpu = new List<double>();

        public ClientTuning(int associations)
        {
            _mode = GCSettings.LatencyMode;
            ThreadPool.GetMinThreads(out _worker, out _completion);
            var needed = 2 * associations + 8;
            if (_worker < needed || _completion < needed)
            {
                _raised = ThreadPool.SetMinThreads(Math.Max(_worker, needed), Math.Max(_completion, needed));
            }
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch (ArgumentOutOfRangeException) { }
            _gcAtStart = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        }

        public void AddCpu(double percent)
        {
            lock (_cpu) _cpu.Add(percent);
        }

        public ClientHealth Health()
        {
            ThreadPool.GetMinThreads(out var worker, out var completion);
            var health = new ClientHealth
            {
                LogicalProcessors = Environment.ProcessorCount,
                ServerGc = GCSettings.IsServerGC,
                GcLatencyMode = GCSettings.LatencyMode.ToString(),
                MinWorkerThreads = worker,
                MinCompletionThreads = completion,
                Gen0Collections = GC.CollectionCount(0) - _gcAtStart[0],
                Gen1Collections = GC.CollectionCount(1) - _gcAtStart[1],
                Gen2Collections = GC.CollectionCount(2) - _gcAtStart[2],
            };
            lock (_cpu)
            {
                if (_cpu.Count > 0)
                {
                    health.MeanCpuPercent = Math.Round(_cpu.Average(), 1);
                    health.MaxCpuPercent = Math.Round(_cpu.Max(), 1);
                }
            }
            return health;
        }

        public void Dispose()
        {
            try { GCSettings.LatencyMode = _mode; } catch (ArgumentOutOfRangeException) { }
            if (_raised) ThreadPool.SetMinThreads(_worker, _completion);
        }
    }
}
