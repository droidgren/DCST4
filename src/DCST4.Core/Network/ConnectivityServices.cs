using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    public enum ConnectivityOutcome { Success, Failed, Cancelled }

    /// <summary>Why a C-ECHO did not succeed. Tells apart what <see cref="ConnectivityOutcome.Failed"/> holds together.</summary>
    public enum ConnectivityFailure
    {
        None,
        /// <summary>No TCP connection, association or response within its timeout.</summary>
        Timeout,
        AssociationRejected,
        /// <summary>The association was accepted, but not the Verification SOP Class. Nothing was sent.</summary>
        ContextNotAccepted,
        /// <summary>The SCP answered with a status other than Success.</summary>
        DimseStatus,
        /// <summary>The SCP sent A-ABORT.</summary>
        Aborted,
        Connection,
        /// <summary>Stopped on our side before connecting, e.g. the client certificate is not installed.</summary>
        LocalConfiguration,
    }

    /// <summary>Result of a Ping or C-ECHO. <see cref="Message"/> is ready to show to the user.</summary>
    public sealed class ConnectivityResult
    {
        public ConnectivityResult(ConnectivityOutcome outcome, string message, long elapsedMs = 0, ushort? statusCode = null, ConnectivityFailure failure = ConnectivityFailure.None)
        {
            Outcome = outcome;
            Message = message;
            ElapsedMs = elapsedMs;
            StatusCode = statusCode;
            Failure = failure;
        }

        public ConnectivityOutcome Outcome { get; }
        public string Message { get; }
        public long ElapsedMs { get; }
        /// <summary>Raw (0000,0900) of the response, or null if the SCP sent none.</summary>
        public ushort? StatusCode { get; }
        public ConnectivityFailure Failure { get; }
    }

    /// <summary>ICMP ping. Carried over from DMWLT4, with results now logged.</summary>
    public static class PingService
    {
        private const int TimeoutMs = 4000;
        private static readonly ILogger Logger = Log.ForContext(typeof(PingService));

        public static async Task<ConnectivityResult> PingAsync(string host)
        {
            Logger.Information("Ping -> {host}", host);
            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(host, TimeoutMs).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                    {
                        Logger.Information("Ping {host} success in {ms} ms", host, reply.RoundtripTime);
                        return new ConnectivityResult(ConnectivityOutcome.Success, $"Ping successful ({reply.RoundtripTime} ms).", reply.RoundtripTime);
                    }

                    Logger.Warning("Ping {host} failed: {status}", host, reply.Status);
                    return new ConnectivityResult(ConnectivityOutcome.Failed, $"Ping failed: {reply.Status}.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Ping error");
                return new ConnectivityResult(ConnectivityOutcome.Failed, "Ping error: " + NetworkErrors.GetUserMessage(ex));
            }
        }
    }

    /// <summary>C-ECHO. Carried over from DMWLT4.</summary>
    public static class EchoService
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(EchoService));

        /// <param name="timeline">Receives the protocol steps of this association, if given.</param>
        /// <param name="configure">
        /// Called with the client and the request before the association opens, so that a negotiation test can decide what is
        /// proposed. fo-dicom adds the request's own context first, then the client's additional contexts.
        /// </param>
        /// <param name="wrapTls">Given the TLS initiator of the endpoint, returns the one to use, so that a test can record the handshake.</param>
        public static async Task<ConnectivityResult> EchoAsync(DicomEndpoint endpoint, NetworkOptions options, CancellationToken token, AssociationTimeline timeline = null, Action<IDicomClient, DicomCEchoRequest> configure = null,
            Func<FellowOakDicom.Network.Tls.ITlsInitiator, FellowOakDicom.Network.Tls.ITlsInitiator> wrapTls = null)
        {
            Logger.Information("C-ECHO -> {calledAe} at {host}:{port} from {callingAe}", endpoint.CalledAe, endpoint.Host, endpoint.Port, endpoint.CallingAe);

            IDicomClient client;
            try
            {
                client = DicomClientBuilder.Create(endpoint, options, wrapTls);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "C-ECHO failed");
                return new ConnectivityResult(ConnectivityOutcome.Failed, "Echo failed: " + NetworkErrors.GetUserMessage(ex), failure: ConnectivityFailure.LocalConfiguration);
            }

            TimeoutWatch watch = null;
            Exception sendFailure = null;
            try
            {
                watch = new TimeoutWatch(client, token, options);
                timeline?.Attach(client);

                var request = new DicomCEchoRequest();
                configure?.Invoke(client, request);
                DicomStatus echoStatus = null;
                ushort? rawStatus = null;
                var associationAccepted = false;
                var contextAccepted = false;
                var dimseIndex = 0;
                var responseWatch = watch;

                client.AssociationAccepted += (s, e) =>
                {
                    associationAccepted = true;
                    contextAccepted = e.Association.PresentationContexts.Any(c =>
                        c.AbstractSyntax == DicomUID.Verification && c.Result == DicomPresentationContextResult.Accept);
                    if (contextAccepted && timeline != null) dimseIndex = timeline.RecordRequest("C-ECHO", "Verification SOP Class");
                };

                request.OnResponseReceived += (req, res) =>
                {
                    echoStatus = res.Status;
                    // Without an accepted context fo-dicom answers the request itself, and that is not a status from the SCP.
                    if (contextAccepted)
                    {
                        rawStatus = res.Command.GetSingleValueOrDefault(DicomTag.Status, res.Status.Code);
                        timeline?.RecordResponse(dimseIndex, "C-ECHO", rawStatus.Value, StoreStatus.Describe(rawStatus.Value));
                    }
                    responseWatch.Stop();
                };

                var sw = Stopwatch.StartNew();
                await client.AddRequestAsync(request).ConfigureAwait(false);
                await client.SendAsync(watch.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation).ConfigureAwait(false);
                sw.Stop();

                // SendAsync completing without an exception does not guarantee that a successful
                // C-ECHO response was received, so check the actual response status.
                if (token.IsCancellationRequested) return Cancelled();
                var notAccepted = associationAccepted && !contextAccepted;
                if (echoStatus == null)
                {
                    var failure = notAccepted ? ConnectivityFailure.ContextNotAccepted : watch.TimedOut ? ConnectivityFailure.Timeout : ConnectivityFailure.Connection;
                    return NoResponse(watch.TimedOut ? watch.Reason + " (timed out)" : "no response received", failure);
                }
                if (echoStatus != DicomStatus.Success)
                {
                    Logger.Warning("C-ECHO returned status {status}", echoStatus);
                    return new ConnectivityResult(ConnectivityOutcome.Failed, $"Echo failed: {echoStatus}.", statusCode: rawStatus,
                        failure: notAccepted ? ConnectivityFailure.ContextNotAccepted : ConnectivityFailure.DimseStatus);
                }

                Logger.Information("C-ECHO success in {ms} ms", sw.ElapsedMilliseconds);
                return new ConnectivityResult(ConnectivityOutcome.Success, $"Echo successful ({sw.ElapsedMilliseconds} ms).", sw.ElapsedMilliseconds, rawStatus);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Cancelled();
            }
            catch (OperationCanceledException ex) when (watch?.TimedOut == true)
            {
                sendFailure = ex;
                return NoResponse(watch.Reason + " (timed out)", ConnectivityFailure.Timeout);
            }
            catch (Exception ex)
            {
                sendFailure = ex;
                Logger.Error(ex, "C-ECHO failed");
                return new ConnectivityResult(ConnectivityOutcome.Failed, "Echo failed: " + NetworkErrors.GetUserMessage(ex), failure: Classify(ex));
            }
            finally
            {
                var timedOut = watch?.TimedOut == true;
                timeline?.Complete(sendFailure, token.IsCancellationRequested || timedOut, timedOut ? watch.Reason : "Cancelled");
                watch?.Dispose();
            }
        }

        internal static ConnectivityFailure Classify(Exception ex)
        {
            if (ex is AggregateException aggregate)
            {
                var causes = aggregate.Flatten().InnerExceptions.Select(Classify).ToList();
                return causes.Contains(ConnectivityFailure.Timeout) ? ConnectivityFailure.Timeout : causes.FirstOrDefault();
            }

            if (ex is TimeoutException || ex is DicomAssociationRequestTimedOutException) return ConnectivityFailure.Timeout;
            if (ex is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut) return ConnectivityFailure.Timeout;
            if (ex is DicomAssociationRejectedException) return ConnectivityFailure.AssociationRejected;
            if (ex is DicomAssociationAbortedException) return ConnectivityFailure.Aborted;
            return ex.InnerException != null ? Classify(ex.InnerException) : ConnectivityFailure.Connection;
        }

        private static ConnectivityResult Cancelled()
        {
            Logger.Information("C-ECHO cancelled");
            return new ConnectivityResult(ConnectivityOutcome.Cancelled, "Echo cancelled.");
        }

        private static ConnectivityResult NoResponse(string reason, ConnectivityFailure failure)
        {
            Logger.Warning("C-ECHO: {reason}", reason);
            return new ConnectivityResult(ConnectivityOutcome.Failed, $"Echo failed: {reason}.", failure: failure);
        }
    }
}
