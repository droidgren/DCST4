using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Serilog;
using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    public enum ConnectivityOutcome { Success, Failed, Cancelled }

    /// <summary>Result of a Ping or C-ECHO. <see cref="Message"/> is ready to show to the user.</summary>
    public sealed class ConnectivityResult
    {
        public ConnectivityResult(ConnectivityOutcome outcome, string message, long elapsedMs = 0)
        {
            Outcome = outcome;
            Message = message;
            ElapsedMs = elapsedMs;
        }

        public ConnectivityOutcome Outcome { get; }
        public string Message { get; }
        public long ElapsedMs { get; }
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

        public static async Task<ConnectivityResult> EchoAsync(DicomEndpoint endpoint, NetworkOptions options, CancellationToken token)
        {
            Logger.Information("C-ECHO -> {calledAe} at {host}:{port} from {callingAe}", endpoint.CalledAe, endpoint.Host, endpoint.Port, endpoint.CallingAe);

            TimeoutWatch watch = null;
            try
            {
                var client = DicomClientBuilder.Create(endpoint, options);
                watch = new TimeoutWatch(client, token, options);

                var request = new DicomCEchoRequest();
                DicomStatus echoStatus = null;
                var responseWatch = watch;
                request.OnResponseReceived += (req, res) =>
                {
                    echoStatus = res.Status;
                    responseWatch.Stop();
                };

                var sw = Stopwatch.StartNew();
                await client.AddRequestAsync(request).ConfigureAwait(false);
                await client.SendAsync(watch.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation).ConfigureAwait(false);
                sw.Stop();

                // SendAsync completing without an exception does not guarantee that a successful
                // C-ECHO response was received, so check the actual response status.
                if (token.IsCancellationRequested) return Cancelled();
                if (echoStatus == null) return NoResponse(watch.TimedOut ? watch.Reason + " (timed out)" : "no response received");
                if (echoStatus != DicomStatus.Success)
                {
                    Logger.Warning("C-ECHO returned status {status}", echoStatus);
                    return new ConnectivityResult(ConnectivityOutcome.Failed, $"Echo failed: {echoStatus}.");
                }

                Logger.Information("C-ECHO success in {ms} ms", sw.ElapsedMilliseconds);
                return new ConnectivityResult(ConnectivityOutcome.Success, $"Echo successful ({sw.ElapsedMilliseconds} ms).", sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Cancelled();
            }
            catch (OperationCanceledException) when (watch?.TimedOut == true)
            {
                return NoResponse(watch.Reason + " (timed out)");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "C-ECHO failed");
                return new ConnectivityResult(ConnectivityOutcome.Failed, "Echo failed: " + NetworkErrors.GetUserMessage(ex));
            }
            finally
            {
                watch?.Dispose();
            }
        }

        private static ConnectivityResult Cancelled()
        {
            Logger.Information("C-ECHO cancelled");
            return new ConnectivityResult(ConnectivityOutcome.Cancelled, "Echo cancelled.");
        }

        private static ConnectivityResult NoResponse(string reason)
        {
            Logger.Warning("C-ECHO: {reason}", reason);
            return new ConnectivityResult(ConnectivityOutcome.Failed, $"Echo failed: {reason}.");
        }
    }
}
