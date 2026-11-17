using DCST4.Core.Files;
using DCST4.Core.Pipeline;
using DCST4.Core.Generation.Realism;
using DCST4.Core.Generation;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.IO;
using FellowOakDicom.IO.Writer;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace DCST4.Core.Performance
{
    /// <summary>The encoder cannot produce a mix on this computer, which is a local problem.</summary>
    public sealed class PoolUnavailableException : Exception
    {
        public PoolUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// One object of the pool, split at Pixel Data: the attributes before it, which are stamped and encoded again for every
    /// send, and Pixel Data with whatever follows it, encoded once and shared by every send.
    /// </summary>
    public sealed class PoolInstance
    {
        internal PoolInstance(int entryIndex, string sopClassUid, DicomTransferSyntax syntax, DicomDataset header, byte[] tail, int headerBytes)
        {
            EntryIndex = entryIndex;
            SopClassUid = sopClassUid;
            TransferSyntax = syntax;
            Header = header;
            Tail = tail;
            HeaderBytes = headerBytes;
        }

        /// <summary>Which object type of the mix this is, counted from 0.</summary>
        public int EntryIndex { get; }
        public string SopClassUid { get; }
        /// <summary>The syntax this object goes in: the mix's, or for files sent as they are, the file's own.</summary>
        public DicomTransferSyntax TransferSyntax { get; }
        internal DicomDataset Header { get; }
        /// <summary>Pixel Data as it goes on the wire, shared by every send.</summary>
        public byte[] Tail { get; }
        /// <summary>The size of the encoded attributes before Pixel Data, as generated.</summary>
        public int HeaderBytes { get; }
        public long WireBytes => HeaderBytes + Tail.Length;
        public bool HasFrameOfReference => Header.Contains(DicomTag.FrameOfReferenceUID);
    }

    /// <summary>The objects a load run sends, generated and encoded in the mix's syntax before any is sent.</summary>
    public sealed class InstancePool
    {
        private InstancePool(DataMix mix, List<PoolInstance> instances)
        {
            Mix = mix;
            Instances = instances;
        }

        public DataMix Mix { get; }
        public DicomTransferSyntax Syntax => Mix.Syntax;
        public IReadOnlyList<PoolInstance> Instances { get; }
        public IReadOnlyList<string> SopClasses => Instances.Select(i => i.SopClassUid).Distinct().ToList();

        /// <summary>One presentation context for each SOP class and syntax the pool sends: one syntax for a generated mix, any number for files.</summary>
        public IReadOnlyList<PoolContext> Contexts => Instances.Select(i => new PoolContext(i.SopClassUid, i.TransferSyntax.UID.UID)).Distinct().ToList();

        /// <summary>An association offers at most 128 presentation contexts.</summary>
        public const int MaxContexts = 128;
        public long AverageWireBytes => Instances.Count == 0 ? 0 : (long)Instances.Average(i => (double)i.WireBytes);

        /// <summary>
        /// Generates the pool. Every object type uses the same seed, so they share one patient from the test personnummer
        /// list. Throws <see cref="PoolUnavailableException"/> if the encoder cannot produce the mix here. The noise of a
        /// realistic image is drawn once per object in the pool, not again for every send.
        /// </summary>
        public static InstancePool Build(DataMix mix, int seed, string uidRoot, TestPatientNaming naming, CancellationToken token,
            RealismLevel realism = RealismLevel.Off, LossyCompressionSettings lossy = null)
        {
            if (mix == null) throw new ArgumentNullException(nameof(mix));
            if (mix.Files != null) return BuildFromFiles(mix, seed, uidRoot, naming, token, lossy);
            var instances = new List<PoolInstance>();
            for (var entryIndex = 0; entryIndex < mix.Entries.Count; entryIndex++)
            {
                var entry = mix.Entries[entryIndex];
                var request = new TestDatasetRequest
                {
                    ProfileId = entry.ProfileId,
                    Seed = seed,
                    UidRoot = uidRoot,
                    PatientNaming = naming,
                    InstancesPerSeries = Math.Max(1, mix.PoolPerEntry),
                    Rows = entry.Rows,
                    Columns = entry.Columns,
                    BitsStored = entry.BitsStored,
                    Frames = entry.Frames,
                    TransferSyntax = mix.Syntax,
                    PrivatePayloadBytes = entry.PrivatePayloadBytes,
                    Realism = realism,
                    Lossy = lossy,
                };
                var problems = request.Validate();
                if (problems.Count > 0) throw new ArgumentException($"Mix {mix.Id}: " + string.Join(" ", problems));

                var generator = new TestDatasetGenerator(request);
                if (generator.IsUnavailable)
                {
                    var reason = generator.Manifest.Unavailable?.FirstOrDefault()?.Reason ?? "the encoder cannot produce it";
                    throw new PoolUnavailableException($"Mix {mix.Id} cannot be generated on this computer: {entry.ProfileId} in {mix.Syntax.UID.Name}: {reason}");
                }

                foreach (var image in generator.GetImages(token))
                {
                    token.ThrowIfCancellationRequested();
                    var shared = SharedBytes(image.File.Dataset);
                    if (shared > DataMix.MaxPoolBytes)
                    {
                        throw new PoolUnavailableException($"Mix {mix.Id}: one {entry.ProfileId} object is {shared / 1048576.0:0} MB, and a load run keeps its objects in memory, which holds at most {DataMix.MaxPoolBytes / 1048576} MB each. Use fewer frames, a smaller payload or a compressed transfer syntax, or send it with Generate & Send.");
                    }
                    instances.Add(Split(entryIndex, image.File.Dataset, mix.Syntax, shared));
                }
            }
            return new InstancePool(mix, Interleave(instances));
        }

        /// <summary>In turn by object type, so that a short run of a mix still sends every type: CR, US, CR, US ...</summary>
        private static List<PoolInstance> Interleave(List<PoolInstance> instances) =>
            instances.GroupBy(i => i.EntryIndex)
                .SelectMany(g => g.Select((item, position) => new { item, position }))
                .OrderBy(x => x.position).ThenBy(x => x.item.EntryIndex)
                .Select(x => x.item).ToList();

        /// <summary>
        /// The pool of a mix of existing files: each file once, re-identified, with every source patient as one test patient,
        /// since a load run puts the objects of a study together whatever patient they came from. A series of the files is an
        /// object type of the mix, so its files stay in one series. The files are kept in memory, so all of them together may
        /// take at most <see cref="DataMix.MaxPoolBytes"/>.
        /// </summary>
        private static InstancePool BuildFromFiles(DataMix mix, int seed, string uidRoot, TestPatientNaming naming, CancellationToken token, LossyCompressionSettings lossy)
        {
            if (mix.Files.Files.Count == 0) throw new PoolUnavailableException("The custom mix has no files to send.");
            var source = new FolderImageSource(mix.Files, new FolderSendOptions
            {
                UidRoot = uidRoot,
                PatientNaming = naming,
                RunNonce = "pool/" + seed.ToString(CultureInfo.InvariantCulture),
                Seed = seed,
                OnePatient = true,
            });

            var instances = new List<PoolInstance>();
            long total = 0;
            foreach (var image in source.GetImages(token))
            {
                token.ThrowIfCancellationRequested();
                var file = image.File;
                if (mix.FilesSyntax != null)
                {
                    try
                    {
                        file = TransferSyntaxConverter.Convert(file, mix.FilesSyntax, token, null, lossy);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        throw new PoolUnavailableException($"{image.SourcePath} cannot be put in {mix.FilesSyntax.UID.Name} on this computer: {ex.Message}");
                    }
                }

                var shared = SharedBytes(file.Dataset);
                total += shared;
                if (total > DataMix.MaxPoolBytes)
                {
                    throw new PoolUnavailableException($"The files of the custom mix take more than {DataMix.MaxPoolBytes / 1048576} MB, and a load run keeps its objects in memory. Choose a folder with fewer or smaller files, or send them with Send files.");
                }
                instances.Add(Split(image.SeriesNumber - 1, file.Dataset, file.Dataset.InternalTransferSyntax, shared));
            }

            var pool = new InstancePool(mix, Interleave(instances));
            if (pool.Contexts.Count > MaxContexts)
            {
                throw new PoolUnavailableException($"The files have {pool.Contexts.Count} combinations of SOP class and transfer syntax, and an association offers at most {MaxContexts}. Choose one transfer syntax for them on the Files tab.");
            }
            return pool;
        }

        /// <summary>
        /// Where the part that every send shares begins: at a private payload if the object has one, otherwise at Pixel Data.
        /// The payload is as large as the pixels or larger, so encoding it again for every send would measure DCST4.
        /// </summary>
        private static bool IsShared(DicomDataset dataset, DicomTag tag)
        {
            var start = PrivatePayload.BytesIn(dataset) > 0 ? PrivatePayload.SequenceTag : DicomTag.PixelData;
            return tag.Group > start.Group || (tag.Group == start.Group && tag.Element >= start.Element);
        }

        /// <summary>The size of the large values in the shared part, which is nearly all of it.</summary>
        private static long SharedBytes(DicomDataset dataset)
        {
            var size = PrivatePayload.BytesIn(dataset);
            var pixels = dataset.GetDicomItem<DicomItem>(DicomTag.PixelData);
            if (pixels is DicomFragmentSequence fragments) size += fragments.Fragments.Sum(f => f.Size + 8) + fragments.OffsetTable.Count * 4L;
            else if (pixels is DicomElement element) size += element.Buffer.Size;
            return size;
        }

        private static PoolInstance Split(int entryIndex, DicomDataset dataset, DicomTransferSyntax syntax, long sharedBytes)
        {
            if (dataset.InternalTransferSyntax != syntax)
            {
                throw new InvalidOperationException($"The generator gave {dataset.InternalTransferSyntax.UID.Name} where {syntax.UID.Name} was asked for.");
            }

            var header = new DicomDataset(syntax);
            var tail = new DicomDataset(syntax);
            foreach (var item in dataset)
            {
                if (IsShared(dataset, item.Tag)) tail.Add(item);
                else header.Add(item);
            }

            // Whatever follows the start of the shared part is sent as it is, which is right, but stamping only touches
            // the header, so none of the mixes may have a UID after it.
            var tailBytes = Encode(tail, syntax, sharedBytes);
            var headerBytes = Encode(header, syntax).Length;
            return new PoolInstance(entryIndex, dataset.GetSingleValue<string>(DicomTag.SOPClassUID), syntax, header, tailBytes, headerBytes);
        }

        /// <summary>
        /// The elements of a dataset as they go on the wire, with no File Meta Information. <paramref name="expectedBytes"/>
        /// sizes the buffer from the start, since one that grows by doubling cannot reach a gigabyte.
        /// </summary>
        internal static byte[] Encode(DicomDataset dataset, DicomTransferSyntax syntax, long expectedBytes = 0)
        {
            using (var stream = new MemoryStream((int)Math.Min(int.MaxValue / 2, expectedBytes + EncodingSlackBytes)))
            {
                var writer = new DicomWriter(syntax, new DicomWriteOptions(), new StreamByteTarget(stream));
                new DicomDatasetWalker(dataset).Walk(writer);
                return stream.ToArray();
            }
        }

        /// <summary>Room for the tags and lengths around the large values, and for the functional groups of an enhanced object.</summary>
        private const int EncodingSlackBytes = 64 * 1024;

        /// <summary>A value for one run only, so that two runs never give the same UIDs, even with the same seed.</summary>
        public static string NewRunNonce()
        {
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    /// <summary>A presentation context of a load run: one SOP class in one transfer syntax.</summary>
    public struct PoolContext : IEquatable<PoolContext>
    {
        public PoolContext(string sopClassUid, string transferSyntaxUid)
        {
            SopClassUid = sopClassUid;
            TransferSyntaxUid = transferSyntaxUid;
        }

        public string SopClassUid { get; }
        public string TransferSyntaxUid { get; }

        public bool Equals(PoolContext other) => string.Equals(SopClassUid, other.SopClassUid, StringComparison.Ordinal) && string.Equals(TransferSyntaxUid, other.TransferSyntaxUid, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is PoolContext other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(SopClassUid ?? string.Empty) * 31 + StringComparer.Ordinal.GetHashCode(TransferSyntaxUid ?? string.Empty);
    }

    /// <summary>One send, ready to go: the stamped header and the shared tail.</summary>
    public sealed class StampedInstance
    {
        public string SopClassUid { get; internal set; }
        public string TransferSyntaxUid { get; internal set; }
        public string SopInstanceUid { get; internal set; }
        public string StudyInstanceUid { get; internal set; }
        public string SeriesInstanceUid { get; internal set; }
        public IReadOnlyList<ArraySegment<byte>> Parts { get; internal set; }
        public long Bytes { get; internal set; }
    }

    /// <summary>
    /// Gives each send new UIDs under the profile's root, as a modality would for a new exam: a new SOP Instance UID per
    /// send, and a new study, series per object type and Frame of Reference per <see cref="LoadSettings.InstancesPerStudy"/>
    /// sends of one worker. Each worker has its own stamper and its own copy of each header. The patient stays the one the
    /// pool was generated with.
    /// </summary>
    public sealed class UidStamper
    {
        private readonly DeterministicUidSource _uids;
        private readonly string _accessionScope;
        private readonly Dictionary<PoolInstance, DicomDataset> _headers = new Dictionary<PoolInstance, DicomDataset>();
        private readonly Dictionary<int, int> _instanceInSeries = new Dictionary<int, int>();
        private readonly int _perStudy;
        private long _sent;
        private long _study = -1;
        private string _studyUid;
        private string _accession;

        public UidStamper(string uidRoot, string runNonce, int worker, int instancesPerStudy = LoadSettings.InstancesPerStudy)
        {
            var scope = "load/" + runNonce + "/w" + worker.ToString(CultureInfo.InvariantCulture);
            _uids = new DeterministicUidSource(uidRoot, scope);
            _accessionScope = scope;
            _perStudy = Math.Max(1, instancesPerStudy);
        }

        public StampedInstance Stamp(PoolInstance item)
        {
            var study = _sent / _perStudy;
            if (study != _study)
            {
                _study = study;
                _studyUid = _uids.Uid("s" + Long(study) + "/study");
                _accession = "L" + (Math.Abs((long)BitConverter.ToInt64(SeededValues.Sha256(_accessionScope + "/s" + Long(study)), 0) % 1000000000000000L)).ToString("D15", CultureInfo.InvariantCulture);
                _instanceInSeries.Clear();
            }

            if (!_headers.TryGetValue(item, out var header))
            {
                header = item.Header.Clone();
                _headers[item] = header;
            }

            _instanceInSeries.TryGetValue(item.EntryIndex, out var number);
            _instanceInSeries[item.EntryIndex] = ++number;

            var entry = Long(item.EntryIndex);
            var sop = _uids.Uid("i" + Long(_sent));
            var series = _uids.Uid("s" + Long(study) + "/series/" + entry);
            header.AddOrUpdate(DicomTag.SOPInstanceUID, sop);
            header.AddOrUpdate(DicomTag.StudyInstanceUID, _studyUid);
            header.AddOrUpdate(DicomTag.SeriesInstanceUID, series);
            header.AddOrUpdate(DicomTag.AccessionNumber, _accession);
            header.AddOrUpdate(DicomTag.SeriesNumber, (item.EntryIndex + 1).ToString(CultureInfo.InvariantCulture));
            header.AddOrUpdate(DicomTag.InstanceNumber, number.ToString(CultureInfo.InvariantCulture));
            if (item.HasFrameOfReference) header.AddOrUpdate(DicomTag.FrameOfReferenceUID, _uids.Uid("s" + Long(study) + "/for/" + entry));
            _sent++;

            var headerBytes = InstancePool.Encode(header, header.InternalTransferSyntax);
            return new StampedInstance
            {
                SopClassUid = item.SopClassUid,
                TransferSyntaxUid = item.TransferSyntax.UID.UID,
                SopInstanceUid = sop,
                StudyInstanceUid = _studyUid,
                SeriesInstanceUid = series,
                Parts = new[] { new ArraySegment<byte>(headerBytes), new ArraySegment<byte>(item.Tail) },
                Bytes = headerBytes.Length + item.Tail.Length,
            };
        }

        private static string Long(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
