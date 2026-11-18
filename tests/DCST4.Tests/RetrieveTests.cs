using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using DCST4.Core.Testing;
using DCST4.Core.Verification;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>Phase 4: store, query, retrieve and the integrity oracle, against the mock SCP.</summary>
    internal static class RetrieveTests
    {
        private const int ListenPort = 41215;
        private const string RoundTrip = "store-ct,store-mr,query-index,retrieve-cget,retrieve-cmove,verify-integrity";

        private static int _port;
        private static string _temp;
        private static Action<string, bool, string> _check;

        public static async Task Run(int port, int closedPort, string temp, Action<string, bool, string> check)
        {
            _port = port;
            _temp = temp;
            _check = check;

            await TestRoundTrip();
            await TestRetrieveSyntaxes();
            await TestTomosynthesis();
            await TestXrayObjects();
            await TestUsVolume();
            await TestModifications();
            await TestMoveDiagnosis(closedPort);
            await TestStoreMapping(closedPort);
            await TestPartialAndCancel();
            await TestQuery();
            await TestDuplicates();
            await TestDeterminismAndSelection();
        }

        // ---------------------------------------------------------------- helpers

        private static TargetProfile Profile(int responseTimeout = 15, int? port = null) => new TargetProfile
        {
            Name = "Mock",
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = port ?? _port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = responseTimeout },
            LocalScpAe = "DCST4",
            LocalScpPort = ListenPort,
            UidRoot = "1.2.826.0.1.3680043.10.999",
        };

        private static void Fresh()
        {
            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MoveDestinations["DCST4"] = Tuple.Create("127.0.0.1", ListenPort);
        }

        private static Task<TestRunReport> Execute(string tests, TargetProfile profile = null, TestRunOptions options = null, CancellationToken token = default(CancellationToken))
        {
            var selected = TestCatalog.Select(tests, out var error);
            if (selected == null) throw new InvalidOperationException(error);

            var use = options ?? new TestRunOptions();
            use.Seed = use.Seed ?? 7;
            use.IndexWaitSeconds = use.IndexWaitSeconds == TestRunOptions.DefaultIndexWaitSeconds ? 1 : use.IndexWaitSeconds;
            return new TestRunner().RunAsync(profile ?? Profile(), selected, token, DCST4.Core.TestData.TestPatientNaming.RegistrySwedishName, use);
        }

        private static TestResult Result(TestRunReport report, string id) => report.Results.Single(r => r.TestId == id);

        private static IntegritySet Set(TestRunReport report, string objectType, string method) =>
            Result(report, "verify-integrity").Integrity.Sets.Single(s => s.ObjectType == objectType && s.Method == method);

        private static TestResultClass LevelOf(IntegritySet set, IntegrityLevel level) => set.Levels.Single(l => l.Level == level).Result;

        private static string Show(TestRunReport report) =>
            string.Join("\n    ", report.Results.Select(r => $"{r.TestId}: {r.Result}: {r.Explanation}"));

        private static byte[] Flip(byte[] frame)
        {
            var changed = frame.ToArray();
            for (var i = 0; i < Math.Min(40, changed.Length); i++) changed[i] ^= 0x7F;
            return changed;
        }

        // ---------------------------------------------------------------- retrieve-syntaxes

        /// <summary>Few syntaxes, so that the section stays short: three the mock accepts, one it does not, and a video syntax.</summary>
        private static readonly DicomTransferSyntax[] FewSyntaxes =
        {
            DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1,
            DicomTransferSyntax.JPEG2000Lossless, DicomTransferSyntax.MPEG2,
        };

        /// <summary>Runs the tests, with retrieve-syntaxes asking for <see cref="FewSyntaxes"/> only.</summary>
        private static Task<TestRunReport> ExecuteFew(string tests)
        {
            var selected = TestCatalog.Select(tests, out var error);
            if (selected == null) throw new InvalidOperationException(error);
            var few = selected.Select(t => t.Id == "retrieve-syntaxes" ? new DCST4.Core.Testing.Cases.RetrieveSyntaxesTest(FewSyntaxes) : t).ToList();
            return new TestRunner().RunAsync(Profile(), few, CancellationToken.None, DCST4.Core.TestData.TestPatientNaming.RegistrySwedishName, new TestRunOptions { Seed = 7, IndexWaitSeconds = 1 });
        }

        private static DCST4.Core.Capability.CapabilityResult? CellOf(TestResult result, DicomUID sopClass, DicomTransferSyntax syntax) =>
            result.RetrieveMatrix?.Find(sopClass.UID, syntax.UID.UID)?.Result;

        private static string Cells(TestResult result) => result.RetrieveMatrix == null ? "no matrix" : string.Join(", ",
            result.RetrieveMatrix.AllCells().Select(c => $"{c.Item1.SopClassName}/{DCST4.Core.Capability.CapabilityCatalog.ShortName(DicomTransferSyntax.Parse(c.Item2.TransferSyntaxUid))}={c.Item2.Result} {c.Item2.Status}"));

        private static async Task TestRetrieveSyntaxes()
        {
            Console.WriteLine("\nRetrieve matrix: retrieve-syntaxes");
            var all = TestCatalog.All.ToList();
            var test = all.SingleOrDefault(t => t.Id == "retrieve-syntaxes");
            _check("retrieve-syntaxes exists, after retrieve-cget, and runs with 'all'", test != null && all.IndexOf(test) == all.FindIndex(t => t.Id == "retrieve-cget") + 1
                && TestCatalog.Select("all", out _).Contains(test) && !test.StoresObjects && test.Category == TestCategory.Capability, null);

            var fitted = DCST4.Core.Testing.Cases.RetrieveSyntaxesTest.Fitted(FewSyntaxes, true, false);
            _check("Fitted: a single-frame image gets no video syntax", !fitted.Contains(DicomTransferSyntax.MPEG2) && fitted.Count == 4, null);
            _check("Fitted: a multi-frame image gets them all", DCST4.Core.Testing.Cases.RetrieveSyntaxesTest.Fitted(FewSyntaxes, true, true).Count == FewSyntaxes.Length, null);
            _check("Fitted: an object without pixel data gets only the native syntaxes", DCST4.Core.Testing.Cases.RetrieveSyntaxesTest.Fitted(FewSyntaxes, false, false)
                .SequenceEqual(new[] { DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRLittleEndian }), null);

            Fresh();
            var report = await ExecuteFew("negotiation-probe,store-ct,store-ct-edge,store-basic-text-sr,retrieve-syntaxes");
            var result = Result(report, "retrieve-syntaxes");
            var ct = DicomUID.CTImageStorage;
            var sr = DicomUID.BasicTextSRStorage;
            _check("Delivered: PASS", result.Result == TestResultClass.PASS, Show(report) + "\n    " + Cells(result));
            _check("Delivered: one row per SOP class, so ct-edge adds none", result.RetrieveMatrix?.Rows.Count == 2, Cells(result));
            _check("Delivered: CT in the three syntaxes the mock accepts", CellOf(result, ct, DicomTransferSyntax.ImplicitVRLittleEndian) == DCST4.Core.Capability.CapabilityResult.Accepted
                && CellOf(result, ct, DicomTransferSyntax.ExplicitVRLittleEndian) == DCST4.Core.Capability.CapabilityResult.Accepted
                && CellOf(result, ct, DicomTransferSyntax.JPEGProcess14SV1) == DCST4.Core.Capability.CapabilityResult.Accepted, Cells(result));
            _check("Delivered: CT not in JPEG 2000, whose context was rejected", CellOf(result, ct, DicomTransferSyntax.JPEG2000Lossless) == DCST4.Core.Capability.CapabilityResult.TransferSyntaxNotSupported, Cells(result));
            _check("Delivered: no video cell for a single-frame CT, and only native ones for the SR", CellOf(result, ct, DicomTransferSyntax.MPEG2) == null
                && result.RetrieveMatrix.Rows.Single(r => r.SopClassUid == sr.UID).Cells.Count == 2, Cells(result));
            _check("Delivered: every cell has its association and raw status", result.RetrieveMatrix.AllCells().All(c => c.Item2.Association > 0 && c.Item2.Status == "0x0000"), Cells(result));
            _check("Delivered: the store matrix is untouched and the result records the C-GET status", result.CapabilityMatrix == null && result.StatusCodes.Any(s => s.Operation == "C-GET" && s.Code == "0x0000"), null);
            _check("Delivered: compared with the store matrix", result.Details["storedButNotDelivered"] == "0" && result.Details["deliveredButNotStored"] == "0", string.Join(", ", result.Details.Select(d => d.Key + "=" + d.Value)));
            _check("Delivered: one C-GET per cell, each a timeline association", result.RetrieveMatrix.Associations.Count == result.RetrieveMatrix.AllCells().Count() && result.Timeline.Select(e => e.Association).Distinct().Count() == result.RetrieveMatrix.Associations.Count, null);

            var html = DCST4.Core.Reporting.RunReportWriter.Render(report);
            _check("Report: a retrieve matrix section, linked from the top", html.Contains("id=\"retrieve-capability\"") && html.Contains("href=\"#retrieve-capability\"") && html.Contains("Against the capability matrix"), null);
            var files = ResultFileWriter.Write(report, Path.Combine(_temp, "retrieve-syntaxes"));
            _check("Result files: the retrieve matrix is written beside the store matrix", File.Exists(files.RetrieveCapabilityJson) && File.Exists(files.RetrieveCapabilityCsv)
                && files.RetrieveCapabilityCsv.EndsWith("-retrieve-capability.csv") && File.ReadAllText(files.RetrieveCapabilityCsv).Split('\n')[0].TrimEnd('\r').EndsWith(",dimse_status"), files.RetrieveCapabilityCsv);
            var loaded = DCST4.Core.Reporting.RunFile.Load(files.Json);
            _check("Result files: the retrieve matrix reads back", Result(loaded, "retrieve-syntaxes").RetrieveMatrix?.AllCells().Count() == result.RetrieveMatrix.AllCells().Count(), null);

            // The view model of the second grid.
            var view = new DCST4.ViewModels.CapabilityViewModel(isRetrieve: true);
            var changed = DCST4.Core.Capability.CapabilityMatrix.Load(files.RetrieveCapabilityJson);
            changed.Find(ct.UID, DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID).Result = DCST4.Core.Capability.CapabilityResult.NotDelivered;
            changed.Find(ct.UID, DicomTransferSyntax.JPEG2000Lossless.UID.UID).Result = DCST4.Core.Capability.CapabilityResult.Accepted;
            view.Show(changed);
            view.SetStoreReference(Result(report, "negotiation-probe").CapabilityMatrix);
            var ctRow = view.Rows.Single(r => r.SopClassUid == ct.UID);
            var column = view.Columns.ToList();
            Func<DicomTransferSyntax, string> code = s => ctRow.Cells[column.FindIndex(c => c.Name == s.UID.Name)].Code;
            _check("View: D for not delivered, marked v as accepted on store", code(DicomTransferSyntax.ExplicitVRLittleEndian) == "Dv", code(DicomTransferSyntax.ExplicitVRLittleEndian));
            _check("View: delivered but not accepted on store is marked ^", code(DicomTransferSyntax.JPEG2000Lossless) == "✓^", code(DicomTransferSyntax.JPEG2000Lossless));
            _check("View: a syntax the SR was not asked about is a blank cell, so the columns line up", view.Rows.Single(r => r.SopClassUid == sr.UID).Cells.Count == column.Count
                && view.Rows.Single(r => r.SopClassUid == sr.UID).Cells[column.FindIndex(c => c.Name == DicomTransferSyntax.JPEG2000Lossless.UID.Name)].Code == string.Empty, null);
            _check("View: the differences list and summary", view.Differences.Count == 2 && view.ComparisonSummary.Contains("1 accepted on store but not delivered, 1 delivered but not accepted on store"), view.ComparisonSummary);

            // The comparer: a syntax no longer delivered is a regression.
            var after = DCST4.Core.Reporting.RunFile.Load(files.Json);
            Result(after, "retrieve-syntaxes").RetrieveMatrix.Find(ct.UID, DicomTransferSyntax.JPEGProcess14SV1.UID.UID).Result = DCST4.Core.Capability.CapabilityResult.NotDelivered;
            var comparison = DCST4.Core.Reporting.RunComparer.Compare(new[] { new DCST4.Core.Reporting.NamedRun("a", loaded), new DCST4.Core.Reporting.NamedRun("b", after) });
            var findings = comparison.Runs[0].Findings;
            _check("Comparer: a syntax no longer delivered is a Retrieve regression", findings.Any(f => f.Kind == DCST4.Core.Reporting.FindingKind.Regression && f.Area == "Retrieve" && f.Subject == "CT Image Storage" && f.Value.StartsWith("no longer delivered")),
                string.Join(" | ", findings.Select(f => f.ToString())));

            Fresh();
            Scp.RetrieveSyntax = DicomTransferSyntax.ImplicitVRLittleEndian;
            var other = Result(await ExecuteFew("store-ct,retrieve-syntaxes"), "retrieve-syntaxes");
            _check("Another syntax accepted than proposed: FAIL, and the cell says so", other.Result == TestResultClass.FAIL
                && CellOf(other, ct, DicomTransferSyntax.ExplicitVRLittleEndian) == DCST4.Core.Capability.CapabilityResult.AcceptedOtherSyntax
                && CellOf(other, ct, DicomTransferSyntax.ImplicitVRLittleEndian) == DCST4.Core.Capability.CapabilityResult.Accepted, other.Explanation + "\n    " + Cells(other));

            Fresh();
            Scp.OnRetrieve = f => null;
            var nothing = Result(await ExecuteFew("store-ct,retrieve-syntaxes"), "retrieve-syntaxes");
            _check("Success, and nothing sent: FAIL, the cells are not delivered", nothing.Result == TestResultClass.FAIL && nothing.Explanation.Contains("irregular")
                && CellOf(nothing, ct, DicomTransferSyntax.ExplicitVRLittleEndian) == DCST4.Core.Capability.CapabilityResult.NotDelivered, nothing.Explanation + "\n    " + Cells(nothing));

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.StudyRootQueryRetrieveInformationModelGet, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            Scp.ContextTable.Set(DicomUID.PatientRootQueryRetrieveInformationModelGet, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var noGet = Result(await ExecuteFew("store-ct,retrieve-syntaxes"), "retrieve-syntaxes");
            _check("C-GET not accepted: NOT_SUPPORTED after one try per root", noGet.Result == TestResultClass.NOT_SUPPORTED && noGet.RetrieveMatrix?.Associations.Count == 2, noGet.Explanation);

            Fresh();
            var alone = Result(await ExecuteFew("retrieve-syntaxes"), "retrieve-syntaxes");
            _check("Without a store test: INDETERMINATE and says why", alone.Result == TestResultClass.INDETERMINATE && alone.RetrieveMatrix == null && alone.Explanation.Length > 0, alone.Explanation);
        }

        /// <summary>
        /// Breast Tomosynthesis was the one image type without a store test until 1.11.0, since its dataset is 700 MB by
        /// default. The test sends it at 128 x 128 with 8 frames.
        /// </summary>
        private static async Task TestTomosynthesis()
        {
            Console.WriteLine("\nChain: Breast Tomosynthesis");
            const string id = "store-breast-tomosynthesis";
            var test = TestCatalog.All.SingleOrDefault(t => t.Id == id);
            _check("The store test exists, after the other store tests and before the queries", test != null && TestCatalog.All.ToList().IndexOf(test) < TestCatalog.All.ToList().FindIndex(t => t.Id == "query-index")
                && TestCatalog.All.ToList().IndexOf(test) > TestCatalog.All.ToList().FindIndex(t => t.Id == "store-ct"), null);
            _check("It says how much it stores: one object of about 260 KB, not the 700 MB of the dataset", test?.Storage != null && test.Storage.Objects == 1 && test.Storage.Describe() == "1 examination, 1 object, about 258 KB", test?.Storage?.Describe());
            _check("It is named without the size, and runs with 'all'", test?.Name == "C-STORE: Breast Tomosynthesis" && TestCatalog.Select("all", out _).Any(t => t.Id == id), test?.Name);

            Fresh();
            var report = await Execute(id + ",query-index,retrieve-cget,retrieve-cmove,verify-integrity");
            _check("Stored, found, retrieved both ways and intact", report.Results.All(r => r.Result == TestResultClass.PASS), Show(report));
            var row = report.Chain?.SingleOrDefault();
            _check("A chain row of its own, named breast-tomosynthesis", row != null && row.ObjectType == "breast-tomosynthesis" && row.DisplayName == "Breast Tomosynthesis"
                && row.Accepted?.Result == TestResultClass.PASS && row.Indexed?.Result == TestResultClass.PASS && row.Retrievable?.Result == TestResultClass.PASS && row.Intact?.Result == TestResultClass.PASS, Show(report));
            var stored = Scp.Archive.SingleOrDefault()?.Dataset;
            _check("The mock got one Breast Tomosynthesis object of 128 x 128 with 8 frames", stored != null && stored.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == DicomUID.BreastTomosynthesisImageStorage
                && stored.GetSingleValue<int>(DicomTag.NumberOfFrames) == 8 && stored.GetSingleValue<ushort>(DicomTag.Rows) == 128, null);
            var instance = Set(report, "breast-tomosynthesis", "C-GET").Instances.Single();
            _check("What came back is byte-identical, and every frame matches the manifest", instance.Pixel == PixelVerdict.ByteIdentical && instance.FramesMatchManifest == true && instance.NormalizedHashMatchesManifest, instance.Pixel.ToString());

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.BreastTomosynthesisImageStorage, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var refused = await Execute(id);
            _check("A system without the SOP class: NOT_SUPPORTED, nothing sent", Result(refused, id).Result == TestResultClass.NOT_SUPPORTED && Scp.StoreCount == 0, Show(refused));
        }

        // ---------------------------------------------------------------- the XA and RF objects in the chain

        private static async Task TestXrayObjects()
        {
            Console.WriteLine("\nChain: XA and RF cine, Enhanced XA and Enhanced XRF");
            var ids = new[] { "store-xa-cine", "store-rf-cine", "store-enhanced-xa", "store-enhanced-rf" };
            var tests = ids.Select(id => TestCatalog.All.SingleOrDefault(t => t.Id == id)).ToList();
            _check("The four store tests exist, with the other store tests and in 'all'", tests.All(t => t != null && t.Storage != null && t.Storage.Objects == 1)
                && ids.All(id => TestCatalog.Select("all", out _).Any(t => t.Id == id)), string.Join(", ", tests.Select(t => t?.Name)));
            _check("They are named after their objects", string.Join(" | ", tests.Select(t => t?.Name)) == "C-STORE: XA Multi-frame | C-STORE: RF Multi-frame | C-STORE: Enhanced XA | C-STORE: Enhanced XRF",
                string.Join(" | ", tests.Select(t => t?.Name)));

            Fresh();
            var report = await Execute(string.Join(",", ids) + ",query-index,retrieve-cget,retrieve-cmove,verify-integrity");
            _check("All four stored, found, retrieved both ways and intact", report.Results.All(r => r.Result == TestResultClass.PASS), Show(report));
            _check("A chain row each, all four steps passed", report.Chain != null && report.Chain.Count == 4
                && report.Chain.All(r => r.Accepted?.Result == TestResultClass.PASS && r.Indexed?.Result == TestResultClass.PASS
                    && r.Retrievable?.Result == TestResultClass.PASS && r.Intact?.Result == TestResultClass.PASS), Show(report));

            var byClass = Scp.Archive.ToDictionary(a => a.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID), a => a.Dataset);
            _check("The mock got a cine XA and RF in the single-frame SOP classes, both multi-frame",
                byClass.Count == 4 && byClass[DicomUID.XRayAngiographicImageStorage].GetSingleValue<int>(DicomTag.NumberOfFrames) == 8
                && byClass[DicomUID.XRayAngiographicImageStorage].GetString(DicomTag.CineRate) == "15"
                && byClass[DicomUID.XRayRadiofluoroscopicImageStorage].GetString(DicomTag.CineRate) == "8", string.Join(", ", byClass.Keys.Select(k => k.Name)));
            _check("... and the two enhanced objects, with their functional groups",
                byClass.ContainsKey(DicomUID.EnhancedXAImageStorage) && byClass.ContainsKey(DicomUID.EnhancedXRFImageStorage)
                && byClass[DicomUID.EnhancedXAImageStorage].Contains(DicomTag.SharedFunctionalGroupsSequence)
                && byClass[DicomUID.EnhancedXRFImageStorage].GetString(DicomTag.PlaneIdentification) == "MONOPLANE", null);
            foreach (var pair in new[] { Tuple.Create("xa-cine", "XA Multi-frame"), Tuple.Create("enhanced-xa", "Enhanced XA") })
            {
                var instance = Set(report, pair.Item1, "C-GET").Instances.Single();
                _check($"{pair.Item2}: what came back is byte-identical, and every frame matches the manifest",
                    instance.Pixel == PixelVerdict.ByteIdentical && instance.FramesMatchManifest == true && instance.NormalizedHashMatchesManifest, instance.Pixel.ToString());
            }

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.EnhancedXAImageStorage, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var refused = await Execute("store-enhanced-xa,store-xa-cine");
            _check("A system without Enhanced XA: NOT_SUPPORTED for it, and the cine run still goes",
                Result(refused, "store-enhanced-xa").Result == TestResultClass.NOT_SUPPORTED && Result(refused, "store-xa-cine").Result == TestResultClass.PASS && Scp.StoreCount == 1, Show(refused));
        }

        private static async Task TestUsVolume()
        {
            Console.WriteLine("\nChain: Enhanced US Volume");
            var test = TestCatalog.All.SingleOrDefault(t => t.Id == "store-enhanced-us-volume");
            _check("The store test exists, is in 'all' and is named after its object", test != null && test.Storage != null && test.Storage.Objects == 1
                && test.Name == "C-STORE: Enhanced US Volume" && TestCatalog.Select("all", out _).Any(t => t.Id == test.Id), test?.Name);

            Fresh();
            var report = await Execute("store-enhanced-us-volume,query-index,retrieve-cget,verify-integrity");
            _check("Stored, found, retrieved and intact", report.Results.All(r => r.Result == TestResultClass.PASS), Show(report));
            var stored = Scp.Archive.Single().Dataset;
            _check("The mock got an Enhanced US Volume with its volume geometry", stored.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == DicomUID.EnhancedUSVolumeStorage
                && stored.GetSingleValue<int>(DicomTag.NumberOfFrames) == 8 && stored.GetString(DicomTag.UltrasoundAcquisitionGeometry) == "APEX"
                && stored.GetSequence(DicomTag.PerFrameFunctionalGroupsSequence).Items.All(i => i.Contains(DicomTag.PlanePositionVolumeSequence)), null);
            var instance = Set(report, "enhanced-us-volume", "C-GET").Instances.Single();
            _check("Enhanced US Volume: what came back is byte-identical, and every frame matches the manifest",
                instance.Pixel == PixelVerdict.ByteIdentical && instance.FramesMatchManifest == true && instance.NormalizedHashMatchesManifest, instance.Pixel.ToString());

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.EnhancedUSVolumeStorage, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var refused = await Execute("store-enhanced-us-volume,store-us-multiframe");
            _check("A system without Enhanced US Volume: NOT_SUPPORTED for it, and the cine loop still goes",
                Result(refused, "store-enhanced-us-volume").Result == TestResultClass.NOT_SUPPORTED && Result(refused, "store-us-multiframe").Result == TestResultClass.PASS && Scp.StoreCount == 1, Show(refused));
        }

        // ---------------------------------------------------------------- acceptance: the round trip

        private static async Task TestRoundTrip()
        {
            Console.WriteLine("\nChain: full round trip against the mock");
            Fresh();
            var report = await Execute(RoundTrip);

            _check("Round trip: every test passes", report.Results.All(r => r.Result == TestResultClass.PASS), Show(report));
            _check("Round trip: the mock stored them", Scp.Archive.Count == 2, Scp.Archive.Count.ToString());
            _check("Round trip: a chain row per object type", report.Chain != null && report.Chain.Select(c => c.ObjectType).SequenceEqual(new[] { "ct", "mr" }), null);

            foreach (var row in report.Chain ?? new List<ChainRow>())
            {
                _check($"Round trip: {row.ObjectType}: accepted, indexed, retrievable and intact all PASS",
                    row.Accepted?.Result == TestResultClass.PASS && row.Indexed?.Result == TestResultClass.PASS && row.Retrievable?.Result == TestResultClass.PASS && row.Intact?.Result == TestResultClass.PASS,
                    Show(report));
                _check($"Round trip: {row.ObjectType}: no commit test, so no committed step", row.Committed == null, null);
                _check($"Round trip: {row.ObjectType}: syntax is uncompressed", row.TransferSyntaxUid == DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID, row.TransferSyntaxUid);
            }

            foreach (var method in new[] { "C-GET", "C-MOVE" })
            {
                foreach (var type in new[] { "ct", "mr" })
                {
                    var set = Set(report, type, method);
                    _check($"Round trip: {type} by {method}: every level PASS", set.Levels.All(l => l.Result == TestResultClass.PASS), string.Join("; ", set.Levels.Select(l => l.Level + "=" + l.Result + " " + l.Explanation)));
                    var instance = set.Instances.Single();
                    _check($"Round trip: {type} by {method}: byte-identical pixels", instance.Pixel == PixelVerdict.ByteIdentical, instance.Pixel.ToString());
                    _check($"Round trip: {type} by {method}: the manifest's hashes hold", instance.FramesMatchManifest == true && instance.NormalizedHashMatchesManifest, null);
                    _check($"Round trip: {type} by {method}: no silent difference", instance.Differences.All(d => d.Class != AttributeChangeClass.SilentModification), string.Join(";", instance.Differences.Select(d => d.Path + " " + d.Class)));
                }
            }

            var store = Result(report, "store-ct");
            _check("Store test: raw status and context recorded", store.StatusCodes.Any(s => s.Code == "0x0000") && store.NegotiatedContexts.Count > 0, null);
            _check("Store test: UIDs under the profile's root", store.Uids["target.sopInstanceUid"].StartsWith("1.2.826.0.1.3680043.10.999."), store.Uids["target.sopInstanceUid"]);
            _check("Store test: says what chose the syntax", store.Details["transferSyntaxSelectedBy"] == "default", null);
            _check("Store test: the timeline holds the C-STORE", store.Timeline.Any(e => e.Type == "C-STORE-RQ") && store.Timeline.Any(e => e.Type == "C-STORE-RSP" && e.Status == "0x0000"), string.Join(",", store.Timeline.Select(e => e.Type)));
            var steps = store.Timeline.Select(e => e.Type).ToList();
            _check("Store test: the request comes after the association was accepted", steps.IndexOf("A-ASSOCIATE-AC") >= 0 && steps.IndexOf("A-ASSOCIATE-AC") < steps.IndexOf("C-STORE-RQ") && steps.IndexOf("C-STORE-RQ") < steps.IndexOf("C-STORE-RSP"), string.Join(",", steps));
            _check("Store test: a random DCST4 Patient ID by default", DCST4.Core.TestData.TestPatientIds.IsRandomId(Scp.Archive[0].PatientId), Scp.Archive[0].PatientId);
            _check("Report: the seed is written", report.Seed == 7, null);

            var json = ResultFileWriter.ToJson(report);
            _check("JSON: holds the chain", json.Contains("\"chain\"") && json.Contains("\"retrievable\"") && json.Contains("\"intact\"") && json.Contains("\"committed\": null"), null);
            _check("JSON: holds the integrity report", json.Contains("\"integrity\"") && json.Contains("ByteIdentical"), null);
            _check("JSON: schema version 8", TestRunReport.CurrentSchemaVersion == 8 && json.Contains("\"schemaVersion\": 8"), null);

            var folder = Path.Combine(_temp, "chain-files");
            var files = ResultFileWriter.Write(report, folder);
            _check("Files: a chain CSV next to the results", files.ChainCsv != null && File.ReadAllLines(files.ChainCsv).Length == 3, files.ChainCsv);
        }

        // ---------------------------------------------------------------- acceptance: each modification at its level

        private static async Task<IntegritySet> Modified(string store, string objectType, Func<DicomFile, DicomFile> script, string method = "C-GET")
        {
            Fresh();
            Scp.OnRetrieve = script;
            var report = await Execute(store + ",query-index,retrieve-cget,verify-integrity");
            return Set(report, objectType, method);
        }

        private static async Task TestModifications()
        {
            Console.WriteLine("\nIntegrity: each scripted change at its level");

            var changed = await Modified("store-ct", "ct", f => { f.Dataset.AddOrUpdate(DicomTag.PatientName, "Changed^Name"); return f; });
            var change = changed.Instances.Single().Differences.Single(d => d.Path.Contains("(0010,0010)"));
            _check("Changed attribute: Attributes FAIL", LevelOf(changed, IntegrityLevel.Attributes) == TestResultClass.FAIL, null);
            _check("Changed attribute: a silent modification, with both values", change.Class == AttributeChangeClass.SilentModification && change.Retrieved == "Changed^Name" && !string.IsNullOrEmpty(change.Original), null);
            _check("Changed attribute: the other levels hold", LevelOf(changed, IntegrityLevel.Transport) == TestResultClass.PASS && LevelOf(changed, IntegrityLevel.Identity) == TestResultClass.PASS && LevelOf(changed, IntegrityLevel.Pixel) == TestResultClass.PASS, null);

            var documented = await Modified("store-ct", "ct", f =>
            {
                var before = f.Dataset.GetSingleValue<string>(DicomTag.PatientName);
                f.Dataset.AddOrUpdate(DicomTag.PatientName, "Corrected^Name");
                var record = new DicomDataset(new DicomSequence(DicomTag.ModifiedAttributesSequence, new DicomDataset { { DicomTag.PatientName, before } }));
                f.Dataset.AddOrUpdate(new DicomSequence(DicomTag.OriginalAttributesSequence, record));
                return f;
            });
            _check("Documented change: a documented coercion", documented.Instances.Single().Differences.Any(d => d.Path.Contains("(0010,0010)") && d.Class == AttributeChangeClass.DocumentedCoercion), null);
            _check("Documented change: not a failure", LevelOf(documented, IntegrityLevel.Attributes) == TestResultClass.PASS, documented.Levels.First(l => l.Level == IntegrityLevel.Attributes).Explanation);

            var privateDropped = await Modified("store-ct-edge", "ct-edge", f =>
            {
                var gone = f.Dataset.Where(i => i.Tag.Group == 0x0099 && (i.Tag.Element & 0xFF) == 0x01).Select(i => i.Tag).ToArray();
                foreach (var tag in gone) f.Dataset.Remove(tag);
                return f;
            });
            var droppedInstance = privateDropped.Instances.Single();
            _check("Dropped private tag: reported separately", droppedInstance.PrivateDropped.Count == 1, string.Join(",", droppedInstance.PrivateDropped));
            _check("Dropped private tag: the others kept", droppedInstance.PrivateKept.Count > 0, null);
            _check("Dropped private tag: Attributes FAIL, as PrivateDropped", LevelOf(privateDropped, IntegrityLevel.Attributes) == TestResultClass.FAIL && droppedInstance.Differences.Any(d => d.Class == AttributeChangeClass.PrivateDropped), null);
            _check("Dropped private tag: the pixels are untouched", droppedInstance.Pixel == PixelVerdict.ByteIdentical, droppedInstance.Pixel.ToString());

            var charset = await Modified("store-ct", "ct", f =>
            {
                var name = f.Dataset.GetSingleValue<string>(DicomTag.PatientName);
                f.Dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                f.Dataset.AddOrUpdate(DicomTag.PatientName, name);
                return f;
            });
            var charsetInstance = charset.Instances.Single();
            _check("Character set change: reported", charsetInstance.CharacterSetChanged && charsetInstance.CharacterSetSent == "ISO_IR 100" && charsetInstance.CharacterSetReturned == "ISO_IR 192", charsetInstance.CharacterSetReturned);
            _check("Character set change: the text is still the same", charsetInstance.Differences.All(d => d.Class != AttributeChangeClass.SilentModification), string.Join(";", charsetInstance.Differences.Select(d => d.Path + " " + d.Original + " -> " + d.Retrieved)));
            _check("Character set change: named at Attributes", charset.Levels.First(l => l.Level == IntegrityLevel.Attributes).Explanation.Contains("another Specific Character Set"), charset.Levels.First(l => l.Level == IntegrityLevel.Attributes).Explanation);

            var pixels = await Modified("store-ct", "ct", f =>
            {
                var frame = DicomPixelData.Create(f.Dataset).GetFrame(0).Data;
                f.Dataset.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(Flip(frame))));
                return f;
            });
            var pixelInstance = pixels.Instances.Single();
            _check("Changed pixels: Pixel FAIL", LevelOf(pixels, IntegrityLevel.Pixel) == TestResultClass.FAIL, null);
            _check("Changed pixels: told apart from corruption", pixelInstance.Pixel == PixelVerdict.LossyChanged, pixelInstance.Pixel.ToString());
            _check("Changed pixels: maximum and mean absolute difference", pixelInstance.MaxAbsoluteDifference > 0 && pixelInstance.MeanAbsoluteDifference > 0 && pixelInstance.FramesMatchManifest == false, null);
            _check("Changed pixels: Attributes hold", LevelOf(pixels, IntegrityLevel.Attributes) == TestResultClass.PASS, null);

            var corrupt = await Modified("store-ct", "ct", f =>
            {
                var frame = DicomPixelData.Create(f.Dataset).GetFrame(0).Data;
                f.Dataset.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(frame.Take(frame.Length / 2).ToArray())));
                return f;
            });
            _check("Truncated pixel data: Corrupt", corrupt.Instances.Single().Pixel == PixelVerdict.Corrupt, corrupt.Instances.Single().Pixel.ToString());

            var seen = 0;
            var missing = await Modified("store-key-object-selection", "key-object-selection", f => Interlocked.Increment(ref seen) == 1 ? f : null);
            _check("Missing instance: Transport FAIL with the UID", LevelOf(missing, IntegrityLevel.Transport) == TestResultClass.FAIL && missing.Missing.Count == 1 && missing.ReturnedCount == 1 && missing.ExpectedCount == 2, null);
            _check("Missing instance: what did come back still compares", missing.Instances.Count == 1 && LevelOf(missing, IntegrityLevel.Pixel) == TestResultClass.PASS, null);

            var newSeries = await Modified("store-ct", "ct", f => { f.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, "1.2.3.4.5"); return f; });
            _check("Changed UID: Identity FAIL, Transport holds", LevelOf(newSeries, IntegrityLevel.Identity) == TestResultClass.FAIL && LevelOf(newSeries, IntegrityLevel.Transport) == TestResultClass.PASS, null);

            var truncated = await Modified("store-ct", "ct", f =>
            {
                var description = f.Dataset.GetSingleValue<string>(DicomTag.StudyDescription);
                f.Dataset.AddOrUpdate(DicomTag.StudyDescription, description.Substring(0, 3));
                return f;
            });
            _check("Truncated text: its own class", truncated.Instances.Single().Differences.Any(d => d.Class == AttributeChangeClass.Truncated && d.Path.Contains("(0008,1030)")) && LevelOf(truncated, IntegrityLevel.Attributes) == TestResultClass.FAIL, null);

            var added = await Modified("store-ct", "ct", f => { f.Dataset.AddOrUpdate(DicomTag.RetrieveAETitle, "MOCKSCP"); return f; });
            _check("Added attribute: listed, not a failure", added.Instances.Single().Differences.Any(d => d.Class == AttributeChangeClass.Added) && LevelOf(added, IntegrityLevel.Attributes) == TestResultClass.PASS, null);

            // ------------------------------------------------ transcoding
            Fresh();
            Scp.RetrieveSyntax = DicomTransferSyntax.ImplicitVRLittleEndian;
            var transcoded = await Execute("store-ct-edge,query-index,retrieve-cget,verify-integrity");
            var set = Set(transcoded, "ct-edge", "C-GET");
            var instance = set.Instances.Single();
            _check("Transcoded on retrieve: reported at Transfer Syntax", instance.TransferSyntaxReturned == DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID && instance.TransferSyntaxSent == DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID, instance.TransferSyntaxReturned);
            _check("Transcoded on retrieve: lossless-equivalent pixels", instance.Pixel == PixelVerdict.LosslessEquivalent, instance.Pixel.ToString());
            _check("Transcoded on retrieve: VR loss is not a change", instance.Differences.Any(d => d.Class == AttributeChangeClass.VrLost) && LevelOf(set, IntegrityLevel.Attributes) == TestResultClass.PASS, set.Levels.First(l => l.Level == IntegrityLevel.Attributes).Explanation + " / " + string.Join(";", instance.Differences.Select(d => d.Path + " " + d.Class)));
            _check("Transcoded on retrieve: a private sequence that cannot be read is marked", instance.Differences.Any(d => d.Class == AttributeChangeClass.NotCompared), null);
            _check("Transcoded on retrieve: what happened is explained", instance.TranscodingInference.Contains("another syntax"), instance.TranscodingInference);
        }

        // ---------------------------------------------------------------- acceptance: C-MOVE diagnostics

        private static async Task TestMoveDiagnosis(int closedPort)
        {
            Console.WriteLine("\nC-MOVE diagnosis");
            const string Tests = "store-ct,retrieve-cmove,retrieve-cmove-diagnostic";

            Fresh();
            var ok = await Execute(Tests);
            _check("Delivered: PASS", Result(ok, "retrieve-cmove-diagnostic").Result == TestResultClass.PASS && Result(ok, "retrieve-cmove-diagnostic").Details["diagnosis"] == "Delivered", Show(ok));

            Fresh();
            Scp.MoveDestinations.Clear();
            var unknown = await Execute(Tests);
            var unknownResult = Result(unknown, "retrieve-cmove-diagnostic");
            _check("Unknown AE: FAIL with the raw 0xA801", unknownResult.Result == TestResultClass.FAIL && unknownResult.StatusCodes.Any(s => s.Code == "0xA801"), Show(unknown));
            _check("Unknown AE: says what to do", unknownResult.Details["diagnosis"] == "DestinationUnknown" && unknownResult.Explanation.Contains("DCST4") && unknownResult.Explanation.Contains("does not know") && unknownResult.Explanation.Contains(ListenPort.ToString()), unknownResult.Explanation);
            _check("Unknown AE: the retrieve test does not blame the PACS", Result(unknown, "retrieve-cmove").Result == TestResultClass.INDETERMINATE, Result(unknown, "retrieve-cmove").Result.ToString());

            Fresh();
            Scp.MoveDestinations["DCST4"] = Tuple.Create("127.0.0.1", closedPort);
            var unreachable = await Execute(Tests);
            var unreachableResult = Result(unreachable, "retrieve-cmove-diagnostic");
            _check("Unreachable: FAIL", unreachableResult.Result == TestResultClass.FAIL && unreachableResult.Details["diagnosis"] == "DestinationUnreachable", Show(unreachable));
            _check("Unreachable: names the firewall and the port", unreachableResult.Explanation.Contains("Windows Defender Firewall") && unreachableResult.Explanation.Contains(ListenPort.ToString()) && unreachableResult.Explanation.Contains("DCST4"), unreachableResult.Explanation);
            _check("Unreachable: no association reached the listener", unreachableResult.Details["incomingAssociations"] == "0", null);
            _check("Unreachable: retrieve test is INDETERMINATE and says why", Result(unreachable, "retrieve-cmove").Result == TestResultClass.INDETERMINATE && Result(unreachable, "retrieve-cmove").Explanation.Contains("Firewall"), null);

            Fresh();
            Scp.MoveDestinations["DCST4"] = Tuple.Create("127.0.0.1", closedPort);
            var several = await Execute("store-ct,store-mr,store-cr,retrieve-cmove");
            var severalMove = Result(several, "retrieve-cmove");
            _check("Unreachable, three object types: only the first is tried", severalMove.Timeline.Count(e => e.Type == "C-MOVE-RQ") == 1 && severalMove.Details["notTried"] == "2", string.Join(",", severalMove.Timeline.Select(e => e.Type)));
            _check("Unreachable, three object types: all three are INDETERMINATE in the chain, and the others say they were not tried",
                several.Chain.Count == 3 && several.Chain.All(r => r.Retrievable.Result == TestResultClass.INDETERMINATE) && several.Chain.Skip(1).All(r => r.Retrievable.Explanation.Contains("Not tried")) && !several.Chain[0].Retrievable.Explanation.Contains("Not tried"), Show(several));

            Fresh();
            var allMoved = await Execute("store-ct,store-mr,store-cr,retrieve-cmove");
            _check("Reachable, three object types: every one is moved", Result(allMoved, "retrieve-cmove").Timeline.Count(e => e.Type == "C-MOVE-RQ") == 3 && !Result(allMoved, "retrieve-cmove").Details.ContainsKey("notTried") && Result(allMoved, "retrieve-cmove").Result == TestResultClass.PASS, Show(allMoved));

            using (DicomServerFactory.Create<MockStorageScp>(ListenPort))
            {
                Fresh();
                var occupied = await Execute(Tests);
                _check("Port in use: TOOL_ERROR, not the PACS's fault", Result(occupied, "retrieve-cmove-diagnostic").Result == TestResultClass.TOOL_ERROR && Result(occupied, "retrieve-cmove-diagnostic").Explanation.Contains(ListenPort.ToString()), Show(occupied));
            }

        }

        // ---------------------------------------------------------------- store as a test case

        private static async Task TestStoreMapping(int closedPort)
        {
            Console.WriteLine("\nC-STORE as test cases: mapping to result classes");

            Fresh();
            Scp.StatusFor = i => DicomStatus.StorageCoercionOfDataElements;
            var warning = await Execute("store-ct");
            _check("Warning status: PASS, and the code is kept", Result(warning, "store-ct").Result == TestResultClass.PASS && Result(warning, "store-ct").StatusCodes.Any(s => s.Code == "0xB000") && Result(warning, "store-ct").Explanation.Contains("warning"), Show(warning));

            Fresh();
            Scp.StatusFor = i => new DicomStatus(DicomStatus.StorageStorageOutOfResources, "Disk full");
            Scp.OffendingElement = new Scp.Tag { Value = DicomTag.PatientName };
            var refused = await Execute("store-ct");
            var refusedResult = Result(refused, "store-ct");
            _check("Failure status: FAIL with the raw code", refusedResult.Result == TestResultClass.FAIL && refusedResult.StatusCodes.Any(s => s.Code.StartsWith("0xA7")), Show(refused));
            _check("Failure status: comment and offending element in the explanation", refusedResult.Explanation.Contains("Disk full") && refusedResult.Explanation.Contains("(0010,0010)"), refusedResult.Explanation);

            Fresh();
            Scp.StatusFor = i => DicomStatus.SOPClassNotSupported;
            var unsupported = await Execute("store-ct");
            _check("0x0122 from the SCP: NOT_SUPPORTED", Result(unsupported, "store-ct").Result == TestResultClass.NOT_SUPPORTED && Result(unsupported, "store-ct").StatusCodes.Any(s => s.Code == "0x0122"), Show(unsupported));

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.CTImageStorage, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var noContext = await Execute("store-ct");
            _check("No accepted context: NOT_SUPPORTED, nothing sent", Result(noContext, "store-ct").Result == TestResultClass.NOT_SUPPORTED && Scp.StoreCount == 0, Show(noContext));
            _check("No accepted context: no made-up status", Result(noContext, "store-ct").StatusCodes.Count == 0, null);

            Fresh();
            Scp.RejectAssociation = true;
            var rejected = await Execute("store-ct");
            _check("Rejected association: FAIL", Result(rejected, "store-ct").Result == TestResultClass.FAIL && Result(rejected, "store-ct").Explanation.Contains("rejected"), Show(rejected));

            var closed = await Execute("store-ct", Profile(port: closedPort));
            _check("Closed port: FAIL or TIMEOUT", Result(closed, "store-ct").Result == TestResultClass.FAIL || Result(closed, "store-ct").Result == TestResultClass.TIMEOUT, Show(closed));

            Fresh();
            Scp.HangFromImage = 0;
            var hung = await Execute("store-ct", Profile(responseTimeout: 1));
            _check("No response: TIMEOUT", Result(hung, "store-ct").Result == TestResultClass.TIMEOUT, Show(hung));

            // Every profile can be generated and stored small. Their pixels and contexts differ, so this would otherwise be unproven.
            Fresh();
            var ids = string.Join(",", DatasetProfileCatalog.Default.Select(p => "store-" + p.Id));
            var all = await Execute(ids);
            _check("Every object type stores", all.Results.All(r => r.Result == TestResultClass.PASS), Show(all));
        }

        // ---------------------------------------------------------------- a selection without its prerequisites, and cancel

        private static async Task TestPartialAndCancel()
        {
            Console.WriteLine("\nPartial selections and cancel");

            foreach (var id in new[] { "query-index", "query-levels", "query-return-keys", "retrieve-cget", "retrieve-syntaxes", "retrieve-cmove", "retrieve-cmove-diagnostic", "verify-integrity" })
            {
                Fresh();
                var report = await Execute(id);
                _check($"{id} alone: INDETERMINATE and says why", report.Results.Single().Result == TestResultClass.INDETERMINATE && report.Results.Single().Explanation.Length > 0, Show(report));
                _check($"{id} alone: no chain written", report.Chain == null, null);
            }

            Fresh();
            Scp.ResponseDelayMs = 3000;
            using (var cancellation = new CancellationTokenSource(300))
            {
                var report = await Execute("store-ct,query-index", token: cancellation.Token);
                _check("Cancel: the running store is INDETERMINATE", Result(report, "store-ct").Result == TestResultClass.INDETERMINATE, Show(report));
                _check("Cancel: the run says so", report.Cancelled, null);
                _check("Cancel: the chain still shows what happened", report.Chain?.Single().Accepted.Result == TestResultClass.INDETERMINATE, null);
            }
        }

        // ---------------------------------------------------------------- query capability

        private static async Task TestQuery()
        {
            Console.WriteLine("\nQuery capability");
            const string Tests = "store-ct,query-index,query-levels,query-return-keys,query-matching";

            Fresh();
            var report = await Execute(Tests);
            _check("Query tests: all PASS", new[] { "query-index", "query-levels", "query-return-keys", "query-matching" }.All(id => Result(report, id).Result == TestResultClass.PASS), Show(report));

            var levels = Result(report, "query-levels").Query.Levels;
            _check("Levels: eight combinations", levels.Count == 8, levels.Count.ToString());
            _check("Levels: Patient Root works at every level", levels.Where(r => r.Root == "Patient").All(r => r.Result == TestResultClass.PASS && r.Matches == 1), string.Join(",", levels.Select(l => l.Root + "/" + l.Level + "=" + l.Result)));
            _check("Levels: Study Root at PATIENT is the expected refusal", levels.Single(r => r.Root == "Study" && r.Level == "PATIENT").Result == TestResultClass.EXPECTED_FAILURE, null);
            _check("Levels: Study Root works at the others", levels.Where(r => r.Root == "Study" && r.Level != "PATIENT").All(r => r.Result == TestResultClass.PASS), null);

            var keys = Result(report, "query-return-keys").Query.ReturnKeys;
            _check("Return keys: one row per requested key", keys.Count > 25 && keys.Any(k => k.Level == "IMAGE" && k.Keyword == "SOPInstanceUID"), keys.Count.ToString());
            _check("Return keys: required ones are populated", keys.Where(k => k.Required).All(k => k.State == ReturnKeyState.Populated), string.Join(",", keys.Where(k => k.Required && k.State != ReturnKeyState.Populated).Select(k => k.Level + " " + k.Keyword)));
            _check("Return keys: values compared with what was sent", keys.Single(k => k.Level == "STUDY" && k.Keyword == "StudyDescription").MatchesOriginal == true, null);
            _check("Return keys: a count has nothing to compare with", keys.Single(k => k.Level == "STUDY" && k.Keyword == "NumberOfStudyRelatedInstances").MatchesOriginal == null, null);

            var cases = Result(report, "query-matching").Query.Matching;
            _check("Matching: names, wildcards and dates are tried", cases.Any(c => c.Case.Contains("wildcard *")) && cases.Any(c => c.Case.Contains("wildcard ?")) && cases.Any(c => c.Case.Contains("date")), null);
            _check("Matching: query in both character sets finds both studies here", cases.Where(c => c.Case.StartsWith("exact name")).All(c => c.Matches == 2 && c.Note.Contains("across")), string.Join(";", cases.Select(c => c.Case + "=" + c.Matches)));
            _check("Matching: a range that misses finds nothing", cases.Where(c => c.Case.Contains("after the study") || c.Case.Contains("before the study")).All(c => c.Matches == 0 && c.Result == TestResultClass.PASS), null);
            _check("Matching: a range that hits finds both", cases.Single(c => c.Case == "date range around the study").Matches == 2, null);
            _check("Matching: case sensitivity is observed, not judged", cases.Single(c => c.Case.Contains("upper case")).Note.Contains("is case sensitive"), cases.Single(c => c.Case.Contains("upper case")).Note);
            _check("Matching: every identifier is scoped to one patient", Scp.Archive.Select(a => a.PatientId).Distinct().Count() == 2, string.Join(",", Scp.Archive.Select(a => a.PatientId + "|" + a.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty))));

            Fresh();
            Scp.QueryCaseInsensitive = true;
            var insensitive = await Execute("query-matching");
            _check("Matching: an insensitive system is told apart", Result(insensitive, "query-matching").Query.Matching.Single(c => c.Case.Contains("upper case")).Note.Contains("not case sensitive"), null);

            Fresh();
            Scp.DropReturnKeys.Add(DicomTag.StudyDate);
            var dropped = await Execute("store-ct,query-return-keys");
            _check("Return keys: a required key missing is FAIL and named", Result(dropped, "query-return-keys").Result == TestResultClass.FAIL && Result(dropped, "query-return-keys").Explanation.Contains("StudyDate"), Show(dropped));

            Fresh();
            Scp.HideFromQuery = _ => true;
            var hidden = await Execute("store-ct,query-index");
            _check("Not indexed: FAIL after the wait", Result(hidden, "query-index").Result == TestResultClass.FAIL && hidden.Chain.Single().Indexed.Result == TestResultClass.FAIL && Result(hidden, "query-index").Explanation.Contains("could not be found"), Show(hidden));

            Fresh();
            Scp.QueryStatus = DicomStatus.ProcessingFailure;
            var failing = await Execute("store-ct,query-index");
            _check("Query failure status: FAIL with the code", Result(failing, "query-index").Result == TestResultClass.FAIL && Result(failing, "query-index").StatusCodes.Any(s => s.Code == "0x0110"), Show(failing));

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.StudyRootQueryRetrieveInformationModelFind, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var noFind = await Execute("store-ct,query-index");
            _check("C-FIND not accepted: NOT_SUPPORTED, and the chain says so", Result(noFind, "query-index").Result == TestResultClass.NOT_SUPPORTED && noFind.Chain.Single().Indexed.Result == TestResultClass.NOT_SUPPORTED, Show(noFind));
        }

        // ---------------------------------------------------------------- duplicates and conflicts

        private static async Task TestDuplicates()
        {
            Console.WriteLine("\nDuplicates and conflicts");

            Fresh();
            var identical = await Execute("duplicate-identical");
            _check("Identical: accepted, one instance: PASS", Result(identical, "duplicate-identical").Result == TestResultClass.PASS && Result(identical, "duplicate-identical").Details["instancesFound"] == "1", Show(identical));

            Fresh();
            Scp.Duplicates = DuplicatePolicy.Reject;
            var refused = await Execute("duplicate-identical");
            _check("Identical: refused with 0x0111: EXPECTED_FAILURE", Result(refused, "duplicate-identical").Result == TestResultClass.EXPECTED_FAILURE, Show(refused));

            Fresh();
            Scp.Duplicates = DuplicatePolicy.Overwrite;
            var newest = await Execute("duplicate-modified");
            _check("Modified: the second version won", Result(newest, "duplicate-modified").Result == TestResultClass.PASS && Result(newest, "duplicate-modified").Details["attributeVersionKept"] == "second" && Result(newest, "duplicate-modified").Details["pixelVersionKept"] == "second", Show(newest));

            Fresh();
            Scp.Duplicates = DuplicatePolicy.KeepFirst;
            var oldest = await Execute("duplicate-modified");
            _check("Modified: the first version won", Result(oldest, "duplicate-modified").Result == TestResultClass.PASS && Result(oldest, "duplicate-modified").Details["attributeVersionKept"] == "first", Show(oldest));

            Fresh();
            Scp.Duplicates = DuplicatePolicy.Reject;
            var modifiedRefused = await Execute("duplicate-modified");
            _check("Modified: refused with 0x0111: EXPECTED_FAILURE", Result(modifiedRefused, "duplicate-modified").Result == TestResultClass.EXPECTED_FAILURE, Show(modifiedRefused));

            Fresh();
            Scp.Duplicates = DuplicatePolicy.Overwrite;
            Scp.OnRetrieve = f => { f.Dataset.AddOrUpdate(DicomTag.SeriesDescription, f.Dataset.GetSingleValue<string>(DicomTag.SeriesDescription).Replace(" MODIFIED", string.Empty)); return f; };
            var mixed = await Execute("duplicate-modified");
            _check("Modified: attributes of one and pixels of the other is a finding", Result(mixed, "duplicate-modified").Result == TestResultClass.FAIL && Result(mixed, "duplicate-modified").Details["finding"] == "mixed-versions", Show(mixed));

            Fresh();
            var kept = await Execute("conflict-demographics");
            var keptResult = Result(kept, "conflict-demographics");
            _check("Conflict: two patients kept in one study is a finding", keptResult.Result == TestResultClass.FAIL && keptResult.Details["finding"] == "two-patients-in-one-study", Show(kept));
            _check("Conflict: both patients have random IDs by default, and differ", keptResult.Details["firstPatientId"] != keptResult.Details["secondPatientId"]
                && DCST4.Core.TestData.TestPatientIds.IsRandomId(keptResult.Details["firstPatientId"])
                && DCST4.Core.TestData.TestPatientIds.IsRandomId(keptResult.Details["secondPatientId"]), keptResult.Details["firstPatientId"]);

            Fresh();
            var listed = await Execute("conflict-demographics", options: new TestRunOptions { PatientIds = DCST4.Core.TestData.TestPatientIdSource.TestPersonnummer });
            var listedResult = Result(listed, "conflict-demographics");
            _check("Conflict: with the personnummer chosen, both patients are from the list and differ", listedResult.Details["firstPatientId"] != listedResult.Details["secondPatientId"]
                && DCST4.Core.TestData.TestPersonnummerRegistry.Default.Contains(listedResult.Details["firstPatientId"])
                && DCST4.Core.TestData.TestPersonnummerRegistry.Default.Contains(listedResult.Details["secondPatientId"]), Show(listed));

            Fresh();
            Scp.Conflicts = ConflictPolicy.Reject;
            var rejected = await Execute("conflict-demographics");
            _check("Conflict: refused: EXPECTED_FAILURE", Result(rejected, "conflict-demographics").Result == TestResultClass.EXPECTED_FAILURE, Show(rejected));

            Fresh();
            Scp.Conflicts = ConflictPolicy.ReconcileToFirst;
            var reconciled = await Execute("conflict-demographics");
            _check("Conflict: reconciled to one patient: PASS", Result(reconciled, "conflict-demographics").Result == TestResultClass.PASS && Result(reconciled, "conflict-demographics").Details["patientIdsReturned"] == Result(reconciled, "conflict-demographics").Details["firstPatientId"], Show(reconciled));
        }

        // ---------------------------------------------------------------- seeds and syntax selection

        private static async Task TestDeterminismAndSelection()
        {
            Console.WriteLine("\nSeeds and transfer syntax selection");

            Fresh();
            var first = await Execute(RoundTrip);
            Fresh();
            var second = await Execute(RoundTrip);
            var firstJson = TestSuiteTests.NormalizeJson(ResultFileWriter.ToJson(first));
            var secondJson = TestSuiteTests.NormalizeJson(ResultFileWriter.ToJson(second));
            _check("Same seed: the same result, apart from times", firstJson == secondJson, TestSuiteTests.FirstDifference(firstJson, secondJson));

            Fresh();
            var other = await Execute("store-ct", options: new TestRunOptions { Seed = 8 });
            _check("Another seed: other UIDs", Result(other, "store-ct").Uids["target.sopInstanceUid"] != Result(first, "store-ct").Uids["target.sopInstanceUid"], null);

            Fresh();
            var drawn = await new TestRunner().RunAsync(Profile(), TestCatalog.Select("store-ct", out _), CancellationToken.None);
            _check("No seed given: one is drawn and written", drawn.Seed.HasValue, null);

            Fresh();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.CTImageStorage, DicomTransferSyntax.ExplicitVRLittleEndian, ScpAnswer.Reject(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported));
            var byMatrix = await Execute("negotiation-probe,store-ct");
            var matrixResult = Result(byMatrix, "store-ct");
            _check("Matrix: the accepted syntax is used", matrixResult.Details["transferSyntaxSelectedBy"] == "capability-matrix" && byMatrix.Chain.Single().TransferSyntaxUid == DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID, Show(byMatrix));
            _check("Matrix: and the store passes", matrixResult.Result == TestResultClass.PASS, matrixResult.Explanation);

            Fresh();
            var bySetting = await Execute("store-ct,store-basic-text-sr", options: new TestRunOptions { SyntaxOverride = DicomTransferSyntax.ImplicitVRLittleEndian });
            _check("Choice: applied per object type", Result(bySetting, "store-ct").Details["transferSyntaxSelectedBy"] == "user-choice" && bySetting.Chain.First().TransferSyntaxUid == DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID, Show(bySetting));
            _check("Choice: also where it fits an object without pixels", Result(bySetting, "store-basic-text-sr").Result == TestResultClass.PASS, Show(bySetting));

            Fresh();
            var compressed = await Execute("store-basic-text-sr", options: new TestRunOptions { SyntaxOverride = DicomTransferSyntax.JPEGProcess14SV1 });
            _check("Choice: a compressed syntax on an object without pixels falls back and does not crash", Result(compressed, "store-basic-text-sr").Result == TestResultClass.PASS && Result(compressed, "store-basic-text-sr").Details["transferSyntaxSelectedBy"] == "default", Show(compressed));

            var cliFolder = Path.Combine(_temp, "cli-chain");
            Directory.CreateDirectory(cliFolder);
            var profilePath = Path.Combine(cliFolder, "profile.json");
            Profile().Save(profilePath);
            Fresh();
            var cli = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "store-ct,store-ct-edge,query-index,retrieve-cget,verify-integrity", "--out", cliFolder, "--log-dir", Path.Combine(cliFolder, "logs"), "--seed", "11", "--transfer-syntax", "implicit", "--index-wait", "1");
            _check("Command line: runs the chain", cli.ExitCode == 0, cli.Output);
            _check("Command line: names the chain file", cli.Output.Contains("Chain:") && cli.Output.Contains("-chain.csv"), cli.Output);
            _check("Command line: the choice of syntax is used", Directory.GetFiles(cliFolder, "*.json").Select(f => File.ReadAllText(f, Encoding.UTF8)).Any(t => t.Contains("\"user-choice\"") && t.Contains("\"seed\": 11")), null);
            _check("Command line: originals and retrieved files are kept under --out", Directory.GetDirectories(cliFolder, "work-*").Length == 1, null);

            var badSeed = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", cliFolder, "--seed", "soon");
            _check("Command line: an unusable --seed: exit code 2", badSeed.ExitCode == 2, badSeed.Output);
            var badMatrix = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", cliFolder, "--log-dir", Path.Combine(cliFolder, "logs"), "--matrix", Path.Combine(cliFolder, "nothing.json"));
            _check("Command line: an unusable --matrix: exit code 2", badMatrix.ExitCode == 2, badMatrix.Output);

            var work = Path.Combine(_temp, "kept-work");
            Fresh();
            await Execute(RoundTrip, options: new TestRunOptions { WorkFolder = work });
            _check("Work folder given: the originals and retrieved files stay", Directory.GetFiles(work, "*", SearchOption.AllDirectories).Length > 4, null);

            var before = Directory.GetDirectories(Path.GetTempPath(), "DCST4-*").Length;
            Fresh();
            await Execute("store-ct");
            _check("No work folder: the temporary one is removed", Directory.GetDirectories(Path.GetTempPath(), "DCST4-*").Length == before, null);
        }
    }
}
