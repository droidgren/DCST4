using DCST4.Core;
using DCST4.Core.Capability;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using DCST4.Core.Testing;
using DCST4.Core.Testing.Cases;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>The negotiation probe, the capability matrix, claimed vs actual and the negative negotiation tests.</summary>
    internal static class NegotiationTests
    {
        private const string Implicit = "1.2.840.10008.1.2";
        private const string Explicit = "1.2.840.10008.1.2.1";

        private static readonly CapabilityResult[] ScriptedResults =
        {
            CapabilityResult.Accepted,
            CapabilityResult.AbstractSyntaxNotSupported,
            CapabilityResult.TransferSyntaxNotSupported,
            CapabilityResult.UserRejection,
            CapabilityResult.NoReason,
        };

        /// <summary>Syntaxes fo-dicom 5.2.6 knows only as a UID: JPEG XL, Encapsulated Uncompressed and Deflated Image Frame Compression.</summary>
        private static readonly DicomTransferSyntax[] ProbeOnlyByUid =
        {
            DicomTransferSyntax.Parse("1.2.840.10008.1.2.4.110"), DicomTransferSyntax.Parse("1.2.840.10008.1.2.4.111"), DicomTransferSyntax.Parse("1.2.840.10008.1.2.4.112"),
            DicomTransferSyntax.Parse("1.2.840.10008.1.2.1.98"), DicomTransferSyntax.Parse("1.2.840.10008.1.2.8.1"),
        };

        private static TargetProfile Profile(int port) => TestSuiteTests.MockProfile(port);

        private static ProbeOptions MatrixOnly(IReadOnlyList<DicomUID> sopClasses = null, IReadOnlyList<DicomTransferSyntax> syntaxes = null, int limit = ProbeOptions.ContextLimit) =>
            new ProbeOptions { SopClasses = sopClasses, TransferSyntaxes = syntaxes, MaxContextsPerAssociation = limit, NegotiationFeatures = false };

        private static CapabilityResult ScriptedFor(DicomUID sop, DicomTransferSyntax syntax) =>
            ScriptedResults[(sop.UID.Sum(c => c) * 31 + syntax.UID.UID.Sum(c => c)) % ScriptedResults.Length];

        private static ScpAnswer ToAnswer(CapabilityResult result)
        {
            switch (result)
            {
                case CapabilityResult.Accepted: return ScpAnswer.Accept;
                case CapabilityResult.AbstractSyntaxNotSupported: return ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
                case CapabilityResult.TransferSyntaxNotSupported: return ScpAnswer.Reject(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported);
                case CapabilityResult.UserRejection: return ScpAnswer.Reject(DicomPresentationContextResult.RejectUser);
                case CapabilityResult.NoReason: return ScpAnswer.Reject(DicomPresentationContextResult.RejectNoReason);
                default: throw new ArgumentOutOfRangeException(nameof(result));
            }
        }

        private static void ScriptTable(ProbePlan plan)
        {
            var table = new ScpContextTable();
            foreach (var sop in plan.SopClasses)
            {
                foreach (var syntax in plan.TransferSyntaxes) table.Set(sop, syntax, ToAnswer(ScriptedFor(sop, syntax)));
            }
            Scp.ContextTable = table;
        }

        private static List<string> MatrixDifferences(CapabilityMatrix matrix, ProbePlan plan)
        {
            var differences = new List<string>();
            foreach (var sop in plan.SopClasses)
            {
                foreach (var syntax in plan.TransferSyntaxes)
                {
                    var cell = matrix.Find(sop.UID, syntax.UID.UID);
                    var expected = ScriptedFor(sop, syntax);
                    if (cell == null) differences.Add($"{sop.Name} × {syntax.UID.Name}: missing");
                    else if (cell.Result != expected) differences.Add($"{sop.Name} × {syntax.UID.Name}: {cell.Result}, expected {expected}");
                    else if (expected == CapabilityResult.Accepted && cell.AcceptedTransferSyntaxUid != syntax.UID.UID) differences.Add($"{sop.Name} × {syntax.UID.Name}: accepted as {cell.AcceptedTransferSyntaxUid}");
                    else if (cell.Association == null || cell.PresentationContextId == null) differences.Add($"{sop.Name} × {syntax.UID.Name}: no association or context ID");
                }
            }
            return differences;
        }

        /// <summary>What reached the SCP is exactly the plan: one syntax per context, and nothing fo-dicom added itself.</summary>
        private static List<string> WireProblems(ProbePlan plan)
        {
            var problems = new List<string>();
            var associations = Scp.Associations.ToList();
            if (associations.Count != plan.Associations.Count) problems.Add($"{associations.Count} associations, planned {plan.Associations.Count}");

            foreach (var received in associations)
            {
                if (received.Contexts.Count > ProbeOptions.ContextLimit) problems.Add($"association {received.Number}: {received.Contexts.Count} contexts");
                problems.AddRange(received.Contexts.Where(c => c.TransferSyntaxUids.Count != 1).Select(c => $"association {received.Number}: context {c.Id} holds {c.TransferSyntaxUids.Count} syntaxes"));

                var opening = received.Contexts.First();
                if (opening.Id != 1 || opening.AbstractSyntaxUid != DicomUID.Verification.UID || opening.TransferSyntaxUids.Single() != Implicit) problems.Add($"association {received.Number}: the first context is {opening.AbstractSyntaxUid}");

                var planned = plan.Associations.Single(a => a.Index == received.Number);
                var expected = new[] { DicomUID.Verification.UID + "|" + Implicit }.Concat(planned.Contexts.Select(c => c.SopClass.UID + "|" + c.Syntax.UID.UID)).ToList();
                var actual = received.Contexts.Select(c => c.AbstractSyntaxUid + "|" + c.TransferSyntaxUids.Single()).ToList();
                if (!expected.SequenceEqual(actual)) problems.Add($"association {received.Number}: proposed something else than planned");
            }

            var matrixCells = associations.Where(a => !plan.Associations.Single(p => p.Index == a.Number).IsFeatureProbe)
                .SelectMany(a => a.Contexts.Skip(1)).Select(c => c.AbstractSyntaxUid + "|" + c.TransferSyntaxUids.Single()).ToList();
            var cells = plan.SopClasses.SelectMany(s => plan.TransferSyntaxes.Select(t => s.UID + "|" + t.UID.UID)).ToList();
            var echoCell = DicomUID.Verification.UID + "|" + Implicit;
            if (matrixCells.Count != matrixCells.Distinct().Count()) problems.Add("a combination was proposed twice");
            if (!new HashSet<string>(matrixCells).SetEquals(cells.Where(c => c != echoCell))) problems.Add("the proposed combinations are not the planned ones");
            return problems;
        }

        private static async Task<TestResult> RunCase(ITestCase test, TargetProfile profile, CancellationToken token = default(CancellationToken)) =>
            (await new TestRunner().RunAsync(profile, new[] { test }, token)).Results.Single();

        public static async Task Run(int port, int closedPort, string temp, Func<string[]> logLines, Action<string, bool, string> check)
        {
            TestCatalogAndPlan(check);
            await TestMatrixAgainstScript(port, check);
            await TestBoundaries(port, check);
            await TestContextAnswers(port, check);
            await TestAssociationFailures(port, closedPort, logLines, check);
            await TestNegativeCases(port, check);
            await TestFiles(port, temp, check);
            TestComparison(temp, check);
            Scp.Reset();
        }

        // ---------------------------------------------------------------- catalog and plan

        private static void TestCatalogAndPlan(Action<string, bool, string> check)
        {
            Console.WriteLine("\nNegotiation probe: catalog and plan");

            var storage = DatasetProfileCatalog.All.Select(p => p.IsImage ? p.Image.SopClass : p.Object.SopClass).Distinct().ToList();
            check("Storage SOP classes come from the dataset profiles", storage.All(CapabilityCatalog.SopClasses.Contains) && CapabilityCatalog.StorageSopClasses.Count == storage.Count, CapabilityCatalog.StorageSopClasses.Count.ToString());
            check("Every generated modality is in the plan", DCST4.Core.Generation.Modalities.ModalityCatalog.All.All(m => CapabilityCatalog.SopClasses.Contains(m.SopClass)), null);
            check("A profile that must be asked for by name is covered too", CapabilityCatalog.SopClasses.Contains(DicomUID.BreastTomosynthesisImageStorage), null);

            foreach (var service in new[] { DicomUID.Verification, DicomUID.StorageCommitmentPushModel, DicomUID.StudyRootQueryRetrieveInformationModelFind, DicomUID.StudyRootQueryRetrieveInformationModelMove, DicomUID.StudyRootQueryRetrieveInformationModelGet, DicomUID.PatientRootQueryRetrieveInformationModelFind, DicomUID.PatientRootQueryRetrieveInformationModelMove, DicomUID.PatientRootQueryRetrieveInformationModelGet })
            {
                check("Service SOP class in the plan: " + service.Name, CapabilityCatalog.SopClasses.Contains(service), null);
            }

            var required = new[]
            {
                DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRBigEndian, DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
                DicomTransferSyntax.JPEGProcess1, DicomTransferSyntax.JPEGProcess14SV1, DicomTransferSyntax.JPEGLSLossless, DicomTransferSyntax.JPEGLSNearLossless,
                DicomTransferSyntax.JPEG2000Lossless, DicomTransferSyntax.JPEG2000Lossy, DicomTransferSyntax.RLELossless, DicomTransferSyntax.HTJ2KLossless, DicomTransferSyntax.HTJ2KLosslessRPCL, DicomTransferSyntax.HTJ2K,
                DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41, DicomTransferSyntax.MPEG4AVCH264BDCompatibleHighProfileLevel41, DicomTransferSyntax.MPEG4AVCH264HighProfileLevel42For2DVideo,
                DicomTransferSyntax.MPEG4AVCH264HighProfileLevel42For3DVideo, DicomTransferSyntax.MPEG4AVCH264StereoHighProfileLevel42,
                DicomTransferSyntax.JPEGProcess2_4, DicomTransferSyntax.JPEGProcess14, DicomTransferSyntax.MPEG2, DicomTransferSyntax.MPEG2MainProfileHighLevel,
                DicomTransferSyntax.HEVCH265MainProfileLevel51, DicomTransferSyntax.HEVCH265Main10ProfileLevel51,
            }.Concat(ProbeOnlyByUid).ToList();
            check("Every required transfer syntax is in the plan", required.All(s => CapabilityCatalog.TransferSyntaxes.Any(p => p.Syntax == s)), string.Join(", ", required.Where(s => CapabilityCatalog.TransferSyntaxes.All(p => p.Syntax != s)).Select(s => s.UID.UID)));
            check("JPEG XL is proposed in all three variants", new[] { "1.2.840.10008.1.2.4.110", "1.2.840.10008.1.2.4.111", "1.2.840.10008.1.2.4.112" }.All(uid => CapabilityCatalog.TransferSyntaxes.Any(p => p.Syntax.UID.UID == uid)), null);
            check("Every proposed syntax has its dictionary name", CapabilityCatalog.TransferSyntaxes.All(s => CapabilityMatrix.DisplayName(s.Syntax.UID.UID) != s.Syntax.UID.UID), string.Join(", ", CapabilityCatalog.TransferSyntaxes.Where(s => CapabilityMatrix.DisplayName(s.Syntax.UID.UID) == s.Syntax.UID.UID).Select(s => s.Syntax.UID.UID)));
            check("A syntax looked up by UID equals a fresh lookup", ProbeOnlyByUid.All(s => s == DicomTransferSyntax.Parse(s.UID.UID) && s.IsExplicitVR && s.IsEncapsulated), null);
            // JPEG XL Lossless, JPEG XL and H.264 High Profile / Level 4.1 have encoders of DCST4's own and are no longer probe-only.
            var generated = new[] { "1.2.840.10008.1.2.4.110", "1.2.840.10008.1.2.4.112", DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41.UID.UID };
            var probeOnly = ProbeOnlyByUid.Where(s => !generated.Contains(s.UID.UID)).Concat(new[]
            {
                DicomTransferSyntax.MPEG2, DicomTransferSyntax.MPEG2MainProfileHighLevel, DicomTransferSyntax.HEVCH265MainProfileLevel51, DicomTransferSyntax.HEVCH265Main10ProfileLevel51,
                DicomTransferSyntax.MPEG4AVCH264BDCompatibleHighProfileLevel41, DicomTransferSyntax.MPEG4AVCH264HighProfileLevel42For2DVideo,
            }).ToList();
            check("Probe-only syntaxes can not be chosen to generate or send in", probeOnly.All(s => CompressionCatalog.All.All(c => c.Syntax != s) && CompressionCatalog.Find(s.UID.UID) == null), null);
            check("JPEG XL Lossless, JPEG XL and H.264 Level 4.1 can be generated, and are still probed", generated.All(uid => CompressionCatalog.Find(uid) != null && CapabilityCatalog.TransferSyntaxes.Any(p => p.Syntax.UID.UID == uid)), null);
            check("Column labels are short and unique", CapabilityCatalog.TransferSyntaxes.All(s => s.ShortName.Length <= 11) && CapabilityCatalog.TransferSyntaxes.Select(s => s.ShortName).Distinct().Count() == CapabilityCatalog.TransferSyntaxes.Count, null);

            var plan = ProbePlan.Create(null);
            var matrixPlans = plan.Associations.Where(a => !a.IsFeatureProbe).ToList();
            check("Default plan: several associations", matrixPlans.Count > 1, matrixPlans.Count.ToString());
            check("Default plan: within the limit, counting the C-ECHO context", plan.Associations.All(a => a.Contexts.Count + 1 <= ProbeOptions.ContextLimit), null);
            check("Default plan: every context but the C-ECHO's is proposed once", matrixPlans.Sum(a => a.Contexts.Count) == plan.SopClasses.Count * plan.TransferSyntaxes.Count - 1, null);
            check("Default plan: role selection only on storage classes", plan.Associations.SelectMany(a => a.Contexts).Where(c => c.ProposeScpRole).All(c => c.SopClass.StorageCategory != DicomStorageCategory.None), null);
            check("Default plan: at most one role proposal per SOP class in an association", plan.Associations.All(a => a.Contexts.Where(c => c.ProposeScpRole).Select(c => c.SopClass).Distinct().Count() == a.Contexts.Count(c => c.ProposeScpRole)), null);

            var duplicated = ProbePlan.Create(MatrixOnly(new[] { DicomUID.CTImageStorage, DicomUID.CTImageStorage }, new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRLittleEndian }));
            check("Duplicates in the options are removed", duplicated.SopClasses.Count == 1 && duplicated.TransferSyntaxes.Count == 1 && duplicated.Associations.Single().Contexts.Count == 1, null);

            Func<Action, bool> throws = action => { try { action(); return false; } catch (ArgumentException) { return true; } };
            check("A limit above 128 is refused", throws(() => ProbePlan.Create(MatrixOnly(limit: 129))), null);
            check("A limit of 1 leaves no room and is refused", throws(() => ProbePlan.Create(MatrixOnly(limit: 1))), null);
            check("An empty list is refused", throws(() => ProbePlan.Create(MatrixOnly(new DicomUID[0]))), null);
        }

        // ---------------------------------------------------------------- the matrix against a scripted mock

        private static async Task TestMatrixAgainstScript(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nNegotiation probe: matrix against a scripted SCP");

            var smallOptions = MatrixOnly(new[] { DicomUID.CTImageStorage, DicomUID.MRImageStorage, DicomUID.Verification }, new[] { DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.JPEG2000Lossless });
            await CheckScripted("Small plan", smallOptions, 1, port, check);
            // 127 contexts per association besides the C-ECHO's, which also answers its own cell: 8 with 31 SOP classes and 30 syntaxes.
            var defaultContexts = CapabilityCatalog.SopClasses.Count * CapabilityCatalog.TransferSyntaxes.Count - 1;
            await CheckScripted("Default SOP classes and syntaxes", MatrixOnly(), (defaultContexts + ProbeOptions.ContextLimit - 2) / (ProbeOptions.ContextLimit - 1), port, check);
            await CheckScripted("A low limit, many associations", MatrixOnly(CapabilityCatalog.StorageSopClasses.Take(4).ToList(), CapabilityCatalog.TransferSyntaxes.Take(6).Select(s => s.Syntax).ToList(), 5), 6, port, check);

            // ------------------------------------------------ with the extras, as the test case runs it
            Scp.Reset();
            var fullPlan = ProbePlan.Create(null);
            ScriptTable(fullPlan);
            var full = await RunCase(new NegotiationProbeTest(), Profile(port));
            check("Test case: every cell matches the script", MatrixDifferences(full.CapabilityMatrix, fullPlan).Count == 0, string.Join("\n", MatrixDifferences(full.CapabilityMatrix, fullPlan).Take(20)));
            check("Test case: the wire held exactly the plan", WireProblems(fullPlan).Count == 0, string.Join("\n", WireProblems(fullPlan).Take(20)));
            check("Test case: PASS, released", full.Result == TestResultClass.PASS && full.Explanation.Contains("released"), full.Explanation);
            check("Test case: each association released, and the mock saw it", Scp.Associations.All(a => a.Released && !a.AbortedByPeer) && full.CapabilityMatrix.Associations.All(a => a.Ending == AssociationEnding.Released), null);
            check("Test case: no image or anything else was stored", Scp.StoreCount == 0 && Scp.Received.IsEmpty, Scp.StoreCount.ToString());
            check("Test case: the timeline shows every release", full.Timeline.Count(e => e.Type == "A-RELEASE-RP") == fullPlan.Associations.Count, full.Timeline.Count(e => e.Type == "A-RELEASE-RP").ToString());
            check("Test case: timeline events name their association and are in order", full.Timeline.All(e => e.Association.HasValue) && full.Timeline.Zip(full.Timeline.Skip(1), (a, b) => a.OffsetMs <= b.OffsetMs).All(x => x) && full.Timeline.Select(e => e.Index).SequenceEqual(Enumerable.Range(0, full.Timeline.Count)), null);
            check("Test case: every proposed context is recorded with what was proposed", full.NegotiatedContexts.All(c => c.ProposedTransferSyntaxUids.Count == 1) && full.NegotiatedContexts.Count == Scp.Associations.Sum(a => a.Contexts.Count), full.NegotiatedContexts.Count.ToString());
            check("Test case: raw C-ECHO status recorded", full.StatusCodes.All(s => s.Operation == "C-ECHO" && s.Code == "0x0000") && full.StatusCodes.Count > 0, null);
            check("Test case: remote identity", full.Uids["remoteImplementationClassUid"] == DicomImplementation.ClassUID.UID && full.Details["remoteImplementationVersionName"] == DicomImplementation.Version, full.Uids["remoteImplementationClassUid"]);

            Scp.Reset();
            ScriptTable(fullPlan);
            var cancelled = default(TestResult);
            using (var cancellation = new CancellationTokenSource())
            {
                Scp.EchoDelayMs = 2000;
                var running = RunCase(new NegotiationProbeTest(), Profile(port), cancellation.Token);
                while (Scp.EchoCount == 0) await Task.Delay(10);
                cancellation.Cancel();
                cancelled = await running;
            }
            check("Cancel: INDETERMINATE, with a partial matrix", cancelled.Result == TestResultClass.INDETERMINATE && cancelled.CapabilityMatrix.AllCells().Any(c => c.Item2.Result == CapabilityResult.NotProbed), cancelled.Result.ToString());
            check("Cancel: what the SCP answered is kept", cancelled.CapabilityMatrix.AllCells().Any(c => CapabilityMatrix.WasAnswered(c.Item2.Result)), null);
            check("Cancel: no further association was opened", Scp.AssociationCount == 1, Scp.AssociationCount.ToString());
        }

        private static async Task CheckScripted(string name, ProbeOptions options, int associations, int port, Action<string, bool, string> check)
        {
            Scp.Reset();
            var plan = ProbePlan.Create(options);
            ScriptTable(plan);
            var outcome = await NegotiationProbe.RunAsync(Profile(port), options, CancellationToken.None);

            var differences = MatrixDifferences(outcome.Matrix, plan);
            var wire = WireProblems(plan);
            check(name + ": every cell matches the script", differences.Count == 0, string.Join("\n", differences.Take(20)));
            check(name + ": number of associations", Scp.AssociationCount == associations && plan.Associations.Count == associations, Scp.AssociationCount.ToString());
            check(name + ": on the wire, one syntax per context and nothing added", wire.Count == 0, string.Join("\n", wire.Take(20)));
            check(name + ": every association released", Scp.Associations.All(a => a.Released) && outcome.Matrix.Associations.All(a => a.Ending == AssociationEnding.Released), null);
        }

        private static async Task TestBoundaries(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nNegotiation probe: the limit of 128 contexts");

            var syntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian };
            var storage = Enumerable.Range(0, 200).Select(i => DicomUID.Parse("1.2.826.0.1.3680043.8.498.77." + (i + 1).ToString(CultureInfo.InvariantCulture))).ToList();

            Func<int, IReadOnlyList<DicomTransferSyntax>, IReadOnlyList<DicomUID>, Task<int>> run = async (count, syntaxList, sops) =>
            {
                Scp.Reset();
                Scp.ContextTable = new ScpContextTable();
                var options = MatrixOnly(sops, syntaxList);
                var outcome = await NegotiationProbe.RunAsync(Profile(port), options, CancellationToken.None);
                check($"{count} cells: all accepted, none lost", outcome.Matrix.AllCells().All(c => c.Item2.Result == CapabilityResult.Accepted), null);
                check($"{count} cells: no association over the limit", Scp.Associations.All(a => a.Contexts.Count <= ProbeOptions.ContextLimit), string.Join(",", Scp.Associations.Select(a => a.Contexts.Count)));
                return Scp.AssociationCount;
            };

            check("127 cells without the C-ECHO's: one association", await run(127, syntaxes, storage.Take(127).ToList()) == 1, Scp.AssociationCount.ToString());
            check("128 cells without the C-ECHO's: two associations", await run(128, syntaxes, storage.Take(128).ToList()) == 2, Scp.AssociationCount.ToString());
            var filling = Scp.Associations.Select(a => a.Contexts.Count).ToList();
            check("128 cells: the first association is full at 128", filling.First() == 128 && filling.Last() == 2, string.Join(",", filling));

            var withEcho = storage.Take(127).Concat(new[] { DicomUID.Verification }).ToList();
            check("128 cells with the C-ECHO's own: one association of 128", await run(128, new[] { DicomTransferSyntax.ImplicitVRLittleEndian }, withEcho) == 1 && Scp.Associations.Single().Contexts.Count == 128, Scp.AssociationCount.ToString());
            check("129 cells with the C-ECHO's own: two associations", await run(129, new[] { DicomTransferSyntax.ImplicitVRLittleEndian }, storage.Take(128).Concat(new[] { DicomUID.Verification }).ToList()) == 2, Scp.AssociationCount.ToString());

            Scp.Reset();
            Scp.ContextTable = new ScpContextTable();
            var single = await NegotiationProbe.RunAsync(Profile(port), MatrixOnly(new[] { DicomUID.Verification }, new[] { DicomTransferSyntax.ImplicitVRLittleEndian }), CancellationToken.None);
            check("A plan of only the C-ECHO's context still opens one association", Scp.AssociationCount == 1 && Scp.Associations.Single().Contexts.Count == 1 && single.Matrix.AllCells().Single().Item2.Result == CapabilityResult.Accepted, Scp.AssociationCount.ToString());

            Scp.Reset();
            Scp.ContextTable = new ScpContextTable();
            var noEchoCell = await NegotiationProbe.RunAsync(Profile(port), MatrixOnly(new[] { DicomUID.CTImageStorage }, new[] { DicomTransferSyntax.JPEGProcess14SV1 }), CancellationToken.None);
            check("A plan without Verification has no such row", noEchoCell.Matrix.Rows.Count == 1 && noEchoCell.Matrix.Rows[0].SopClassUid == DicomUID.CTImageStorage.UID, null);
            check("The C-ECHO still went out on its own context", Scp.Associations.Single().Contexts.First().AbstractSyntaxUid == DicomUID.Verification.UID && Scp.EchoCount == 1, Scp.EchoCount.ToString());
        }

        // ---------------------------------------------------------------- answers per context

        private static async Task TestContextAnswers(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nNegotiation probe: answers per context");
            var ct = DicomUID.CTImageStorage;
            var syntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1, DicomTransferSyntax.RLELossless, DicomTransferSyntax.JPEG2000Lossless };

            Scp.Reset();
            var table = new ScpContextTable { Default = ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported) };
            table.Set(ct, DicomTransferSyntax.ExplicitVRLittleEndian, ScpAnswer.Accept);
            table.Set(ct, DicomTransferSyntax.JPEGProcess14SV1, ScpAnswer.AcceptOther(DicomTransferSyntax.ExplicitVRLittleEndian));
            table.Set(ct, DicomTransferSyntax.RLELossless, ScpAnswer.NotAnswered);
            table.Set(ct, DicomTransferSyntax.JPEG2000Lossless, ScpAnswer.Reject(DicomPresentationContextResult.RejectUser));
            table.Set(DicomUID.Verification, ScpAnswer.Accept);
            Scp.ContextTable = table;

            var result = await RunCase(new NegotiationProbeTest(MatrixOnly(new[] { ct }, syntaxes)), Profile(port));
            Func<DicomTransferSyntax, MatrixCell> cell = s => result.CapabilityMatrix.Find(ct.UID, s.UID.UID);
            check("Accepted", cell(DicomTransferSyntax.ExplicitVRLittleEndian).Result == CapabilityResult.Accepted, null);
            check("Accepted with another syntax is told apart", cell(DicomTransferSyntax.JPEGProcess14SV1).Result == CapabilityResult.AcceptedOtherSyntax && cell(DicomTransferSyntax.JPEGProcess14SV1).AcceptedTransferSyntaxUid == Explicit, null);
            check("A context left unanswered is told apart", cell(DicomTransferSyntax.RLELossless).Result == CapabilityResult.NotAnswered, cell(DicomTransferSyntax.RLELossless).Result.ToString());
            check("User rejection", cell(DicomTransferSyntax.JPEG2000Lossless).Result == CapabilityResult.UserRejection, null);
            check("An irregular answer fails the test and is named", result.Result == TestResultClass.FAIL && result.Explanation.Contains("another transfer syntax"), result.Explanation);
            check("The association is still released", result.CapabilityMatrix.Associations.Single().Ending == AssociationEnding.Released, null);
            check("The proposed syntax is kept when another was accepted", result.NegotiatedContexts.Single(c => c.AcceptedTransferSyntaxUid == Explicit && c.ProposedTransferSyntaxUids[0] != Explicit).ProposedTransferSyntaxUids[0] == DicomTransferSyntax.JPEGProcess14SV1.UID.UID, null);

            // ------------------------------------------------ nothing accepted
            Scp.Reset();
            Scp.ContextTable = new ScpContextTable { Default = ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported) };
            var none = await RunCase(new NegotiationProbeTest(MatrixOnly(new[] { ct }, syntaxes)), Profile(port));
            check("Nothing accepted is NOT_SUPPORTED", none.Result == TestResultClass.NOT_SUPPORTED && none.CapabilityMatrix.AllCells().All(c => c.Item2.Result == CapabilityResult.AbstractSyntaxNotSupported), none.Explanation);
            check("Nothing accepted: no C-ECHO on the wire, clean release", Scp.EchoCount == 0 && Scp.Associations.Single().Released, Scp.EchoCount.ToString());

            // ------------------------------------------------ remote identity
            Scp.Reset();
            Scp.ContextTable = new ScpContextTable();
            Scp.MaxPduLength = 32768;
            var identity = await NegotiationProbe.RunAsync(Profile(port), MatrixOnly(new[] { ct }, syntaxes), CancellationToken.None);
            check("Max PDU length as the SCP announced it", identity.Matrix.RemoteMaxPduLength == 32768 && identity.Matrix.Associations.Single().RemoteMaxPduLength == 32768, identity.Matrix.RemoteMaxPduLength.ToString());
            check("Implementation class UID and version name", identity.Matrix.RemoteImplementationClassUid == DicomImplementation.ClassUID.UID && identity.Matrix.RemoteImplementationVersionName == DicomImplementation.Version, identity.Matrix.RemoteImplementationVersionName);

            // ------------------------------------------------ roles and extended negotiation
            var featureOptions = new ProbeOptions { SopClasses = new[] { ct, DicomUID.MRImageStorage, DicomUID.StudyRootQueryRetrieveInformationModelFind, DicomUID.StudyRootQueryRetrieveInformationModelGet, DicomUID.Verification }, TransferSyntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian } };

            Scp.Reset();
            Scp.ContextTable = new ScpContextTable();
            Scp.RoleReply = sop => sop == ct.UID ? Tuple.Create<bool?, bool?>(false, true) : Tuple.Create<bool?, bool?>(null, null);
            Scp.ExtendedReply = sop => sop == DicomUID.StudyRootQueryRetrieveInformationModelFind.UID ? new byte[] { 1, 0, 1, 0 } : null;
            var features = await NegotiationProbe.RunAsync(Profile(port), featureOptions, CancellationToken.None);
            var featureAssociation = Scp.Associations.Last();
            check("Features: asked on an association of their own, after the matrix", features.Matrix.Associations.Last().IsFeatureProbe && featureAssociation.Contexts.Count == 5, featureAssociation.Contexts.Count.ToString());
            check("Features: the matrix associations carry none of it", Scp.Associations.Take(Scp.AssociationCount - 1).All(a => a.Contexts.All(c => c.ScuRole == null && c.ScpRole == null) && a.ExtendedNegotiationSopClasses.Count == 0), null);
            check("Features: role proposal on the wire is SCU 0, SCP 1", featureAssociation.Contexts.Where(c => c.ScpRole == true).All(c => c.ScuRole == false) && featureAssociation.Contexts.Count(c => c.ScpRole == true) == 2, null);
            var ctRole = features.Matrix.RoleSelectionReplies.Single(r => r.SopClassUid == ct.UID);
            var mrRole = features.Matrix.RoleSelectionReplies.Single(r => r.SopClassUid == DicomUID.MRImageStorage.UID);
            check("Features: a role reply is recorded", ctRole.Replied && ctRole.ScuRole == false && ctRole.ScpRole == true, null);
            check("Features: no role reply is told from an accepted one", !mrRole.Replied && mrRole.ScuRole == null && mrRole.ScpRole == null, null);
            check("Features: only storage classes get a role reply", features.Matrix.RoleSelectionReplies.Count == 2, null);
            var findReply = features.Matrix.ExtendedNegotiationReplies.Single(r => r.SopClassUid == DicomUID.StudyRootQueryRetrieveInformationModelFind.UID);
            var getReply = features.Matrix.ExtendedNegotiationReplies.Single(r => r.SopClassUid == DicomUID.StudyRootQueryRetrieveInformationModelGet.UID);
            check("Features: an extended negotiation reply is recorded", findReply.Replied && findReply.Requested == "01 01 01 01" && findReply.Accepted == "01 00 01 00", findReply.Accepted);
            check("Features: an unanswered item is told apart", !getReply.Replied && getReply.Requested == "01" && getReply.Accepted == null, getReply.Accepted);

            Scp.Reset();
            Scp.ContextTable = new ScpContextTable();
            Scp.AbortAtAssociation = 2;
            var featureAbort = await NegotiationProbe.RunAsync(Profile(port), featureOptions, CancellationToken.None);
            check("Features: an SCP that aborts on them leaves the matrix intact", featureAbort.Matrix.AllCells().All(c => c.Item2.Result == CapabilityResult.Accepted) && featureAbort.Matrix.Associations.Last().Ending == AssociationEnding.Aborted, featureAbort.Matrix.Associations.Last().Ending.ToString());
        }

        // ---------------------------------------------------------------- associations that do not go well

        private static async Task TestAssociationFailures(int port, int closedPort, Func<string[]> logLines, Action<string, bool, string> check)
        {
            Console.WriteLine("\nNegotiation probe: rejection, abort, timeout");
            var options = MatrixOnly(CapabilityCatalog.StorageSopClasses.Take(4).ToList(), CapabilityCatalog.TransferSyntaxes.Take(5).Select(s => s.Syntax).ToList(), 6);
            var plan = ProbePlan.Create(options);
            var expectedAssociations = plan.Associations.Count;
            check("The failure plan needs several associations", expectedAssociations >= 3, expectedAssociations.ToString());

            Func<TestResult, int, CapabilityResult[]> resultsOf = (result, association) =>
                result.CapabilityMatrix.AllCells().Where(c => c.Item2.Association == association).Select(c => c.Item2.Result).Distinct().ToArray();

            // ------------------------------------------------ every association rejected
            Scp.Reset();
            Scp.RejectWhen = (a, n) => new ScpRejection { Result = DicomRejectResult.Transient, Source = DicomRejectSource.ServiceProviderACSE, Reason = DicomRejectReason.ApplicationContextNotSupported };
            var rejected = await RunCase(new NegotiationProbeTest(options), Profile(port));
            check("All rejected: FAIL, not an exception", rejected.Result == TestResultClass.FAIL, rejected.Result + " " + rejected.Explanation);
            check("All rejected: result, source and reason recorded", rejected.CapabilityMatrix.Associations.All(a => a.Rejection != null && a.Rejection.Result == "Transient" && a.Rejection.Source == "ServiceProviderACSE") && rejected.Explanation.Contains("ServiceProviderACSE"), rejected.Explanation);
            check("All rejected: cells say so", rejected.CapabilityMatrix.AllCells().All(c => c.Item2.Result == CapabilityResult.AssociationRejected), null);
            check("All rejected: the probe tried every association", Scp.AssociationCount == expectedAssociations, Scp.AssociationCount.ToString());

            // ------------------------------------------------ one rejected in the middle
            Scp.Reset();
            ScriptTable(plan);
            Scp.RejectWhen = (a, n) => n == 2 ? new ScpRejection() : null;
            var middle = await RunCase(new NegotiationProbeTest(options), Profile(port));
            check("One rejected: its cells say so, the others hold real answers", resultsOf(middle, 2).Single() == CapabilityResult.AssociationRejected && resultsOf(middle, 1).All(CapabilityMatrix.WasAnswered) && resultsOf(middle, 3).All(CapabilityMatrix.WasAnswered), null);
            check("One rejected: the rest were still probed", Scp.AssociationCount == expectedAssociations, Scp.AssociationCount.ToString());
            check("One rejected: FAIL with the association named", middle.Result == TestResultClass.FAIL && middle.Explanation.Contains("association 2 was rejected"), middle.Explanation);

            // ------------------------------------------------ abort
            Scp.Reset();
            ScriptTable(plan);
            Scp.AbortAtAssociation = 2;
            var aborted = await RunCase(new NegotiationProbeTest(options), Profile(port));
            check("Aborted at A-ASSOCIATE: cells are failed, not guessed", resultsOf(aborted, 2).Single() == CapabilityResult.AssociationFailed && aborted.CapabilityMatrix.Associations[1].Ending == AssociationEnding.Aborted, aborted.CapabilityMatrix.Associations[1].Ending.ToString());
            check("Aborted at A-ASSOCIATE: FAIL, and the probe went on", aborted.Result == TestResultClass.FAIL && Scp.AssociationCount == expectedAssociations, aborted.Explanation);

            Scp.Reset();
            ScriptTable(plan);
            Scp.AbortEchoOfAssociation = 1;
            var abortedLater = await RunCase(new NegotiationProbeTest(options), Profile(port));
            check("Aborted after the A-ASSOCIATE-AC: its answers are kept", resultsOf(abortedLater, 1).All(CapabilityMatrix.WasAnswered) && abortedLater.CapabilityMatrix.Associations[0].Ending == AssociationEnding.Aborted, abortedLater.CapabilityMatrix.Associations[0].Ending.ToString());
            check("Aborted after the A-ASSOCIATE-AC: FAIL", abortedLater.Result == TestResultClass.FAIL, abortedLater.Explanation);
            var abortLogged = logLines().Any(l => l.Contains("Negotiation probe: association 1 ended as Aborted"));
            check("Rejections and aborts are logged", abortLogged && logLines().Any(l => l.Contains("ended as Rejected")), null);

            // ------------------------------------------------ timeout, connection, local
            Scp.Reset();
            Scp.HangOnAssociation = true;
            var hangProfile = Profile(port);
            hangProfile.Network.AssociationTimeoutSeconds = 1;
            var timedOut = await RunCase(new NegotiationProbeTest(options), hangProfile);
            check("No answer to the request: TIMEOUT", timedOut.Result == TestResultClass.TIMEOUT, timedOut.Result + " " + timedOut.Explanation);
            check("No answer to the request: the probe stops and says what is unknown", Scp.AssociationCount == 1 && timedOut.CapabilityMatrix.Associations.Skip(1).All(a => a.Ending == AssociationEnding.NotStarted) && timedOut.CapabilityMatrix.AllCells().Count(c => c.Item2.Result == CapabilityResult.NotProbed) > 0, Scp.AssociationCount.ToString());

            Scp.Reset();
            var closed = await RunCase(new NegotiationProbeTest(options), Profile(closedPort));
            check("Closed port: FAIL or TIMEOUT with a clear message", (closed.Result == TestResultClass.FAIL || closed.Result == TestResultClass.TIMEOUT) && !closed.Explanation.Contains("One or more errors") && !closed.Explanation.Contains("Ett eller flera fel"), closed.Result + " " + closed.Explanation);
            check("Closed port: nothing more is attempted", closed.CapabilityMatrix.Associations.Count(a => a.Ending != AssociationEnding.NotStarted) == 1, null);

            Scp.Reset();
            const string thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
            var tlsProfile = Profile(port);
            tlsProfile.Endpoint.UseTls = true;
            tlsProfile.Endpoint.TlsClientCertThumbprint = thumbprint;
            var local = await RunCase(new NegotiationProbeTest(options), tlsProfile);
            check("Missing client certificate: TOOL_ERROR, nothing reached the SCP", local.Result == TestResultClass.TOOL_ERROR && Scp.AssociationCount == 0, local.Result + " " + local.Explanation);
            check("Missing client certificate: the thumbprint is in no file", !ResultFileWriter.ToJson(new TestRunReport { Results = new List<TestResult> { local } }).Contains(thumbprint) && !local.CapabilityMatrix.ToJson().Contains(thumbprint), null);
        }

        // ---------------------------------------------------------------- negative tests

        private static async Task TestNegativeCases(int port, Action<string, bool, string> check)
        {
            Console.WriteLine("\nNegative negotiation tests");

            var unknownCalling = new UnknownCallingAeTest();
            var wrongCalled = new WrongCalledAeTest();
            var unsupported = new UnsupportedContextsTest();

            check("In the catalog, after C-ECHO", TestCatalog.All[0].Id == "c-echo" && new[] { "negotiation-probe", "negotiation-unknown-calling-ae", "negotiation-wrong-called-ae", "negotiation-unsupported-contexts" }.All(id => TestCatalog.All.Any(t => t.Id == id && t.Category == TestCategory.Capability)), null);

            // ------------------------------------------------ the expected refusal
            Scp.Reset();
            Scp.RejectWhen = (a, n) => a.CallingAE == "DCST4" ? null : new ScpRejection { Source = DicomRejectSource.ServiceUser, Reason = DicomRejectReason.CallingAENotRecognized };
            var calling = await RunCase(unknownCalling, Profile(port));
            check("Unknown calling AE rejected: EXPECTED_FAILURE", calling.Result == TestResultClass.EXPECTED_FAILURE, calling.Result + " " + calling.Explanation);
            check("Unknown calling AE: source and reason recorded", calling.Details["rejectResult"] == "Permanent" && calling.Details["rejectSource"] == "ServiceUser" && calling.Details["rejectReason"] == "CallingAENotRecognized", string.Join(";", calling.Details));
            check("Unknown calling AE: a baseline ran first with the real one", Scp.Associations.First().CallingAe == "DCST4" && Scp.Associations.Last().CallingAe != "DCST4" && Scp.AssociationCount == 2, null);
            check("Unknown calling AE: the wrong title was sent", Scp.Associations.Last().CallingAe == "DCST4NOSUCHAE" && Scp.Associations.Last().CalledAe == "MOCKSCP", Scp.Associations.Last().CallingAe);
            check("Unknown calling AE: explanation has the reason", calling.Explanation.Contains("CallingAENotRecognized"), calling.Explanation);

            Scp.Reset();
            Scp.RejectWhen = (a, n) => a.CalledAE == "MOCKSCP" ? null : new ScpRejection { Result = DicomRejectResult.Transient, Source = DicomRejectSource.ServiceUser, Reason = DicomRejectReason.CalledAENotRecognized };
            var called = await RunCase(wrongCalled, Profile(port));
            check("Wrong called AE rejected: EXPECTED_FAILURE", called.Result == TestResultClass.EXPECTED_FAILURE && called.Details["rejectReason"] == "CalledAENotRecognized" && called.Details["rejectResult"] == "Transient", called.Explanation);
            check("Wrong called AE: only the called title is changed", Scp.Associations.Last().CalledAe == "DCST4NOSUCHAE" && Scp.Associations.Last().CallingAe == "DCST4", Scp.Associations.Last().CalledAe);

            // ------------------------------------------------ a PACS that lets anyone in: a finding, not our error
            Scp.Reset();
            var promiscuous = await RunCase(unknownCalling, Profile(port));
            check("Unknown calling AE accepted: FAIL with a finding, not TOOL_ERROR", promiscuous.Result == TestResultClass.FAIL && promiscuous.Details["finding"] == "accepted-unknown-calling-ae" && promiscuous.Explanation.StartsWith("Finding:"), promiscuous.Result + " " + promiscuous.Explanation);
            check("Unknown calling AE accepted: the association is still released", Scp.Associations.All(a => a.Released), null);

            Scp.Reset();
            var promiscuousCalled = await RunCase(wrongCalled, Profile(port));
            check("Wrong called AE accepted: FAIL with a finding", promiscuousCalled.Result == TestResultClass.FAIL && promiscuousCalled.Details["finding"] == "accepted-wrong-called-ae", promiscuousCalled.Explanation);

            // ------------------------------------------------ an AE title in the profile that happens to be ours
            Scp.Reset();
            var collide = Profile(port);
            collide.Endpoint.CallingAe = "DCST4NOSUCHAE";
            Scp.RejectWhen = (a, n) => n == 1 ? null : new ScpRejection();
            await RunCase(unknownCalling, collide);
            check("A profile using the test's own AE title still gets a different one", Scp.Associations.Last().CallingAe == "DCST4NOSUCHAE2", Scp.Associations.Last().CallingAe);

            // ------------------------------------------------ no verdict possible
            Scp.Reset();
            Scp.RejectAssociation = true;
            var baselineRefused = await RunCase(unknownCalling, Profile(port));
            check("Baseline refused: INDETERMINATE, a refusal would prove nothing", baselineRefused.Result == TestResultClass.INDETERMINATE && Scp.AssociationCount == 1, baselineRefused.Result + " " + baselineRefused.Explanation);

            Scp.Reset();
            Scp.AbortAtAssociation = 2;
            var brokenOff = await RunCase(unknownCalling, Profile(port));
            check("A-ABORT in place of A-ASSOCIATE-RJ: FAIL, and it says so", brokenOff.Result == TestResultClass.FAIL && brokenOff.Explanation.Contains("instead of answering with A-ASSOCIATE-RJ"), brokenOff.Result + " " + brokenOff.Explanation);

            Scp.Reset();
            Scp.HangOnAssociation = true;
            var hangProfile = Profile(port);
            hangProfile.Network.AssociationTimeoutSeconds = 1;
            var hung = await RunCase(unknownCalling, hangProfile);
            check("Baseline without an answer: INDETERMINATE", hung.Result == TestResultClass.INDETERMINATE, hung.Result + " " + hung.Explanation);

            // ------------------------------------------------ only unsupported contexts
            var profile = Profile(port);
            profile.UidRoot = "1.2.3.4.5";

            Scp.Reset();
            Scp.ContextTable = new ScpContextTable { Default = ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported) };
            var allRejected = await RunCase(unsupported, profile);
            var proposed = Scp.Associations.Single().Contexts.Single();
            check("Unsupported: only the context under our root is proposed", proposed.AbstractSyntaxUid.StartsWith("1.2.3.4.5.") && proposed.TransferSyntaxUids.Single() == Implicit, proposed.AbstractSyntaxUid);
            check("Unsupported: UID is valid and recorded", allRejected.Uids["abstractSyntaxUid"] == proposed.AbstractSyntaxUid && proposed.AbstractSyntaxUid.Length <= 64, allRejected.Uids["abstractSyntaxUid"]);
            check("Unsupported: accepted with everything rejected", allRejected.Result == TestResultClass.EXPECTED_FAILURE && allRejected.Details["association"] == "accepted-all-contexts-rejected", allRejected.Explanation);
            check("Unsupported: nothing goes out on the rejected context", Scp.EchoCount == 0 && Scp.Associations.Single().Released, Scp.EchoCount.ToString());
            check("Unsupported: the association ends with a release", allRejected.Timeline.Last().Type == "A-RELEASE-RP" && !allRejected.Timeline.Any(e => e.Type == "C-ECHO-RQ"), string.Join(" ", allRejected.Timeline.Select(e => e.Type)));

            Scp.Reset();
            Scp.RejectWhen = (a, n) => new ScpRejection { Source = DicomRejectSource.ServiceProviderACSE, Reason = DicomRejectReason.NoReasonGiven };
            var wholeRejected = await RunCase(unsupported, profile);
            check("Unsupported: rejected as a whole is told apart", wholeRejected.Result == TestResultClass.EXPECTED_FAILURE && wholeRejected.Details["association"] == "rejected" && wholeRejected.Details["rejectSource"] == "ServiceProviderACSE", wholeRejected.Explanation);

            Scp.Reset();
            Scp.ContextTable = new ScpContextTable();
            var acceptedUnknown = await RunCase(unsupported, profile);
            check("Unsupported: a context accepted is a finding", acceptedUnknown.Result == TestResultClass.FAIL && acceptedUnknown.Details["finding"] == "accepted-unknown-abstract-syntax", acceptedUnknown.Explanation);
            check("Unsupported: and still nothing is sent on it", Scp.EchoCount == 0 && Scp.Associations.Single().Released, Scp.EchoCount.ToString());

            var defaultRoot = await RunCase(unsupported, Profile(port));
            check("Unsupported: under the default root the UID is 2.25 and valid", defaultRoot.Uids["abstractSyntaxUid"].StartsWith("2.25.") && defaultRoot.Uids["abstractSyntaxUid"].Length <= 64, defaultRoot.Uids["abstractSyntaxUid"]);
            var again = await RunCase(unsupported, Profile(port));
            check("Unsupported: the UID is the same in every run", again.Uids["abstractSyntaxUid"] == defaultRoot.Uids["abstractSyntaxUid"], null);
        }

        // ---------------------------------------------------------------- files

        private static async Task TestFiles(int port, string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nCapability matrix files");
            var folder = Path.Combine(temp, "capability");

            Scp.Reset();
            var plan = ProbePlan.Create(null);
            ScriptTable(plan);
            var run = await new TestRunner().RunAsync(Profile(port), new ITestCase[] { new NegotiationProbeTest() }, CancellationToken.None);
            var matrix = run.Results.Single().CapabilityMatrix;

            Scp.Reset();
            ScriptTable(plan);
            var second = await new TestRunner().RunAsync(Profile(port), new ITestCase[] { new NegotiationProbeTest() }, CancellationToken.None);
            check("Two identical runs give identical matrices", matrix.ToJson() == second.Results.Single().CapabilityMatrix.ToJson() && matrix.ToCsv() == second.Results.Single().CapabilityMatrix.ToCsv(), null);
            check("Two identical runs give the same result file", TestSuiteTests.NormalizeJson(ResultFileWriter.ToJson(run)) == TestSuiteTests.NormalizeJson(ResultFileWriter.ToJson(second)), null);

            var files = ResultFileWriter.Write(run, folder);
            check("Writing a run also writes the matrix", File.Exists(files.CapabilityJson) && File.Exists(files.CapabilityCsv) && files.CapabilityJson.EndsWith("-capability.json"), files.CapabilityJson);
            check("Matrix files have no BOM", File.ReadAllBytes(files.CapabilityJson)[0] == (byte)'{' && File.ReadAllBytes(files.CapabilityCsv)[0] == (byte)'s', null);
            check("A result file names its schema version", JsonDocument.Parse(File.ReadAllText(files.Json, Encoding.UTF8)).RootElement.GetProperty("schemaVersion").GetInt32() == TestRunReport.CurrentSchemaVersion, null);
            check("The run file holds the matrix", JsonDocument.Parse(File.ReadAllText(files.Json, Encoding.UTF8)).RootElement.GetProperty("results")[0].GetProperty("capabilityMatrix").GetProperty("rows").GetArrayLength() == plan.SopClasses.Count, null);

            var withoutMatrix = await new TestRunner().RunAsync(Profile(port), new[] { TestCatalog.All[0] }, CancellationToken.None);
            var withoutFiles = ResultFileWriter.Write(withoutMatrix, Path.Combine(folder, "none"));
            check("A run without the probe writes no matrix and no empty field", withoutFiles.CapabilityJson == null && !File.ReadAllText(withoutFiles.Json, Encoding.UTF8).Contains("capabilityMatrix") && !File.ReadAllText(withoutFiles.Json, Encoding.UTF8).Contains("\"association\""), null);

            var loaded = CapabilityMatrix.Load(files.CapabilityJson);
            check("JSON round trip: every cell", matrix.AllCells().All(c => loaded.Find(c.Item1.SopClassUid, c.Item2.TransferSyntaxUid)?.Result == c.Item2.Result), null);
            check("JSON round trip: identical", loaded.ToJson() == matrix.ToJson(), null);
            check("JSON: results and names as text", File.ReadAllText(files.CapabilityJson, Encoding.UTF8).Contains("\"Accepted\"") && File.ReadAllText(files.CapabilityJson, Encoding.UTF8).Contains("CT Image Storage"), null);
            check("JSON: remote identity and associations", loaded.RemoteImplementationClassUid == DicomImplementation.ClassUID.UID && loaded.Associations.Count == plan.Associations.Count, null);

            var csv = File.ReadAllText(files.CapabilityCsv, Encoding.UTF8);
            var rows = TestSuiteTests.ParseCsv(csv);
            check("CSV: one row per cell", rows.Count == plan.SopClasses.Count * plan.TransferSyntaxes.Count + 1, rows.Count.ToString());
            check("CSV: every row has every column", rows.All(r => r.Count == rows[0].Count) && rows[0][0] == "sop_class_uid", null);
            var ctRow = rows.Skip(1).First(r => r[0] == DicomUID.CTImageStorage.UID && r[2] == Explicit);
            check("CSV: the cell's values", ctRow[1] == DicomUID.CTImageStorage.Name && ctRow[4] == matrix.Find(DicomUID.CTImageStorage.UID, Explicit).Result.ToString(), string.Join("|", ctRow));

            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
                check("Same content under sv-SE", matrix.ToJson() == loaded.ToJson() && matrix.ToCsv() == csv, null);
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }

            var cliFolder = Path.Combine(folder, "cli");
            Directory.CreateDirectory(cliFolder);
            var profilePath = Path.Combine(cliFolder, "target.json");
            Profile(port).Save(profilePath);
            Scp.Reset();
            ScriptTable(plan);
            var cli = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "negotiation-probe", "--out", Path.Combine(cliFolder, "out"), "--log-dir", Path.Combine(cliFolder, "logs"));
            check("Command line: runs the probe and names the matrix files", cli.ExitCode == 0 && cli.Output.Contains("negotiation-probe: PASS") && cli.Output.Contains("-capability.json") && cli.Output.Contains("-capability.csv"), cli.Output);
            var cliMatrix = CapabilityMatrix.Load(Directory.GetFiles(Path.Combine(cliFolder, "out"), "*-capability.json").Single());
            check("Command line: the same matrix as the library", cliMatrix.ToJson() == matrix.ToJson(), null);
        }

        // ---------------------------------------------------------------- claimed vs actual

        private static CapabilityMatrix Actual(params Tuple<string, string, CapabilityResult>[] cells)
        {
            var matrix = new CapabilityMatrix();
            foreach (var group in cells.GroupBy(c => c.Item1))
            {
                matrix.Rows.Add(new MatrixRow { SopClassUid = group.Key, Cells = group.Select(c => new MatrixCell { TransferSyntaxUid = c.Item2, Result = c.Item3 }).ToList() });
            }
            return matrix;
        }

        private static Tuple<string, string, CapabilityResult> C(string sop, string syntax, CapabilityResult result) => Tuple.Create(sop, syntax, result);

        private static void TestComparison(string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nClaimed vs actual");
            var ct = DicomUID.CTImageStorage.UID;
            var mr = DicomUID.MRImageStorage.UID;

            var actual = Actual(
                C(ct, Implicit, CapabilityResult.Accepted),
                C(ct, Explicit, CapabilityResult.TransferSyntaxNotSupported),
                C(mr, Implicit, CapabilityResult.Accepted),
                C(mr, Explicit, CapabilityResult.AssociationFailed));
            var claimed = Actual(
                C(ct, Implicit, CapabilityResult.Accepted),
                C(ct, Explicit, CapabilityResult.Accepted),
                C(mr, Explicit, CapabilityResult.Accepted),
                C(DicomUID.NuclearMedicineImageStorage.UID, Implicit, CapabilityResult.Accepted));
            var comparison = CapabilityComparison.Compare(actual, claimed);

            Func<CapabilityDifferenceKind, string[]> of = kind => comparison.OfKind(kind).Select(d => d.SopClassUid + "|" + d.TransferSyntaxUid).ToArray();
            check("Claimed but rejected", of(CapabilityDifferenceKind.ClaimedButRejected).Single() == ct + "|" + Explicit, string.Join(",", of(CapabilityDifferenceKind.ClaimedButRejected)));
            check("Accepted but not claimed", of(CapabilityDifferenceKind.AcceptedButNotClaimed).Single() == mr + "|" + Implicit, string.Join(",", of(CapabilityDifferenceKind.AcceptedButNotClaimed)));
            check("Claimed but the probe got no answer is not blamed on the SCP", of(CapabilityDifferenceKind.ClaimedButNotProbed).Contains(mr + "|" + Explicit), string.Join(",", of(CapabilityDifferenceKind.ClaimedButNotProbed)));
            check("Claimed for something the probe did not propose", of(CapabilityDifferenceKind.ClaimedButNotProbed).Contains(DicomUID.NuclearMedicineImageStorage.UID + "|" + Implicit) && of(CapabilityDifferenceKind.ClaimedButNotProbed).Length == 2, string.Join(",", of(CapabilityDifferenceKind.ClaimedButNotProbed)));
            check("Agreeing combinations are counted", comparison.Agreeing == 1, comparison.Agreeing.ToString());
            check("Names are filled in for the list", comparison.Differences[0].SopClassName == DicomUID.CTImageStorage.Name, comparison.Differences[0].SopClassName);

            var claimedRejected = Actual(C(ct, Implicit, CapabilityResult.UserRejection));
            var notAClaim = CapabilityComparison.Compare(Actual(C(ct, Implicit, CapabilityResult.Accepted)), claimedRejected);
            check("Only Accepted counts as a claim", notAClaim.OfKind(CapabilityDifferenceKind.AcceptedButNotClaimed).Count() == 1, null);

            var same = CapabilityComparison.Compare(actual, actual);
            check("A matrix agrees with itself", same.Differences.Count(d => d.Kind != CapabilityDifferenceKind.ClaimedButNotProbed) == 0, null);

            // ------------------------------------------------ files
            var folder = Path.Combine(temp, "claimed");
            Directory.CreateDirectory(folder);
            Func<string, string> write = text =>
            {
                var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, text, Encoding.UTF8);
                return path;
            };

            var minimal = CapabilityMatrix.Load(write("// From the Conformance Statement\n{ \"rows\": [ { \"sopClassUid\": \"" + ct + "\", \"cells\": [ { \"transferSyntaxUid\": \"" + Implicit + "\", \"result\": \"Accepted\", }, ], }, ], }"));
            check("A hand-written claim: comments, trailing commas and only the required fields", minimal.Find(ct, Implicit).Result == CapabilityResult.Accepted, null);

            Func<string, CapabilityFileException> failure = text =>
            {
                try { CapabilityMatrix.Load(write(text)); return null; }
                catch (CapabilityFileException ex) { return ex; }
            };
            var syntaxError = failure("{ \"rows\": ");
            check("Broken JSON: clear error", syntaxError != null && syntaxError.Message.Contains("not valid"), syntaxError?.Message);
            var empty = failure("{ }");
            check("No rows: clear error", empty != null && empty.Message.Contains("no rows"), empty?.Message);
            var unknownResult = failure("{ \"rows\": [ { \"sopClassUid\": \"1.2\", \"cells\": [ { \"transferSyntaxUid\": \"1.3\", \"result\": \"Maybe\" } ] } ] }");
            check("Unknown result: clear error", unknownResult != null && unknownResult.Message.Contains("not valid"), unknownResult?.Message);
            var noResult = failure("{ \"rows\": [ { \"sopClassUid\": \"1.2\", \"cells\": [ { \"transferSyntaxUid\": \"1.3\" } ] } ] }");
            check("A cell without a result is not silently a claim", noResult != null && noResult.Message.Contains("not valid"), noResult?.Message);
            var twice = failure("{ \"rows\": [ { \"sopClassUid\": \"1.2\", \"cells\": [ { \"transferSyntaxUid\": \"1.3\", \"result\": \"Accepted\" }, { \"transferSyntaxUid\": \"1.3\", \"result\": \"NoReason\" } ] } ] }");
            check("The same cell twice: clear error", twice != null && twice.Message.Contains("twice"), twice?.Message);
            var newer = failure("{ \"schemaVersion\": 99, \"rows\": [ { \"sopClassUid\": \"1.2\", \"cells\": [] } ] }");
            check("Newer schema: clear error", newer != null && newer.Message.Contains("schema version"), newer?.Message);

            CapabilityFileException missing = null;
            try { CapabilityMatrix.Load(Path.Combine(folder, "none.json")); }
            catch (CapabilityFileException ex) { missing = ex; }
            check("Missing file: clear error", missing != null && missing.Message.Contains("Could not read"), missing?.Message);
        }
    }
}
