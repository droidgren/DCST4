using DCST4.Core.Performance;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Files
{
    /// <summary>How the files of a folder are sent.</summary>
    public sealed class FolderSendOptions
    {
        /// <summary>
        /// True (default): each patient becomes a test patient, every UID of an instance gets a new one under
        /// <see cref="UidRoot"/>, and the objects get DCST4's marking. False: the files go exactly as they are, real patient included.
        /// </summary>
        public bool Reidentify { get; set; } = true;
        /// <summary>The profile's UID root. Null or empty uses the UUID arc 2.25.</summary>
        public string UidRoot { get; set; }
        public TestPatientNaming PatientNaming { get; set; } = TestPatientNames.Default;
        /// <summary>Makes this run's UIDs its own. Null draws a new one.</summary>
        public string RunNonce { get; set; }
        /// <summary>For tests: the draw of the test patients. Null follows from the nonce.</summary>
        public int? Seed { get; set; }
        /// <summary>For tests: the date ages are counted to. Null is today.</summary>
        public DateTime? Today { get; set; }
        public TestPersonnummerRegistry Registry { get; set; }
        /// <summary>Every file gets the same test patient. For a load run, which puts files of any source patient in one study.</summary>
        public bool OnePatient { get; set; }
    }

    /// <summary>
    /// Existing files, read one at a time as they are sent and re-identified in memory, or sent as they are. The files on disk
    /// are never changed. Series and instances are numbered through the run, from 1, so that saving them never gives two
    /// files one name; the tags keep the modality's own numbers.
    /// </summary>
    public sealed class FolderImageSource : IImageSource, IPlannedContexts
    {
        private static readonly ILogger Logger = Log.ForContext<FolderImageSource>();

        private readonly FolderScan _scan;
        private readonly FolderSendOptions _options;
        private readonly Dictionary<WatermarkOutcome, int> _watermarks = new Dictionary<WatermarkOutcome, int>();

        public FolderImageSource(FolderScan scan, FolderSendOptions options)
        {
            _scan = scan ?? throw new ArgumentNullException(nameof(scan));
            _options = options ?? new FolderSendOptions();
            if (_options.Reidentify)
            {
                Reidentifier = new FileReidentifier(scan, _options.UidRoot, _options.RunNonce ?? InstancePool.NewRunNonce(), _options.PatientNaming,
                    _options.Registry, _options.Seed, _options.Today, _options.OnePatient);
            }
        }

        public int ExpectedCount => _scan.Files.Count;

        /// <summary>
        /// Each SOP class of the folder in the syntax it will go in: the file's own, or the run's. An object without pixels
        /// stays uncompressed in a compressed run, as <see cref="TransferSyntaxConverter"/> leaves it.
        /// </summary>
        public IReadOnlyCollection<KeyValuePair<string, DicomTransferSyntax>> PlannedContexts(DicomTransferSyntax target) =>
            _scan.Files
                .Select(f => new KeyValuePair<string, DicomTransferSyntax>(f.SopClassUid,
                    target == null || (target.IsEncapsulated && !f.HasPixelData) ? f.TransferSyntax : target))
                .GroupBy(p => p.Key + "|" + p.Value.UID.UID).Select(g => g.First()).ToList();
        public FolderScan Scan => _scan;

        /// <summary>Null when the files are sent unchanged.</summary>
        public FileReidentifier Reidentifier { get; }

        /// <summary>How many files got the watermark, and why the others did not. Filled as the files are read.</summary>
        public IReadOnlyDictionary<WatermarkOutcome, int> Watermarks => _watermarks;

        public IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken)
        {
            if (Reidentifier != null)
            {
                Logger.Information("Re-identifying {files} files of {patients} patients as test patients: {testPatients}",
                    _scan.Files.Count, _scan.PatientCount, string.Join(", ", Reidentifier.Patients));
            }
            else
            {
                // The real patient is not logged: the log is kept on this computer.
                Logger.Warning("Sending {files} files of {patients} patients unchanged, with their own patients and UIDs", _scan.Files.Count, _scan.PatientCount);
            }

            var series = new Dictionary<string, int>(StringComparer.Ordinal);
            var instances = new Dictionary<int, int>();
            foreach (var scanned in _scan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!series.TryGetValue(scanned.SeriesInstanceUid, out var seriesNumber))
                {
                    seriesNumber = series.Count + 1;
                    series[scanned.SeriesInstanceUid] = seriesNumber;
                }
                instances.TryGetValue(seriesNumber, out var instance);
                instances[seriesNumber] = ++instance;

                var file = Open(scanned);
                yield return new SourceImage(file, seriesNumber, instance) { SourcePath = scanned.Path };
            }

            if (Reidentifier != null)
            {
                Logger.Information("Watermark: {outcomes}", string.Join(", ", _watermarks.OrderBy(w => w.Key).Select(w => $"{w.Key} {w.Value}")));
            }
        }

        /// <summary>Opens and re-identifies one file. A file that has gone or changed since it was read stops the run, naming it.</summary>
        public DicomFile Open(ScannedFile scanned)
        {
            DicomFile file;
            try
            {
                file = DicomFile.Open(scanned.Path, FileReadOption.ReadLargeOnDemand);
            }
            catch (Exception ex)
            {
                throw new SourceFileException($"Could not read {scanned.Path}: {ex.Message}", ex);
            }

            if (Reidentifier == null) return file;
            try
            {
                var outcome = Reidentifier.Apply(file, scanned);
                _watermarks.TryGetValue(outcome, out var count);
                _watermarks[outcome] = count + 1;
            }
            catch (Exception ex)
            {
                throw new SourceFileException($"Could not re-identify {scanned.Path}: {ex.Message}", ex);
            }
            return file;
        }
    }

    /// <summary>A file of the folder could not be read or re-identified, which is a local problem.</summary>
    public sealed class SourceFileException : Exception
    {
        public SourceFileException(string message, Exception inner) : base(message, inner) { }
    }
}
