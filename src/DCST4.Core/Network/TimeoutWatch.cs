using FellowOakDicom.Network.Client;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    /// <summary>
    /// Response (DIMSE) timeout and association timeout handling. Carried over from DMWLT4.
    /// fo-dicom's own RequestTimeout is not used: when it fires, fo-dicom drops the request but still waits
    /// until the server has sent all its responses, and it ignores cancellation from then on. Instead, a timer
    /// is started when the association is accepted and restarted on every response; when it fires, the
    /// association is aborted the same way as with the Cancel button.
    /// </summary>
    internal sealed class TimeoutWatch : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<TimeoutWatch>();

        private readonly CancellationTokenSource _linked;
        private readonly Timer _timer;
        private readonly int _responseSeconds;
        private volatile string _reason;
        private volatile bool _disposed;

        public TimeoutWatch(IDicomClient client, CancellationToken userToken, NetworkOptions options)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            _responseSeconds = options.ResponseTimeoutSeconds;
            _timer = new Timer(_ => OnTimeout($"no response within {_responseSeconds} s"), null, Timeout.Infinite, Timeout.Infinite);

            client.AssociationAccepted += (s, e) => Restart();
            client.AssociationRequestTimedOut += (s, e) =>
                OnTimeout($"association not accepted within {options.AssociationTimeoutSeconds} s");
        }

        public CancellationToken Token => _linked.Token;
        public bool TimedOut => _reason != null;
        /// <summary>E.g. "no response within 30 s", or null if nothing timed out.</summary>
        public string Reason => _reason;
        /// <summary>Why <see cref="Abort"/> stopped the operation, or null if it did not.</summary>
        public string AbortReason { get; private set; }

        /// <summary>
        /// Aborts the association because the caller found a reason not to continue. Waits for the cancel to take effect,
        /// so that when this is called from AssociationAccepted, fo-dicom does not go on to send the first request.
        /// The wait is limited because the event is raised from inside fo-dicom's own processing.
        /// </summary>
        public void Abort(string reason)
        {
            AbortReason = reason;
            if (!CancelFromOtherThread().Wait(TimeSpan.FromSeconds(2)))
            {
                Logger.Warning("The association abort is taking longer than expected");
            }
        }

        /// <summary>Restarts the response timer. Call it for every response received, and when a new request goes out after a quiet period.</summary>
        public void Restart()
        {
            if (_responseSeconds <= 0 || _disposed || _reason != null) return;
            try { _timer.Change(TimeSpan.FromSeconds(_responseSeconds), Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Stops the response timer, e.g. when no response is outstanding.</summary>
        public void Stop()
        {
            try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void OnTimeout(string reason)
        {
            if (_reason != null || _disposed) return;
            _reason = reason;
            Logger.Warning("Timeout: {reason}, aborting association", reason);
            CancelFromOtherThread();
        }

        /// <summary>Cancel from another thread: the events are raised from inside fo-dicom's own processing.</summary>
        private Task CancelFromOtherThread()
        {
            return Task.Run(() =>
            {
                try { _linked.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
            _linked.Dispose();
        }
    }
}
