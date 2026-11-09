using DCST4.Core.Capability;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using FellowOakDicom;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DCST4.Core.Testing
{
    /// <summary>What a front-end can decide about a run, beyond which tests are in it.</summary>
    public sealed class TestRunOptions
    {
        public const int DefaultIndexWaitSeconds = 30;

        /// <summary>Null draws a new seed. A fixed seed repeats the same UIDs, which on a real system are duplicates of the first run.</summary>
        public int? Seed { get; set; }
        /// <summary>What the target accepts, from an earlier probe. A probe in this run replaces it.</summary>
        public CapabilityMatrix Matrix { get; set; }
        /// <summary>Send every object type in this syntax where the type allows it. Null chooses per type.</summary>
        public DicomTransferSyntax SyntaxOverride { get; set; }
        /// <summary>Where originals and retrieved files go, and stay. Null uses a temporary folder that is deleted after the run.</summary>
        public string WorkFolder { get; set; }
        /// <summary>How long a stored object may take to appear in a query.</summary>
        public int IndexWaitSeconds { get; set; } = DefaultIndexWaitSeconds;
    }

    /// <summary>One step of the chain for one object type.</summary>
    public sealed class ChainStep
    {
        public TestResultClass Result { get; set; }
        public string Explanation { get; set; }

        public static ChainStep Of(TestResultClass result, string explanation) => new ChainStep { Result = result, Explanation = explanation };
    }

    /// <summary>accepted, indexed, retrievable and intact for one object type. A step that did not run is null.</summary>
    public sealed class ChainRow
    {
        /// <summary>The name in the store test's id, for example ct or ct-edge.</summary>
        public string ObjectType { get; set; }
        public string DisplayName { get; set; }
        public string TransferSyntaxUid { get; set; }
        public ChainStep Accepted { get; set; }
        public ChainStep Indexed { get; set; }
        public ChainStep Retrievable { get; set; }
        public ChainStep Intact { get; set; }
        /// <summary>Storage Commitment. Null until that exists.</summary>
        public ChainStep Committed { get; set; }
    }

    /// <summary>One object that a store test sent.</summary>
    public sealed class StoredInstance
    {
        public ManifestInstance Manifest { get; set; }
        /// <summary>The file as it was sent.</summary>
        public string SavedPath { get; set; }
        public StoreResult Store { get; set; }
        /// <summary>Only with our own copy to compare with, since the later steps all need one.</summary>
        public bool WasStored => SavedPath != null && Store != null && (Store.State == StoreState.Success || Store.State == StoreState.Warning);
    }

    /// <summary>What one retrieve brought back for one object type.</summary>
    public sealed class RetrieveRecord
    {
        public RetrieveMethod Method { get; set; }
        public ChainStep Step { get; set; }
        public RetrieveOutcome Outcome { get; set; }
    }

    /// <summary>One object type on its way through the chain.</summary>
    public sealed class StoredObjectRecord
    {
        public string ObjectType { get; set; }
        public string DisplayName { get; set; }
        public DatasetManifest Manifest { get; set; }
        public string TransferSyntaxUid { get; set; }
        public List<StoredInstance> Instances { get; } = new List<StoredInstance>();
        public ChainStep Accepted { get; set; }
        public ChainStep Indexed { get; set; }
        public RetrieveRecord CGet { get; set; }
        public RetrieveRecord CMove { get; set; }
        public ChainStep Intact { get; set; }

        public IEnumerable<StoredInstance> Stored => Instances.Where(i => i.WasStored);
        public IEnumerable<RetrieveRecord> Retrieves => new[] { CGet, CMove }.Where(r => r != null);

        /// <summary>The study of this object type. Every instance of one object type belongs to it.</summary>
        public string StudyInstanceUid => Instances.Select(i => i.Manifest.StudyInstanceUid).FirstOrDefault();
    }

    /// <summary>
    /// What the tests of one run hand on to each other, since a store has to come before the query and the retrieve that
    /// look for what it stored. One run, one thread at a time: <see cref="TestRunner"/> runs the tests in order.
    /// </summary>
    public sealed class TestRunSession
    {
        private static readonly ILogger Logger = Log.ForContext<TestRunSession>();

        private readonly bool _ownsWorkFolder;
        private string _workFolder;

        public TestRunSession(TestRunOptions options = null)
        {
            Options = options ?? new TestRunOptions();
            Seed = Options.Seed ?? DrawSeed();
            Matrix = Options.Matrix;
            _workFolder = Options.WorkFolder;
            _ownsWorkFolder = string.IsNullOrWhiteSpace(Options.WorkFolder);
        }

        public TestRunOptions Options { get; }

        /// <summary>Everything this run generates follows from it. Written to the result file if anything was generated.</summary>
        public int Seed { get; }

        /// <summary>Whether a test generated data, so that a run which did not can still be compared with an identical run.</summary>
        internal bool SeedWasUsed { get; private set; }

        /// <summary>From a probe in this run, or else the one the front-end supplied.</summary>
        public CapabilityMatrix Matrix { get; set; }

        /// <summary>In the order they were stored.</summary>
        public List<StoredObjectRecord> Objects { get; } = new List<StoredObjectRecord>();

        /// <summary>Created when first needed.</summary>
        public string WorkFolder
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_workFolder))
                {
                    _workFolder = Path.Combine(Path.GetTempPath(), "DCST4-" + Guid.NewGuid().ToString("N"));
                }
                Directory.CreateDirectory(_workFolder);
                return _workFolder;
            }
        }

        /// <summary>A new folder below the work folder.</summary>
        public string NewFolder(string name)
        {
            var path = Path.Combine(WorkFolder, name);
            for (var n = 2; Directory.Exists(path); n++) path = Path.Combine(WorkFolder, name + "-" + n.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// A seed of its own for one purpose. The Patient ID follows from the seed alone and the name also from the character
        /// set, so two purposes on one seed would put one Patient ID under two names and cause the conflict a test looks for.
        /// </summary>
        public int SeedFor(string purpose)
        {
            SeedWasUsed = true;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Seed.ToString(CultureInfo.InvariantCulture) + "/" + purpose));
                return BitConverter.ToInt32(hash, 0) & int.MaxValue;
            }
        }

        public List<ChainRow> BuildChain() => Objects.Select(o => new ChainRow
        {
            ObjectType = o.ObjectType,
            DisplayName = o.DisplayName,
            TransferSyntaxUid = o.TransferSyntaxUid,
            Accepted = o.Accepted,
            Indexed = o.Indexed,
            Retrievable = CombineRetrieves(o),
            Intact = o.Intact,
        }).ToList();

        /// <summary>The object can be retrieved if either way worked. If neither did, the more serious answer is the one to see.</summary>
        private static ChainStep CombineRetrieves(StoredObjectRecord record)
        {
            var retrieves = record.Retrieves.ToList();
            if (retrieves.Count == 0) return null;

            var combined = retrieves.Any(r => r.Step.Result == TestResultClass.PASS)
                ? TestResultClass.PASS
                : ResultSeverity.Worst(retrieves.Select(r => r.Step.Result));
            return ChainStep.Of(combined, string.Join(" ", retrieves.Select(r => RetrieveNames.Of(r.Method) + ": " + r.Step.Explanation)));
        }

        /// <summary>Only a folder this session made itself. The temporary folder holds test data only.</summary>
        internal void Cleanup()
        {
            if (!_ownsWorkFolder || _workFolder == null || !Directory.Exists(_workFolder)) return;
            try
            {
                Directory.Delete(_workFolder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Could not delete the temporary folder {folder}", _workFolder);
            }
        }

        private static int DrawSeed()
        {
            var bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
        }
    }

    /// <summary>Which result outweighs which when several are summed up as one.</summary>
    public static class ResultSeverity
    {
        private static readonly TestResultClass[] FromWorst =
        {
            TestResultClass.TOOL_ERROR, TestResultClass.FAIL, TestResultClass.TIMEOUT, TestResultClass.INDETERMINATE,
            TestResultClass.NOT_SUPPORTED, TestResultClass.EXPECTED_FAILURE, TestResultClass.PASS,
        };

        /// <summary>PASS for an empty list, so check for one first when nothing having happened means something.</summary>
        public static TestResultClass Worst(IEnumerable<TestResultClass> results)
        {
            var list = results.ToList();
            return FromWorst.FirstOrDefault(list.Contains);
        }
    }
}
