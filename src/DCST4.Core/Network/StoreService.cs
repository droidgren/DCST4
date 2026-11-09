using DCST4.Core.Pipeline;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    /// <summary>How a send ended, separate from the per-image results.</summary>
    public sealed class StoreSendOutcome
    {
        /// <summary>Why the send stopped early, or null if every image got a response.</summary>
        public string FatalError { get; set; }
        public bool Cancelled { get; set; }
        public int AssociationCount { get; set; }
        /// <summary>The transfer syntax actually used per SOP class, for the run summary.</summary>
        public string NegotiationSummary { get; set; }
        /// <summary>Set when images went out in another syntax than selected because fallback was allowed.</summary>
        public string FallbackWarning { get; set; }
    }

    /// <summary>
    /// Sends images with C-STORE over one association. Knows nothing about how the images were made.
    ///
    /// A limited number of images is queued at a time and the next is produced as responses arrive, so a long stack
    /// does not sit in memory. Association linger keeps the association open while the next image is being produced;
    /// it is dropped right before the last image so the release does not wait for the linger to run out.
    ///
    /// fo-dicom always proposes Implicit VR LE in a presentation context of its own next to the selected syntax, and
    /// silently transcodes to it if the selected one is not accepted. That would let a PACS pass a test of a syntax it
    /// cannot take, so unless fallback is allowed, the run stops with an error when the selected syntax is not accepted.
    /// </summary>
    public sealed class StoreService
    {
        private const int WindowSize = 4;
        private const int LingerWhileFeedingMs = 30000;
        private const int LingerAfterLastImageMs = 0;

        private static readonly ILogger Logger = Log.ForContext<StoreService>();

        private sealed class PendingImage
        {
            public SourceImage Image;
            public long QueuedAtMs;
        }

        public async Task<StoreSendOutcome> SendAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            IEnumerable<SourceImage> images,
            bool allowFallback,
            Action<StoreResult> onResult,
            CancellationToken token)
        {
            var outcome = new StoreSendOutcome();
            var pending = new ConcurrentDictionary<string, PendingImage>(StringComparer.Ordinal);
            var acceptedBySopClass = new ConcurrentDictionary<string, DicomTransferSyntax[]>(StringComparer.Ordinal);
            var used = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            var clock = Stopwatch.StartNew();
            long lastResponseAtMs = 0;
            var outstanding = 0;
            string rejection = null;

            TimeoutWatch watch = null;
            try
            {
                var client = DicomClientBuilder.Create(endpoint, options);
                client.ClientOptions.AssociationLingerTimeoutInMs = LingerWhileFeedingMs;
                watch = new TimeoutWatch(client, token, options);
                var responseWatch = watch;

                client.AssociationAccepted += (s, e) =>
                {
                    outcome.AssociationCount++;
                    RecordNegotiation(e.Association, acceptedBySopClass);

                    var problem = FindUnacceptedSyntax(pending.Values.Select(p => p.Image), acceptedBySopClass);
                    if (problem == null) return;
                    if (allowFallback)
                    {
                        outcome.FallbackWarning = problem + " Using the syntax the SCP accepted, as fallback is allowed.";
                        Logger.Warning("{warning}", outcome.FallbackWarning);
                    }
                    else
                    {
                        var error = problem + " Nothing was sent. Select another transfer syntax, or allow fallback to Implicit VR.";
                        Logger.Error("{error}", error);
                        responseWatch.Abort(error);
                    }
                };
                client.AssociationRejected += (s, e) =>
                {
                    rejection = $"Association rejected: {e.Result}, source {e.Source}, reason {e.Reason}";
                    Logger.Warning("{rejection}", rejection);
                };
                client.AssociationReleased += (s, e) => Logger.Information("Association released");

                using (var window = new SemaphoreSlim(WindowSize, WindowSize))
                {
                    Task sendTask = null;
                    Func<Task> startSend = () =>
                        client.SendAsync(responseWatch.Token, DicomClientCancellationMode.ImmediatelyAbortAssociation);

                    var queued = 0;
                    await Task.Run(async () =>
                    {
                        using (var enumerator = images.GetEnumerator())
                        {
                            var hasNext = enumerator.MoveNext();
                            while (hasNext)
                            {
                                if (sendTask != null)
                                {
                                    var slot = window.WaitAsync(responseWatch.Token);
                                    var first = await Task.WhenAny(slot, sendTask).ConfigureAwait(false);
                                    if (first == sendTask && sendTask.IsFaulted) break;
                                    await slot.ConfigureAwait(false);
                                }
                                else
                                {
                                    await window.WaitAsync(responseWatch.Token).ConfigureAwait(false);
                                }

                                var image = enumerator.Current;
                                hasNext = enumerator.MoveNext();
                                if (!hasNext) client.ClientOptions.AssociationLingerTimeoutInMs = LingerAfterLastImageMs;

                                var entry = new PendingImage { Image = image, QueuedAtMs = clock.ElapsedMilliseconds };
                                pending[image.SopInstanceUid] = entry;

                                var request = new DicomCStoreRequest(image.File, DicomPriority.Medium);
                                request.OnResponseReceived = (req, response) =>
                                {
                                    var now = clock.ElapsedMilliseconds;
                                    var previous = Interlocked.Exchange(ref lastResponseAtMs, now);
                                    pending.TryRemove(image.SopInstanceUid, out _);

                                    var accepted = SyntaxUsedFor(image, acceptedBySopClass);
                                    if (accepted != null) used[$"{image.File.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID).Name}: {accepted}"] = true;

                                    // Responses arrive one at a time, so the wait for this image started when it
                                    // was queued or when the previous response arrived, whichever came later.
                                    Report(BuildResult(image, response, accepted, now - Math.Max(entry.QueuedAtMs, previous)), onResult);

                                    if (Interlocked.Decrement(ref outstanding) == 0) responseWatch.Stop();
                                    else responseWatch.Restart();
                                    try { window.Release(); } catch (ObjectDisposedException) { }
                                };

                                if (Interlocked.Increment(ref outstanding) == 1 && sendTask != null) responseWatch.Restart();
                                await client.AddRequestAsync(request).ConfigureAwait(false);
                                queued++;

                                // Wait for the first window so the association negotiates every SOP class in it.
                                // If a very slow image let the association linger out, this reopens it.
                                if ((sendTask == null && (queued >= WindowSize || !hasNext)) || (sendTask != null && sendTask.IsCompleted && !sendTask.IsFaulted))
                                {
                                    sendTask = startSend();
                                }
                            }
                        }

                        if (sendTask == null && queued > 0) sendTask = startSend();
                        if (sendTask != null) await sendTask.ConfigureAwait(false);
                    }, responseWatch.Token).ConfigureAwait(false);
                }

                // SendAsync can complete normally after a cancel, so the end state is checked here as well.
                ApplyEndState(outcome, watch, token, rejection);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || watch?.TimedOut == true || watch?.AbortReason != null)
            {
                ApplyEndState(outcome, watch, token, rejection);
            }
            catch (Exception ex)
            {
                outcome.FatalError = rejection ?? watch?.AbortReason ?? NetworkErrors.GetUserMessage(ex);
                Logger.Error(ex, "C-STORE stopped: {reason}", outcome.FatalError);
            }
            finally
            {
                watch?.Dispose();
            }

            // Whatever was queued but got no response was not stored as far as we can tell.
            var noResponseReason = outcome.Cancelled ? "Cancelled before a response" : outcome.FatalError ?? "No response received";
            var unanswered = pending.Values.ToList();
            foreach (var entry in unanswered.OrderBy(e => e.Image.SeriesNumber).ThenBy(e => e.Image.InstanceNumber))
            {
                Report(new StoreResult
                {
                    SeriesNumber = entry.Image.SeriesNumber,
                    InstanceNumber = entry.Image.InstanceNumber,
                    SopClassUid = entry.Image.SopClassUid,
                    SopInstanceUid = entry.Image.SopInstanceUid,
                    State = StoreState.NotSent,
                    Description = noResponseReason,
                    AcceptedTransferSyntax = SyntaxUsedFor(entry.Image, acceptedBySopClass),
                    PixelBytes = entry.Image.PixelBytes,
                    SavedPath = entry.Image.SavedPath,
                    SaveError = entry.Image.SaveError,
                }, onResult);
            }
            if (outcome.FatalError == null && !outcome.Cancelled && unanswered.Count > 0)
            {
                outcome.FatalError = "Some images got no response.";
            }

            outcome.NegotiationSummary = string.Join("; ", used.Keys.OrderBy(k => k, StringComparer.Ordinal));
            return outcome;
        }

        private static void ApplyEndState(StoreSendOutcome outcome, TimeoutWatch watch, CancellationToken token, string rejection)
        {
            if (token.IsCancellationRequested)
            {
                Logger.Information("C-STORE cancelled by user");
                outcome.Cancelled = true;
            }
            else if (watch?.AbortReason != null)
            {
                outcome.FatalError = watch.AbortReason;
            }
            else if (watch?.TimedOut == true)
            {
                outcome.FatalError = watch.Reason + " (timed out)";
                Logger.Warning("C-STORE stopped: {reason}", outcome.FatalError);
            }
            else if (rejection != null)
            {
                outcome.FatalError = rejection;
            }
        }

        private static void RecordNegotiation(DicomAssociation association, ConcurrentDictionary<string, DicomTransferSyntax[]> acceptedBySopClass)
        {
            AssociationNegotiation.Log(Logger, association);

            foreach (var group in association.PresentationContexts.GroupBy(c => c.AbstractSyntax.UID))
            {
                acceptedBySopClass[group.Key] = group
                    .Where(c => c.Result == DicomPresentationContextResult.Accept && c.AcceptedTransferSyntax != null)
                    .Select(c => c.AcceptedTransferSyntax)
                    .ToArray();
            }
        }

        /// <summary>Describes the first queued image whose own syntax the SCP did not accept, or returns null if all are fine.</summary>
        private static string FindUnacceptedSyntax(IEnumerable<SourceImage> images, ConcurrentDictionary<string, DicomTransferSyntax[]> acceptedBySopClass)
        {
            foreach (var image in images)
            {
                var syntax = image.File.Dataset.InternalTransferSyntax;
                acceptedBySopClass.TryGetValue(image.SopClassUid, out var accepted);
                if (accepted != null && accepted.Contains(syntax)) continue;

                var sopClass = image.File.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID).Name;
                var alternatives = accepted == null || accepted.Length == 0 ? "nothing" : string.Join(", ", accepted.Select(t => t.UID.Name));
                return $"The SCP did not accept {syntax.UID.Name} for {sopClass}. It accepted: {alternatives}.";
            }
            return null;
        }

        /// <summary>fo-dicom uses the image's own syntax when it was accepted, and otherwise transcodes to the accepted one.</summary>
        private static string SyntaxUsedFor(SourceImage image, ConcurrentDictionary<string, DicomTransferSyntax[]> acceptedBySopClass)
        {
            if (!acceptedBySopClass.TryGetValue(image.SopClassUid, out var accepted) || accepted.Length == 0) return null;
            var syntax = image.File.Dataset.InternalTransferSyntax;
            return (accepted.Contains(syntax) ? syntax : accepted[0]).UID.Name;
        }

        private static StoreResult BuildResult(SourceImage image, DicomCStoreResponse response, string acceptedSyntax, long elapsedMs)
        {
            // Read the raw code from the command: fo-dicom's own lookup maps unknown vendor codes to a generic status.
            var code = response.Command.GetSingleValueOrDefault(DicomTag.Status, response.Status.Code);

            var result = new StoreResult
            {
                SeriesNumber = image.SeriesNumber,
                InstanceNumber = image.InstanceNumber,
                SopClassUid = image.SopClassUid,
                SopInstanceUid = image.SopInstanceUid,
                State = StoreStatus.Classify(code),
                StatusCode = code,
                Description = StoreStatus.Describe(code),
                ErrorComment = response.Command.GetSingleValueOrDefault(DicomTag.ErrorComment, string.Empty),
                OffendingElement = FormatOffendingElements(response.Command),
                AcceptedTransferSyntax = acceptedSyntax,
                ElapsedMs = elapsedMs,
                PixelBytes = image.PixelBytes,
                SavedPath = image.SavedPath,
                SaveError = image.SaveError,
            };

            // Without an accepted context fo-dicom answers the request itself: nothing was sent to the SCP.
            if (acceptedSyntax == null)
            {
                result.State = StoreState.NotSent;
                result.Description = "Not sent: the SCP accepted no presentation context for this SOP class";
            }

            return result;
        }

        private static string FormatOffendingElements(DicomDataset command)
        {
            if (!command.Contains(DicomTag.OffendingElement)) return string.Empty;
            try
            {
                return string.Join(" ", command.GetValues<DicomTag>(DicomTag.OffendingElement).Select(t => t.ToString()));
            }
            catch (DicomDataException)
            {
                return command.GetString(DicomTag.OffendingElement);
            }
        }

        private static void Report(StoreResult result, Action<StoreResult> onResult)
        {
            var level = result.State == StoreState.Success
                ? Serilog.Events.LogEventLevel.Information
                : result.State == StoreState.Warning ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Error;

            Logger.Write(level,
                "C-STORE series {series} instance {instance}: {state} status {code} ({description}) comment [{comment}] offending [{offending}] syntax [{syntax}] {ms} ms SOP {sop}",
                result.SeriesNumber, result.InstanceNumber, result.State.ToString(), result.StatusCodeText, result.Description,
                result.ErrorComment, result.OffendingElement, result.AcceptedTransferSyntax, result.ElapsedMs, result.SopInstanceUid);

            onResult?.Invoke(result);
        }
    }
}
