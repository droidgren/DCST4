using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    /// <summary>How the association around one DIMSE request went.</summary>
    public sealed class DimseExchange
    {
        /// <summary>None if the request went out and the association ended normally.</summary>
        public ConnectivityFailure Failure { get; internal set; }
        /// <summary>Ready to show to the user. Null if there was no failure.</summary>
        public string Message { get; internal set; }
        public bool Cancelled { get; internal set; }
        public bool AssociationAccepted { get; internal set; }
        /// <summary>Whether the SOP class of the request was accepted, so that it could go out at all.</summary>
        public bool ContextAccepted { get; internal set; }
        public List<NegotiatedContext> Contexts { get; internal set; } = new List<NegotiatedContext>();
    }

    /// <summary>Opens one association for one DIMSE request, the same way everywhere: timeouts, cancel and how it ended.</summary>
    internal static class DimseRunner
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(DimseRunner));

        /// <param name="sopClass">What the request needs a context for.</param>
        /// <param name="configure">
        /// Called before the association opens, to set up the request's callbacks and what is proposed. The callbacks must
        /// restart the watch for each response. They are also called with fo-dicom's own answer when no context was accepted,
        /// which <see cref="DimseExchange.ContextAccepted"/> tells apart from an answer from the SCP.
        /// </param>
        public static Task<DimseExchange> RunAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            DicomRequest request,
            DicomUID sopClass,
            AssociationTimeline timeline,
            Action<IDicomClient, TimeoutWatch, DimseExchange> configure,
            CancellationToken token) =>
            RunAsync(endpoint, options, new[] { request }, sopClass, timeline, configure, token);

        /// <summary>Several requests of one SOP class on one association, sent one after the other.</summary>
        public static async Task<DimseExchange> RunAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            IReadOnlyList<DicomRequest> requests,
            DicomUID sopClass,
            AssociationTimeline timeline,
            Action<IDicomClient, TimeoutWatch, DimseExchange> configure,
            CancellationToken token)
        {
            var exchange = new DimseExchange();
            IDicomClient client;
            try
            {
                client = DicomClientBuilder.Create(endpoint, options);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not set up the client");
                exchange.Failure = ConnectivityFailure.LocalConfiguration;
                exchange.Message = NetworkErrors.GetUserMessage(ex);
                return exchange;
            }

            TimeoutWatch watch = null;
            Exception failure = null;
            string rejection = null;
            try
            {
                watch = new TimeoutWatch(client, token, options);
                timeline?.Attach(client);

                client.AssociationAccepted += (s, e) =>
                {
                    exchange.AssociationAccepted = true;
                    exchange.ContextAccepted = e.Association.PresentationContexts.Any(c => c.AbstractSyntax == sopClass && c.Result == DicomPresentationContextResult.Accept);
                    exchange.Contexts = AssociationNegotiation.Read(e.Association);
                    AssociationNegotiation.Log(Logger, e.Association);
                };
                client.AssociationRejected += (s, e) => rejection = $"Association rejected: {e.Result}, source {e.Source}, reason {e.Reason}";

                configure(client, watch, exchange);

                foreach (var request in requests) await client.AddRequestAsync(request).ConfigureAwait(false);
                await client.SendAsync(watch.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation).ConfigureAwait(false);

                Conclude(exchange, watch, token, rejection, null);
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested || watch?.TimedOut == true || watch?.AbortReason != null)
            {
                failure = ex;
                Conclude(exchange, watch, token, rejection, null);
            }
            catch (Exception ex)
            {
                failure = ex;
                Logger.Error(ex, "The association failed");
                Conclude(exchange, watch, token, rejection, ex);
            }
            finally
            {
                var timedOut = watch?.TimedOut == true;
                timeline?.Complete(failure, token.IsCancellationRequested || timedOut || watch?.AbortReason != null, timedOut ? watch.Reason : watch?.AbortReason ?? "Cancelled");
                watch?.Dispose();
            }

            return exchange;
        }

        private static void Conclude(DimseExchange exchange, TimeoutWatch watch, CancellationToken token, string rejection, Exception exception)
        {
            if (token.IsCancellationRequested)
            {
                exchange.Cancelled = true;
            }
            else if (watch?.AbortReason != null)
            {
                exchange.Failure = ConnectivityFailure.Connection;
                exchange.Message = watch.AbortReason;
            }
            else if (watch?.TimedOut == true)
            {
                exchange.Failure = ConnectivityFailure.Timeout;
                exchange.Message = watch.Reason + " (timed out)";
            }
            else if (rejection != null)
            {
                exchange.Failure = ConnectivityFailure.AssociationRejected;
                exchange.Message = rejection;
            }
            else if (exception != null)
            {
                exchange.Failure = EchoService.Classify(exception);
                exchange.Message = NetworkErrors.GetUserMessage(exception);
            }
            else if (exchange.AssociationAccepted && !exchange.ContextAccepted)
            {
                exchange.Failure = ConnectivityFailure.ContextNotAccepted;
                exchange.Message = "The SCP accepted the association but not the SOP class of the request, so nothing was sent.";
            }
        }
    }
}
