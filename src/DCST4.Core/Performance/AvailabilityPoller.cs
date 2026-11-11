using DCST4.Core.Network;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Performance
{
    /// <summary>
    /// Time-to-available: how long after its C-STORE-RSP a stored instance can be found with C-FIND at IMAGE level. One in
    /// <c>sampleEvery</c> stored instances is polled, on an association of its own per round, at most
    /// <see cref="MaxQueriesPerRound"/> queries a round, so that polling adds little load. Its C-FINDs are not part of the
    /// load metrics. The resolution is the poll interval.
    /// </summary>
    internal sealed class AvailabilityPoller
    {
        public const int MaxQueriesPerRound = LoadSettings.MaxQueriesPerRound;

        private sealed class Sample
        {
            public StampedInstance Instance;
            public long StoredTicks;
            public long LastPolledTicks;
        }

        private readonly DicomEndpoint _endpoint;
        private readonly NetworkOptions _network;
        private readonly int _sampleEvery;
        private readonly double _intervalSeconds;
        private readonly double _timeoutSeconds;
        private readonly ConcurrentQueue<Sample> _incoming = new ConcurrentQueue<Sample>();
        private readonly List<Sample> _pending = new List<Sample>();
        private readonly LatencyHistogram _found = new LatencyHistogram();
        private readonly TimeToAvailable _result;
        private int _stored;

        public AvailabilityPoller(DicomEndpoint endpoint, NetworkOptions network, int sampleEvery, double intervalSeconds, double timeoutSeconds)
        {
            _endpoint = endpoint;
            _network = network;
            _sampleEvery = Math.Max(1, sampleEvery);
            _intervalSeconds = intervalSeconds;
            _timeoutSeconds = timeoutSeconds;
            _result = new TimeToAvailable { SampleEvery = _sampleEvery, IntervalSeconds = intervalSeconds, TimeoutSeconds = timeoutSeconds };
        }

        /// <summary>Called by the workers for every stored instance. Takes the first and then one in every <c>sampleEvery</c>.</summary>
        public void Offer(StampedInstance instance, long storedTicks)
        {
            var n = Interlocked.Increment(ref _stored);
            if ((n - 1) % _sampleEvery != 0) return;
            _incoming.Enqueue(new Sample { Instance = instance, StoredTicks = storedTicks });
        }

        /// <summary>Polls until <paramref name="loadDone"/> is set and every sample is found or timed out, or until cancelled.</summary>
        public async Task<TimeToAvailable> RunAsync(Task loadDone, CancellationToken cancel)
        {
            var interval = TimeSpan.FromSeconds(_intervalSeconds);
            var timeoutTicks = (long)(_timeoutSeconds * Stopwatch.Frequency);
            try
            {
                while (true)
                {
                    await Task.WhenAny(Task.Delay(interval, cancel)).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();

                    while (_incoming.TryDequeue(out var sample))
                    {
                        _pending.Add(sample);
                        _result.Sampled++;
                    }

                    var now = Stopwatch.GetTimestamp();
                    foreach (var expired in _pending.Where(s => now - s.StoredTicks > timeoutTicks).ToList())
                    {
                        _pending.Remove(expired);
                        _result.NeverFound++;
                    }

                    if (_pending.Count == 0)
                    {
                        if (loadDone.IsCompleted && _incoming.IsEmpty) break;
                        continue;
                    }

                    var round = _pending.OrderBy(s => s.LastPolledTicks).ThenBy(s => s.StoredTicks).Take(MaxQueriesPerRound).ToList();
                    var identifiers = round.Select(s => new DicomDataset
                    {
                        { DicomTag.StudyInstanceUID, s.Instance.StudyInstanceUid },
                        { DicomTag.SeriesInstanceUID, s.Instance.SeriesInstanceUid },
                        { DicomTag.SOPInstanceUID, s.Instance.SopInstanceUid },
                    }).ToList();

                    _result.Rounds++;
                    _result.Queries += round.Count;
                    var outcome = await QueryService.FindManyAsync(_endpoint, _network, QueryRoot.Study, DicomQueryRetrieveLevel.Image, identifiers, cancel).ConfigureAwait(false);
                    if (outcome.Exchange.Cancelled) throw new OperationCanceledException(cancel);
                    if (outcome.Exchange.Failure == ConnectivityFailure.ContextNotAccepted)
                    {
                        _result.QuerySupported = false;
                        _result.Note = "The system did not accept Study Root Query/Retrieve FIND, so time-to-available could not be measured.";
                        _result.NotSettled = _pending.Count + _incoming.Count;
                        _pending.Clear();
                        break;
                    }
                    if (outcome.Exchange.Failure != ConnectivityFailure.None)
                    {
                        _result.FailedRounds++;
                        _result.Note = "Some poll rounds failed: " + outcome.Exchange.Message;
                    }
                    else
                    {
                        _result.QuerySupported = true;
                    }

                    var polledAt = Stopwatch.GetTimestamp();
                    for (var i = 0; i < round.Count; i++)
                    {
                        var sample = round[i];
                        sample.LastPolledTicks = polledAt;
                        var found = i < outcome.Queries.Count ? outcome.Queries[i].FoundTicks : 0;
                        if (found == 0) continue;
                        _found.Record(MetricsCollector.Micros(found - sample.StoredTicks));
                        _result.Found++;
                        _pending.Remove(sample);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                while (_incoming.TryDequeue(out _)) _result.Sampled++;
                _result.NotSettled += _pending.Count;
            }

            _result.Available = LatencySummary.From(_found);
            return _result;
        }
    }
}
