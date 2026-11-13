using DCST4.Core.Network;
using DCST4.Core.Output;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Pipeline
{
    /// <summary>The codec families the transfer syntax lists are grouped by, in the order they are shown.</summary>
    public static class TransferSyntaxFamily
    {
        public const string Uncompressed = "Uncompressed";
        public const string Rle = "RLE";
        public const string Jpeg = "JPEG";
        public const string JpegLs = "JPEG-LS";
        public const string Jpeg2000 = "JPEG 2000";
        public const string Htj2k = "HTJ2K";
        public const string JpegXl = "JPEG XL";
        public const string Video = "Video";

        public static IReadOnlyList<string> All { get; } = new[] { Uncompressed, Rle, Jpeg, JpegLs, Jpeg2000, Htj2k, JpegXl, Video };
    }

    /// <summary>Transfer syntaxes a run can use, with the names shown to the user.</summary>
    public sealed class TransferSyntaxOption
    {
        private TransferSyntaxOption(string family, string name, DicomTransferSyntax syntax)
        {
            Family = family;
            Name = name;
            Syntax = syntax;
        }

        /// <summary>The group the list shows the option in, one of <see cref="TransferSyntaxFamily"/>.</summary>
        public string Family { get; }
        public string Name { get; }
        public DicomTransferSyntax Syntax { get; }
        public override string ToString() => Name;

        public static readonly TransferSyntaxOption ImplicitVrLe = new TransferSyntaxOption(TransferSyntaxFamily.Uncompressed, "Implicit VR Little Endian", DicomTransferSyntax.ImplicitVRLittleEndian);
        public static readonly TransferSyntaxOption ExplicitVrLe = new TransferSyntaxOption(TransferSyntaxFamily.Uncompressed, "Explicit VR Little Endian", DicomTransferSyntax.ExplicitVRLittleEndian);
        public static readonly TransferSyntaxOption DeflatedExplicitVrLe = new TransferSyntaxOption(TransferSyntaxFamily.Uncompressed, "Deflated Explicit VR Little Endian (lossless)", DicomTransferSyntax.DeflatedExplicitVRLittleEndian);
        public static readonly TransferSyntaxOption RleLossless = new TransferSyntaxOption(TransferSyntaxFamily.Rle, "RLE Lossless", DicomTransferSyntax.RLELossless);
        /// <summary>Lossy, and for 8 bits stored only. What ultrasound systems send their cine loops in.</summary>
        public static readonly TransferSyntaxOption JpegBaseline = new TransferSyntaxOption(TransferSyntaxFamily.Jpeg, "JPEG Baseline (Process 1, lossy)", DicomTransferSyntax.JPEGProcess1);
        /// <summary>Lossy, at most 12 bits stored.</summary>
        public static readonly TransferSyntaxOption JpegExtended = new TransferSyntaxOption(TransferSyntaxFamily.Jpeg, "JPEG Extended (Process 2 & 4, lossy)", DicomTransferSyntax.JPEGProcess2_4);
        public static readonly TransferSyntaxOption JpegLossless = new TransferSyntaxOption(TransferSyntaxFamily.Jpeg, "JPEG Lossless (Process 14 SV1)", DicomTransferSyntax.JPEGProcess14SV1);
        public static readonly TransferSyntaxOption JpegLsLossless = new TransferSyntaxOption(TransferSyntaxFamily.JpegLs, "JPEG-LS Lossless", DicomTransferSyntax.JPEGLSLossless);
        public static readonly TransferSyntaxOption Jpeg2000Lossless = new TransferSyntaxOption(TransferSyntaxFamily.Jpeg2000, "JPEG 2000 Lossless", DicomTransferSyntax.JPEG2000Lossless);
        public static readonly TransferSyntaxOption Jpeg2000 = new TransferSyntaxOption(TransferSyntaxFamily.Jpeg2000, "JPEG 2000 (lossy)", DicomTransferSyntax.JPEG2000Lossy);
        public static readonly TransferSyntaxOption Htj2kLossless = new TransferSyntaxOption(TransferSyntaxFamily.Htj2k, "HTJ2K Lossless", DicomTransferSyntax.HTJ2KLossless);
        public static readonly TransferSyntaxOption Htj2k = new TransferSyntaxOption(TransferSyntaxFamily.Htj2k, "HTJ2K (lossy)", DicomTransferSyntax.HTJ2K);
        public static readonly TransferSyntaxOption JpegXlLossless = new TransferSyntaxOption(TransferSyntaxFamily.JpegXl, "JPEG XL Lossless", DicomTransferSyntax.Lookup(DicomUID.JPEGXLLossless));
        /// <summary>The syntax allows lossy and lossless. DCST4's encoder writes a lossless codestream.</summary>
        public static readonly TransferSyntaxOption JpegXl = new TransferSyntaxOption(TransferSyntaxFamily.JpegXl, "JPEG XL (written lossless)", DicomTransferSyntax.Lookup(DicomUID.JPEGXL));
        /// <summary>Lossy colour video, for a multi-frame RGB object such as an ultrasound cine loop.</summary>
        public static readonly TransferSyntaxOption H264HighProfile = new TransferSyntaxOption(TransferSyntaxFamily.Video, "H.264 High Profile / Level 4.1 (lossy)", DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41);

        /// <summary>In <see cref="TransferSyntaxFamily"/> order, which is the order of the list.</summary>
        public static IReadOnlyList<TransferSyntaxOption> All { get; } = new[]
        {
            ImplicitVrLe, ExplicitVrLe, DeflatedExplicitVrLe,
            RleLossless,
            JpegBaseline, JpegExtended, JpegLossless,
            JpegLsLossless,
            Jpeg2000Lossless, Jpeg2000,
            Htj2kLossless, Htj2k,
            JpegXlLossless, JpegXl,
            H264HighProfile,
        };
    }

    public sealed class StoreRunOptions
    {
        /// <summary>Send with C-STORE. <see cref="Endpoint"/> is required when true.</summary>
        public bool Send { get; set; } = true;
        /// <summary>Write to <see cref="SaveFolder"/>.</summary>
        public bool Save { get; set; }
        public string SaveFolder { get; set; }
        public DicomTransferSyntax TransferSyntax { get; set; } = DicomTransferSyntax.ExplicitVRLittleEndian;
        /// <summary>
        /// False (default): stop with an error if the SCP does not accept the selected syntax.
        /// True: send as Implicit VR LE instead, the way a real modality would, and flag it in the summary.
        /// </summary>
        public bool AllowFallback { get; set; }
        public DicomEndpoint Endpoint { get; set; }
        public NetworkOptions Network { get; set; } = new NetworkOptions();
        /// <summary>Records the protocol steps of the send, if given.</summary>
        public AssociationTimeline Timeline { get; set; }

        /// <summary>Returns the first problem with these options, or null if they are valid.</summary>
        public string Validate()
        {
            if (!Send && !Save) return "Select Send, Save to disk, or both.";
            if (Save && string.IsNullOrWhiteSpace(SaveFolder)) return "Select a folder to save the images in.";
            return Send ? (Endpoint?.Validate() ?? (Endpoint == null ? "No destination is set." : null)) : null;
        }
    }

    /// <summary>What a whole run did. Holds every image's result, which later features such as Storage Commitment or load testing can build on.</summary>
    public sealed class RunSummary
    {
        public IReadOnlyList<StoreResult> Results { get; set; }
        public int Expected { get; set; }
        public int Success { get; set; }
        public int Warning { get; set; }
        public int Failure { get; set; }
        public int Cancelled { get; set; }
        /// <summary>Expected images that did not get a response, including those never generated.</summary>
        public int NotSent { get; set; }
        public int Saved { get; set; }
        public int SaveFailed { get; set; }
        public long PixelBytes { get; set; }
        public TimeSpan Elapsed { get; set; }
        public double ImagesPerSecond { get; set; }
        public bool WasCancelled { get; set; }
        public string FatalError { get; set; }
        /// <summary>What kind of thing <see cref="FatalError"/> was.</summary>
        public ConnectivityFailure FailureKind { get; set; }
        public int AssociationCount { get; set; }
        public string NegotiationSummary { get; set; }
        /// <summary>Set when images went out in another syntax than selected because fallback was allowed.</summary>
        public string FallbackWarning { get; set; }
        /// <summary>Where the DICOMDIR was written, if images were saved.</summary>
        public string DicomDirPath { get; set; }
        /// <summary>Why the DICOMDIR could not be written, if so. The images themselves may still be saved.</summary>
        public string DicomDirError { get; set; }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Success + Warning + Failure + Cancelled > 0 || NotSent > 0)
            {
                parts.Add($"{Success} ok");
                parts.Add($"{Warning} warning");
                parts.Add($"{Failure} failed");
                if (Cancelled > 0) parts.Add($"{Cancelled} cancelled");
                if (NotSent > 0) parts.Add($"{NotSent} not sent");
            }
            if (Saved > 0) parts.Add($"{Saved} saved");
            if (SaveFailed > 0) parts.Add($"{SaveFailed} not saved");
            parts.Add($"{PixelBytes / 1048576.0:0.0} MB pixel data");
            parts.Add($"{Elapsed.TotalSeconds:0.0} s");
            parts.Add($"{ImagesPerSecond:0.0} img/s");
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// One complete run: takes images from any <see cref="IImageSource"/>, puts them in the selected transfer syntax,
    /// optionally writes them to disk, and optionally sends them. Repeating it with a new source is how load testing can be added.
    /// </summary>
    public sealed class StoreRun
    {
        private static readonly ILogger Logger = Log.ForContext<StoreRun>();

        private readonly IImageSource _source;
        private readonly StoreRunOptions _options;
        private readonly ConcurrentQueue<StoreResult> _results = new ConcurrentQueue<StoreResult>();

        public StoreRun(IImageSource source, StoreRunOptions options)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>Raised on a background thread for each image as soon as its outcome is known.</summary>
        public event Action<StoreResult> ImageCompleted;

        /// <summary>
        /// Raised on a background thread while an image with many frames is encoded, before it is sent: the image's number
        /// in the run from 1, the frames done and the frames in all.
        /// </summary>
        public event Action<int, int, int> FrameEncoded;

        public Task<RunSummary> ExecuteAsync(CancellationToken token)
        {
            return Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                var send = new StoreSendOutcome();
                var writer = _options.Save ? new FileSetWriter(_options.SaveFolder) : null;
                try
                {
                    var prepared = Prepare(_source.GetImages(token), writer, token);
                    if (_options.Send)
                    {
                        var endpoint = _options.Endpoint.Trimmed();
                        Logger.Information("Run started: {count} images to {called} at {host}:{port} from {calling}, proposing {syntax}",
                            _source.ExpectedCount, endpoint.CalledAe, endpoint.Host, endpoint.Port, endpoint.CallingAe, _options.TransferSyntax.UID.Name);
                        send = await new StoreService().SendAsync(endpoint, _options.Network, prepared, _options.AllowFallback, Complete, token, _options.Timeline).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.Information("Run started: {count} images to disk only, {syntax}", _source.ExpectedCount, _options.TransferSyntax.UID.Name);
                        foreach (var image in prepared)
                        {
                            token.ThrowIfCancellationRequested();
                            Complete(new StoreResult
                            {
                                SeriesNumber = image.SeriesNumber,
                                InstanceNumber = image.InstanceNumber,
                                SopClassUid = image.SopClassUid,
                                SopInstanceUid = image.SopInstanceUid,
                                State = image.SaveError == null ? StoreState.Saved : StoreState.SaveFailed,
                                Description = image.SaveError == null ? "Saved" : "Not saved: " + image.SaveError,
                                PixelBytes = image.PixelBytes,
                                SavedPath = image.SavedPath,
                                SaveError = image.SaveError,
                            });
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    send.Cancelled = true;
                }
                catch (Exception ex)
                {
                    send.FatalError = NetworkErrors.GetUserMessage(ex);
                    send.FailureKind = ConnectivityFailure.LocalConfiguration;
                    Logger.Error(ex, "Run stopped: {reason}", send.FatalError);
                }

                clock.Stop();
                var summary = Summarize(send, clock.Elapsed);
                WriteDicomDir(writer, summary);
                Logger.Information("Run finished: {summary}. Stopped by: {fatal}. Cancelled: {cancelled}. Associations: {associations}. Negotiated: [{negotiated}]",
                    summary.ToString(), summary.FatalError ?? "nothing", summary.WasCancelled, summary.AssociationCount, summary.NegotiationSummary);
                return summary;
            });
        }

        /// <summary>Also after a failed or cancelled send: the files saved so far are worth finding.</summary>
        private static void WriteDicomDir(FileSetWriter writer, RunSummary summary)
        {
            if (writer == null || summary.Saved == 0) return;
            try
            {
                var images = writer.WriteDicomDir();
                summary.DicomDirPath = writer.DicomDirPath;
                Logger.Information("DICOMDIR written with {count} images: {path}", images, writer.DicomDirPath);
            }
            catch (Exception ex)
            {
                summary.DicomDirError = ex.Message;
                Logger.Error(ex, "Could not write the DICOMDIR");
            }
        }

        private void Complete(StoreResult result)
        {
            _results.Enqueue(result);
            ImageCompleted?.Invoke(result);
        }

        /// <summary>Transcodes and saves images one at a time as they are requested. Saving happens first, so the file exists even if sending then fails.</summary>
        private IEnumerable<SourceImage> Prepare(IEnumerable<SourceImage> images, FileSetWriter writer, CancellationToken token)
        {
            var number = 0;
            foreach (var image in images)
            {
                var current = ++number;
                image.File = TransferSyntaxConverter.Convert(image.File, _options.TransferSyntax, token, (done, total) => FrameEncoded?.Invoke(current, done, total));
                image.PixelBytes = PixelDataSize(image.File.Dataset);

                if (_options.Save)
                {
                    try
                    {
                        image.SavedPath = writer.Save(image);
                        Logger.Information("Saved {path}", image.SavedPath);
                    }
                    catch (Exception ex)
                    {
                        image.SaveError = ex.Message;
                        Logger.Error(ex, "Could not save series {series} instance {instance}", image.SeriesNumber, image.InstanceNumber);
                    }
                }

                yield return image;
            }
        }

        /// <summary>A report or a document has no pixels to count, and a video stream is counted as a whole.</summary>
        private static long PixelDataSize(DicomDataset dataset) => TransferSyntaxConverter.PixelBytes(dataset);

        private RunSummary Summarize(StoreSendOutcome send, TimeSpan elapsed)
        {
            var results = _results.ToList();
            var completed = results.Count(r => r.State == StoreState.Success || r.State == StoreState.Warning || r.State == StoreState.Failure || r.State == StoreState.Cancel);
            var summary = new RunSummary
            {
                Results = results,
                Expected = _source.ExpectedCount,
                Success = results.Count(r => r.State == StoreState.Success),
                Warning = results.Count(r => r.State == StoreState.Warning),
                Failure = results.Count(r => r.State == StoreState.Failure),
                Cancelled = results.Count(r => r.State == StoreState.Cancel),
                Saved = results.Count(r => r.SavedPath != null),
                SaveFailed = results.Count(r => r.SaveError != null),
                PixelBytes = results.Sum(r => r.PixelBytes),
                Elapsed = elapsed,
                WasCancelled = send.Cancelled,
                FatalError = send.FatalError,
                FailureKind = send.FailureKind,
                AssociationCount = send.AssociationCount,
                NegotiationSummary = send.NegotiationSummary,
                FallbackWarning = send.FallbackWarning,
            };

            summary.NotSent = _options.Send ? Math.Max(0, summary.Expected - completed) : 0;
            var done = _options.Send ? completed : results.Count;
            summary.ImagesPerSecond = elapsed.TotalSeconds > 0 ? done / elapsed.TotalSeconds : 0;
            return summary;
        }
    }
}
