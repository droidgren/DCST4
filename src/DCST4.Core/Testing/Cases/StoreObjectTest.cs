using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using FellowOakDicom;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>
    /// Sends one small object of one type with C-STORE and keeps a copy to compare with later. Its result is the "accepted" step
    /// of the chain, and the later tests of the run work on what it stored.
    /// </summary>
    public sealed class StoreObjectTest : ITestCase
    {
        private readonly DatasetProfile _profile;
        private readonly string _objectType;
        private readonly EdgeCases _edgeCases;
        private readonly int _frames;
        private readonly string _displayName;

        /// <summary><paramref name="frames"/> replaces the profile's own number, where that makes too large an object. 0 keeps it.</summary>
        public StoreObjectTest(DatasetProfile profile, string objectType = null, EdgeCases edgeCases = EdgeCases.None, int frames = 0)
        {
            _profile = profile;
            _objectType = objectType ?? profile.Id;
            _edgeCases = edgeCases;
            _frames = frames;
            _displayName = profile.DisplayName;
        }

        public string Id => "store-" + _objectType;
        public string Name => "C-STORE: " + DisplayName;
        public TestCategory Category => TestCategory.Integrity;
        public string Description => _edgeCases == EdgeCases.None
            ? $"Sends one small {_displayName} object with C-STORE and keeps a copy to compare with later. The query, retrieve, integrity and Storage Commitment tests later in the run work on what it stored."
            : $"Sends one small {_displayName} object with private tags, long strings and nested sequences, and checks what the destination keeps of them when it is retrieved later in the run.";
        public string Group => TestGroups.Store;
        public bool StoresObjects => true;
        public StorageEstimate Storage => StorageEstimate.Of(_profile, Fixtures.Matrix, _frames);

        private string DisplayName => _edgeCases == EdgeCases.None ? _displayName : _displayName + " with private tags and edge cases";

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var session = context.Session;
            var selection = StoreSyntaxSelector.Select(_profile, context, _edgeCases);
            var request = Fixtures.Request(context, _profile.Id, Fixtures.StorePurpose, selection.Item1, edgeCases: _edgeCases);
            request.Frames = _frames;
            var generator = new TestDatasetGenerator(request);

            var record = new StoredObjectRecord
            {
                ObjectType = _objectType,
                DisplayName = DisplayName,
                Manifest = generator.Manifest,
                TransferSyntaxUid = generator.TransferSyntax.UID.UID,
            };
            session.Objects.RemoveAll(o => o.ObjectType == _objectType);
            session.Objects.Add(record);

            var result = new TestResult();
            result.Details["transferSyntax"] = generator.TransferSyntax.UID.Name;
            result.Details["transferSyntaxSelectedBy"] = selection.Item2;

            if (generator.IsUnavailable)
            {
                var reason = generator.Manifest.Unavailable.Select(u => u.Reason).FirstOrDefault();
                record.Accepted = ChainStep.Of(TestResultClass.INDETERMINATE, $"{DisplayName} could not be generated in {generator.TransferSyntax.UID.Name}: {reason}");
                Fixtures.Set(result, record.Accepted.Result, record.Accepted.Explanation);
                return result;
            }

            var timeline = new AssociationTimeline();
            var summary = await Fixtures.SendAsync(context, generator, generator.TransferSyntax, session.NewFolder("originals-" + _objectType), timeline, token).ConfigureAwait(false);

            foreach (var manifest in generator.Manifest.Instances)
            {
                var store = summary.Results.FirstOrDefault(r => r.SopInstanceUid == manifest.SopInstanceUid);
                record.Instances.Add(new StoredInstance { Manifest = manifest, Store = store, SavedPath = store?.SavedPath });
            }

            record.Accepted = StoreMapping.Classify(summary, DisplayName);
            Fixtures.Set(result, record.Accepted.Result, record.Accepted.Explanation);

            foreach (var image in summary.Results.Where(r => r.StatusCode.HasValue && r.AcceptedTransferSyntax != null))
            {
                Fixtures.AddStatus(result, "C-STORE", image.StatusCode.Value);
            }
            foreach (var instance in record.Instances)
            {
                result.Uids[instance.Manifest.Role + ".sopInstanceUid"] = instance.Manifest.SopInstanceUid;
            }
            result.Uids["studyInstanceUid"] = record.StudyInstanceUid ?? string.Empty;
            result.Details["objects"] = Fixtures.Int(summary.Results.Count);
            result.Details["negotiated"] = summary.NegotiationSummary ?? string.Empty;
            result.NegotiatedContexts = timeline.Negotiation;
            result.Timeline = timeline.Events;
            return result;
        }
    }
}
