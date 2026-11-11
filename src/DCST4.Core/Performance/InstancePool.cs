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
        internal PoolInstance(int entryIndex, string sopClassUid, DicomDataset header, byte[] tail, int headerBytes)
        {
            EntryIndex = entryIndex;
            SopClassUid = sopClassUid;
            Header = header;
            Tail = tail;
            HeaderBytes = headerBytes;
        }

        /// <summary>Which object type of the mix this is, counted from 0.</summary>
        public int EntryIndex { get; }
        public string SopClassUid { get; }
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
        public long AverageWireBytes => Instances.Count == 0 ? 0 : (long)Instances.Average(i => (double)i.WireBytes);

        /// <summary>
        /// Generates the pool. Every object type uses the same seed, so they share one patient from the test personnummer
        /// list. Throws <see cref="PoolUnavailableException"/> if the encoder cannot produce the mix here.
        /// </summary>
        public static InstancePool Build(DataMix mix, int seed, string uidRoot, TestPatientNaming naming, CancellationToken token)
        {
            if (mix == null) throw new ArgumentNullException(nameof(mix));
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
                    instances.Add(Split(entryIndex, image.File.Dataset, mix.Syntax));
                }
            }
            // In turn by object type, so that a short run of a mix still sends every type: CR, US, CR, US ...
            var interleaved = instances.GroupBy(i => i.EntryIndex)
                .SelectMany(g => g.Select((item, position) => new { item, position }))
                .OrderBy(x => x.position).ThenBy(x => x.item.EntryIndex)
                .Select(x => x.item).ToList();
            return new InstancePool(mix, interleaved);
        }

        private static PoolInstance Split(int entryIndex, DicomDataset dataset, DicomTransferSyntax syntax)
        {
            if (dataset.InternalTransferSyntax != syntax)
            {
                throw new InvalidOperationException($"The generator gave {dataset.InternalTransferSyntax.UID.Name} where {syntax.UID.Name} was asked for.");
            }

            var header = new DicomDataset(syntax);
            var tail = new DicomDataset(syntax);
            foreach (var item in dataset)
            {
                if (item.Tag.CompareTo(DicomTag.PixelData) < 0) header.Add(item);
                else tail.Add(item);
            }

            // A private or padding element after Pixel Data would be sent as it is, which is right, but stamping only
            // touches the header, so none of the mixes may have a UID there.
            var tailBytes = Encode(tail, syntax);
            var headerBytes = Encode(header, syntax).Length;
            return new PoolInstance(entryIndex, dataset.GetSingleValue<string>(DicomTag.SOPClassUID), header, tailBytes, headerBytes);
        }

        /// <summary>The elements of a dataset as they go on the wire, with no File Meta Information.</summary>
        internal static byte[] Encode(DicomDataset dataset, DicomTransferSyntax syntax)
        {
            using (var stream = new MemoryStream())
            {
                var writer = new DicomWriter(syntax, new DicomWriteOptions(), new StreamByteTarget(stream));
                new DicomDatasetWalker(dataset).Walk(writer);
                return stream.ToArray();
            }
        }

        /// <summary>A value for one run only, so that two runs never give the same UIDs, even with the same seed.</summary>
        public static string NewRunNonce()
        {
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    /// <summary>One send, ready to go: the stamped header and the shared tail.</summary>
    public sealed class StampedInstance
    {
        public string SopClassUid { get; internal set; }
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
