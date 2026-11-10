using DCST4.Core.Network;
using DCST4.Core.Testing;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>Phase 5: Storage Commitment Push Model against the mock, on the same association and on a new one.</summary>
    internal static class CommitmentTests
    {
        private const int ListenPort = 41216;
        private const string Store = "store-ct,store-mr";

        private static int _port;
        private static string _temp;
        private static Action<string, bool, string> _check;

        public static async Task Run(int port, string temp, Action<string, bool, string> check)
        {
            _port = port;
            _temp = temp;
            _check = check;

            await TestSameAssociation();
            await TestSeparateAssociation();
            await TestReleasedTooSoon();
            await TestPartialFailure();
            await TestUnknownInstance();
            await TestLateAndNever();
            await TestUnknownTransaction();
            await TestLocalScpDirectly();
            await TestRefusals();
            await TestResultFiles();
            await TestCli();
        }

        // ---------------------------------------------------------------- helpers

        private static TargetProfile Profile() => new TargetProfile
        {
            Name = "Mock",
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = _port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 10 },
            LocalScpAe = "DCST4",
            LocalScpPort = ListenPort,
            UidRoot = "1.2.826.0.1.3680043.10.999",
        };

        private static void Fresh(CommitMode mode, int delayMs = 0)
        {
            Scp.Reset();
            Scp.KeepStored = true;
            Scp.Commit = mode;
            Scp.CommitDelayMs = delayMs;
            Scp.CommitDestinations["DCST4"] = Tuple.Create("127.0.0.1", ListenPort, "DCST4");
        }

        private static TestRunOptions Options(int linger, int wait = 3, int grace = 2) => new TestRunOptions
        {
            Seed = 11,
            IndexWaitSeconds = 1,
            CommitLingerSeconds = linger,
            CommitWaitSeconds = wait,
            CommitGraceSeconds = grace,
        };

        private static Task<TestRunReport> Execute(string tests, TestRunOptions options, CancellationToken token = default(CancellationToken))
        {
            var selected = TestCatalog.Select(tests, out var error);
            if (selected == null) throw new InvalidOperationException(error);
            return new TestRunner().RunAsync(Profile(), selected, token, DCST4.Core.TestData.TestPatientNaming.RegistrySwedishName, options);
        }

        private static TestResult Result(TestRunReport report, string id) => report.Results.Single(r => r.TestId == id);

        private static string Show(TestResult result) => $"{result.Result}: {result.Explanation}";

        private static string Steps(TestResult result) => string.Join(", ", result.Timeline.Select(e => $"{e.Association}:{e.Direction} {e.Type}{(e.Status == null ? string.Empty : " " + e.Status)}"));

        // ---------------------------------------------------------------- the two ways

        private static async Task TestSameAssociation()
        {
            Console.WriteLine("\nStorage Commitment: report on the same association");
            Fresh(CommitMode.SameAssociation, 300);
            var clock = Stopwatch.StartNew();
            var report = await Execute(Store + ",commit-stored", Options(linger: 2));
            var result = Result(report, "commit-stored");
            var commitment = result.Commitment;

            _check("Same association: PASS", result.Result == TestResultClass.PASS, Show(result));
            _check("Same association: recorded as such", commitment?.ReportVia == "SameAssociation" && result.Details["reportVia"] == "SameAssociation", commitment?.ReportVia);
            _check("Same association: Event Type 1, every instance committed", commitment?.EventTypeId == 1 && commitment.Instances.Count == 2 && commitment.Instances.All(i => i.Outcome == CommitmentInstanceOutcome.Committed), null);
            _check("Same association: the time from N-ACTION to report is measured", commitment?.ReportAfterMs >= 300 && commitment.ReportAfterMs < 5000, commitment?.ReportAfterMs.ToString());
            _check("Same association: N-ACTION status 0x0000 recorded", commitment?.ActionStatus == "0x0000" && result.StatusCodes.Any(s => s.Operation == "N-ACTION" && s.Code == "0x0000"), null);
            _check("Same association: the context is recorded", result.NegotiatedContexts.Any(c => c.AbstractSyntaxUid == DicomUID.StorageCommitmentPushModel.UID && c.AcceptedTransferSyntaxUid != null), null);
            var steps = result.Timeline.Where(e => e.Association == 1).Select(e => e.Type).ToList();
            _check("Same association: the timeline has the request and the report in order",
                steps.IndexOf("N-ACTION-RQ") >= 0 && steps.IndexOf("N-ACTION-RQ") < steps.IndexOf("N-ACTION-RSP") && steps.IndexOf("N-ACTION-RSP") < steps.IndexOf("N-EVENT-REPORT-RQ") && steps.IndexOf("N-EVENT-REPORT-RQ") < steps.IndexOf("N-EVENT-REPORT-RSP") && steps.Last() == "A-RELEASE-RP",
                Steps(result));
            _check("Same association: we answered 0x0000", Scp.CommitReportStatuses.Count == 1 && Scp.CommitReportStatuses.Single().Item2 == 0x0000, null);
            _check("Same association: the mock got both instances in one N-ACTION", Scp.CommitRequests.Count == 1 && Scp.CommitRequests.Single().SopInstanceUids.Count == 2 && Scp.CommitRequests.Single().TransactionUid == commitment.TransactionUid, null);
            _check("Same association: the Transaction UID is under the profile's root", commitment?.TransactionUid.StartsWith("1.2.826.0.1.3680043.10.999.") == true, commitment?.TransactionUid);
            _check("Same association: the association was held for the linger", result.DurationMs >= 2000, result.DurationMs.ToString());
            _check("Same association: no association came in", result.Details["incomingAssociations"] == "0", null);
            foreach (var row in report.Chain)
            {
                _check($"Same association: chain {row.ObjectType} committed PASS", row.Committed?.Result == TestResultClass.PASS && row.Committed.Explanation.Contains("association of the N-ACTION"), row.Committed?.Explanation);
            }
            _check("Same association: well within the time", clock.Elapsed < TimeSpan.FromSeconds(15), clock.Elapsed.ToString());
        }

        private static async Task TestSeparateAssociation()
        {
            Console.WriteLine("\nStorage Commitment: report on a new association");
            Fresh(CommitMode.NewAssociation, 200);
            var report = await Execute(Store + ",commit-stored", Options(linger: 0));
            var result = Result(report, "commit-stored");
            var commitment = result.Commitment;

            _check("New association: PASS", result.Result == TestResultClass.PASS, Show(result));
            _check("New association: recorded as such, with who sent it", commitment?.ReportVia == "SeparateAssociation" && commitment.ReportCallingAe == "MOCKSCP", commitment?.ReportVia);
            _check("New association: the explanation says which way", result.Explanation.Contains("opened to DCST4"), result.Explanation);
            _check("New association: the mock got 0x0000", Scp.CommitReportStatuses.Count == 1 && Scp.CommitReportStatuses.Single().Item2 == 0x0000, string.Join(",", Scp.CommitReportStatuses.Select(s => s.Item2)));
            _check("New association: the SCP role it proposed was accepted", Scp.CommitRoleAccepted.Count == 1 && Scp.CommitRoleAccepted.Single(), string.Join(",", Scp.CommitRoleAccepted));
            _check("New association: the incoming association is association 2 of the timeline",
                result.Timeline.Any(e => e.Association == 2 && e.Type == "A-ASSOCIATE-RQ" && e.Direction == ProtocolEvent.Received) &&
                result.Timeline.Any(e => e.Association == 2 && e.Type == "N-EVENT-REPORT-RQ") &&
                result.Timeline.Any(e => e.Association == 2 && e.Type == "N-EVENT-REPORT-RSP" && e.Status == "0x0000" && e.Direction == ProtocolEvent.Sent),
                Steps(result));
            _check("New association: our own association has no report", !result.Timeline.Any(e => e.Association == 1 && e.Type.StartsWith("N-EVENT-REPORT")), Steps(result));
            _check("New association: chain committed PASS for every object type", report.Chain.All(r => r.Committed?.Result == TestResultClass.PASS), null);
        }

        private static async Task TestReleasedTooSoon()
        {
            Console.WriteLine("\nStorage Commitment: released before a same-association-only report");
            Fresh(CommitMode.SameAssociation, 500);
            var report = await Execute("store-ct,commit-stored", Options(linger: 0, wait: 1, grace: 1));
            var result = Result(report, "commit-stored");
            _check("Released at once: TIMEOUT", result.Result == TestResultClass.TIMEOUT, Show(result));
            _check("Released at once: says the report could only come on a new association", result.Explanation.Contains("released at once") && result.Explanation.Contains("No association reached DCST4"), result.Explanation);
            _check("Released at once: the chain shows TIMEOUT", report.Chain.Single().Committed?.Result == TestResultClass.TIMEOUT, null);
            _check("Released at once: no report time", result.Commitment.ReportAfterMs == null && result.Commitment.ReportVia == null && !result.Commitment.Late, null);
        }

        // ---------------------------------------------------------------- what the report says

        private static async Task TestPartialFailure()
        {
            Console.WriteLine("\nStorage Commitment: a stored instance that fails");
            Fresh(CommitMode.NewAssociation);
            string failing = null;
            Scp.CommitFailure = uid =>
            {
                lock (Scp.Archive)
                {
                    var mr = Scp.Archive.FirstOrDefault(a => a.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty) == "MR");
                    failing = mr?.SopUid;
                    return uid == failing ? (ushort?)0x0110 : null;
                }
            };
            var report = await Execute(Store + ",commit-stored", Options(linger: 0));
            var result = Result(report, "commit-stored");
            var failed = result.Commitment.Instances.Where(i => i.Outcome == CommitmentInstanceOutcome.Failed).ToList();

            _check("Partial: FAIL", result.Result == TestResultClass.FAIL, Show(result));
            _check("Partial: Event Type 2", result.Commitment.EventTypeId == 2 && result.Details["eventType"] == "2", null);
            _check("Partial: the failed instance, with its reason", failed.Count == 1 && failed[0].SopInstanceUid == failing && failed[0].FailureReason == "0x0110" && failed[0].FailureReasonText == "Processing failure", string.Join(";", failed.Select(f => f.SopInstanceUid + " " + f.FailureReason)));
            _check("Partial: the explanation names the reason", result.Explanation.Contains("1 of 2") && result.Explanation.Contains("0x0110"), result.Explanation);
            _check("Partial: not a finding, since the report follows the standard", !result.Details.ContainsKey("finding"), null);
            var ct = report.Chain.Single(r => r.ObjectType == "ct");
            var mrRow = report.Chain.Single(r => r.ObjectType == "mr");
            _check("Partial: the chain fails only that object type", ct.Committed?.Result == TestResultClass.PASS && mrRow.Committed?.Result == TestResultClass.FAIL && mrRow.Committed.Explanation.Contains("0x0110"), mrRow.Committed?.Explanation);
        }

        private static async Task TestUnknownInstance()
        {
            Console.WriteLine("\nStorage Commitment: an instance that was never stored");
            Fresh(CommitMode.NewAssociation);
            var report = await Execute(Store + ",commit-unknown-instance", Options(linger: 0));
            var result = Result(report, "commit-unknown-instance");
            var unknown = result.Commitment.Instances.SingleOrDefault(i => i.NeverStored);

            _check("Unknown instance: EXPECTED_FAILURE", result.Result == TestResultClass.EXPECTED_FAILURE, Show(result));
            _check("Unknown instance: refused with 0x0112", unknown?.Outcome == CommitmentInstanceOutcome.Failed && unknown.FailureReason == "0x0112" && unknown.FailureReasonText == "No such object instance", unknown?.FailureReason);
            _check("Unknown instance: the stored ones committed", result.Commitment.Instances.Where(i => !i.NeverStored).All(i => i.Outcome == CommitmentInstanceOutcome.Committed) && result.Commitment.Instances.Count(i => !i.NeverStored) == 1, null);
            _check("Unknown instance: the UID is under the root and was asked for", unknown?.SopInstanceUid.StartsWith("1.2.826.0.1.3680043.10.999.") == true && Scp.CommitRequests.Single().SopInstanceUids.Contains(unknown.SopInstanceUid) && result.Uids["unknownSopInstanceUid"] == unknown.SopInstanceUid, unknown?.SopInstanceUid);
            _check("Unknown instance: does not touch the chain", report.Chain.All(r => r.Committed == null), null);

            Fresh(CommitMode.NewAssociation);
            Scp.CommitClaimAll = true;
            var claimed = Result(await Execute("store-ct,commit-unknown-instance", Options(linger: 0)), "commit-unknown-instance");
            _check("Claims to commit the unknown: FAIL, as a finding", claimed.Result == TestResultClass.FAIL && claimed.Details.ContainsKey("finding") && claimed.Explanation.Contains("never stored is committed"), Show(claimed));

            Fresh(CommitMode.NewAssociation);
            Scp.CommitOmitReason = true;
            var noReason = Result(await Execute("store-ct,commit-unknown-instance", Options(linger: 0)), "commit-unknown-instance");
            _check("A failure without a Failure Reason: FAIL", noReason.Result == TestResultClass.FAIL && noReason.Explanation.Contains("Failure Reason") && noReason.Details.ContainsKey("finding"), Show(noReason));
        }

        private static async Task TestLateAndNever()
        {
            Console.WriteLine("\nStorage Commitment: late and no report");
            Fresh(CommitMode.NewAssociation, 2500);
            var report = await Execute("store-ct,commit-stored", Options(linger: 0, wait: 1, grace: 4));
            var late = Result(report, "commit-stored");
            _check("Late: TIMEOUT", late.Result == TestResultClass.TIMEOUT, Show(late));
            _check("Late: the explanation says it came later, and on which association", late.Explanation.Contains("It came later") && late.Explanation.Contains("opened to DCST4"), late.Explanation);
            _check("Late: recorded as late, with its time and content", late.Commitment.Late && late.Commitment.LateVia == "SeparateAssociation" && late.Commitment.ReportAfterMs >= 2000 && late.Commitment.Instances.All(i => i.Outcome == CommitmentInstanceOutcome.Committed), late.Commitment.ReportAfterMs.ToString());
            _check("Late: the late report was accepted, since we issued it", Scp.CommitReportStatuses.Count == 1 && Scp.CommitReportStatuses.Single().Item2 == 0x0000, null);
            _check("Late: the chain shows TIMEOUT", report.Chain.Single().Committed?.Result == TestResultClass.TIMEOUT, null);

            Fresh(CommitMode.Never);
            var never = Result(await Execute("store-ct,commit-stored", Options(linger: 0, wait: 1, grace: 1)), "commit-stored");
            _check("Never: TIMEOUT", never.Result == TestResultClass.TIMEOUT, Show(never));
            _check("Never: the explanation says it did not come later either", never.Explanation.Contains("did not come in a further 1 s") && !never.Commitment.Late, never.Explanation);

            Fresh(CommitMode.Never);
            using (var cancel = new CancellationTokenSource())
            {
                var clock = Stopwatch.StartNew();
                var run = Execute("store-ct,commit-stored", Options(linger: 0, wait: 30, grace: 30), cancel.Token);
                while (Scp.CommitRequests.IsEmpty && clock.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
                await Task.Delay(300);
                cancel.Cancel();
                var cancelled = await run;
                var commit = Result(cancelled, "commit-stored");
                _check("Cancel during the wait: INDETERMINATE", commit.Result == TestResultClass.INDETERMINATE, Show(commit));
                _check("Cancel during the wait: quick", clock.Elapsed < TimeSpan.FromSeconds(15), clock.Elapsed.ToString());
                _check("Cancel during the wait: the run is marked cancelled", cancelled.Cancelled, null);
            }
        }

        // ---------------------------------------------------------------- what we refuse

        private static async Task TestUnknownTransaction()
        {
            Console.WriteLine("\nStorage Commitment: a report for a transaction we did not issue");
            foreach (var mode in new[] { CommitMode.SameAssociation, CommitMode.NewAssociation })
            {
                var label = mode == CommitMode.SameAssociation ? "same association" : "new association";
                Fresh(mode);
                Scp.CommitBogusFirst = true;
                var result = Result(await Execute("store-ct,commit-stored", Options(linger: mode == CommitMode.SameAssociation ? 2 : 0)), "commit-stored");
                var bogus = Scp.CommitReportStatuses.Where(s => s.Item1 == Scp.BogusTransactionUid).Select(s => s.Item2).ToList();
                var real = Scp.CommitReportStatuses.Where(s => s.Item1 != Scp.BogusTransactionUid).Select(s => s.Item2).ToList();

                _check($"Bogus transaction, {label}: refused with 0x0110", bogus.Count == 1 && bogus[0] == 0x0110, string.Join(",", Scp.CommitReportStatuses.Select(s => s.Item1 + "=" + s.Item2)));
                _check($"Bogus transaction, {label}: the real report is still accepted", real.Count == 1 && real[0] == 0x0000 && result.Result == TestResultClass.PASS, Show(result));
                _check($"Bogus transaction, {label}: recorded in the result", result.Details["rejectedReports"] == "1" && result.Commitment.RejectedReports.Single().TransactionUid == Scp.BogusTransactionUid && result.Commitment.RejectedReports.Single().AnsweredStatus == "0x0110", null);
                _check($"Bogus transaction, {label}: the refusal is in the timeline", result.Timeline.Any(e => e.Type == "N-EVENT-REPORT-RSP" && e.Status == "0x0110"), Steps(result));
            }
        }

        private static async Task TestLocalScpDirectly()
        {
            Console.WriteLine("\nStorage Commitment: the local SCP on its own");
            var folder = Path.Combine(_temp, "commit-direct");

            async Task<Tuple<ushort, string>> Send(ushort eventType, string transactionUid)
            {
                var client = DicomClientFactory.Create("127.0.0.1", ListenPort, false, "OTHERPACS", "DCST4");
                var answer = Tuple.Create((ushort)0xFFFF, string.Empty);
                var report = new DicomNEventReportRequest(DicomUID.StorageCommitmentPushModel, DicomUID.StorageCommitmentPushModelInstance, eventType)
                {
                    Dataset = new DicomDataset { { DicomTag.TransactionUID, transactionUid } },
                    OnResponseReceived = (q, r) => answer = Tuple.Create(r.Command.GetSingleValueOrDefault(DicomTag.Status, r.Status.Code), r.Command.GetSingleValueOrDefault(DicomTag.ErrorComment, string.Empty)),
                };
                await client.AddRequestAsync(report);
                await client.SendAsync();
                return answer;
            }

            using (LocalStorageServer.Start(ListenPort, folder, null))
            {
                var none = await Send(1, "1.2.3.4");
                _check("Nothing outstanding: refused with 0x0110 and a comment", none.Item1 == 0x0110 && none.Item2 == CommitmentTracker.UnknownTransactionComment, none.Item1.ToString("X4") + " " + none.Item2);
            }

            var tracker = new CommitmentTracker();
            var waiter = tracker.Register("1.2.3.5");
            using (LocalStorageServer.Start(ListenPort, folder, null, tracker))
            {
                var other = await Send(1, "1.2.3.4");
                _check("Another transaction: refused with 0x0110", other.Item1 == 0x0110, other.Item1.ToString("X4"));
                _check("Another transaction: the wait goes on", !waiter.IsCompleted && tracker.Rejected.Count == 1 && tracker.Rejected[0].Via == CommitmentVia.SeparateAssociation && tracker.Rejected[0].CallingAe == "OTHERPACS", null);
                var badType = await Send(3, "1.2.3.5");
                _check("Event Type 3: refused with 0x0113", badType.Item1 == 0x0113 && !waiter.IsCompleted, badType.Item1.ToString("X4"));
                var ours = await Send(1, "1.2.3.5");
                _check("Our transaction: accepted", ours.Item1 == 0x0000 && waiter.IsCompleted && waiter.Result.EventTypeId == 1, ours.Item1.ToString("X4"));
                var again = await Send(1, "1.2.3.5");
                _check("The same transaction again: accepted and counted", again.Item1 == 0x0000 && tracker.Repeated.Count == 1, again.Item1.ToString("X4"));
            }

            var expired = new CommitmentTracker();
            _ = expired.Register("1.2.3.6");
            expired.Expire("1.2.3.6");
            using (LocalStorageServer.Start(ListenPort, folder, null, expired))
            {
                var late = await Send(1, "1.2.3.6");
                _check("After the wait: accepted as late", late.Item1 == 0x0000 && expired.Late.Count == 1 && expired.Late[0].Late, late.Item1.ToString("X4"));
            }
        }

        private static async Task TestRefusals()
        {
            Console.WriteLine("\nStorage Commitment: refusals and local problems");

            Fresh(CommitMode.SameIfOpen);
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.StorageCommitmentPushModel, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var unsupported = await Execute("store-ct,commit-stored", Options(linger: 0));
            _check("Context rejected: NOT_SUPPORTED", Result(unsupported, "commit-stored").Result == TestResultClass.NOT_SUPPORTED && Scp.CommitRequests.IsEmpty, Show(Result(unsupported, "commit-stored")));
            _check("Context rejected: the chain says so", unsupported.Chain.Single().Committed?.Result == TestResultClass.NOT_SUPPORTED, null);

            Fresh(CommitMode.SameIfOpen);
            Scp.CommitActionStatus = DicomStatus.ProcessingFailure;
            var refused = Result(await Execute("store-ct,commit-stored", Options(linger: 0)), "commit-stored");
            _check("N-ACTION refused: FAIL with the raw status", refused.Result == TestResultClass.FAIL && refused.Explanation.Contains("0x0110") && refused.Commitment.ActionStatus == "0x0110", Show(refused));

            Fresh(CommitMode.SameIfOpen);
            Scp.CommitActionStatus = DicomStatus.NoSuchActionType;
            var noAction = Result(await Execute("store-ct,commit-stored", Options(linger: 0)), "commit-stored");
            _check("N-ACTION answered No Such Action Type: NOT_SUPPORTED", noAction.Result == TestResultClass.NOT_SUPPORTED, Show(noAction));

            Fresh(CommitMode.SameIfOpen);
            var nothing = await Execute("commit-stored,commit-unknown-instance", Options(linger: 0));
            _check("Nothing stored: INDETERMINATE", nothing.Results.All(r => r.Result == TestResultClass.INDETERMINATE && r.Explanation.Contains("stored earlier")) && Scp.CommitRequests.IsEmpty, string.Join("; ", nothing.Results.Select(Show)));

            Fresh(CommitMode.SameIfOpen);
            using (LocalStorageServer.Start(ListenPort, Path.Combine(_temp, "commit-occupied"), null))
            {
                var taken = Result(await Execute("store-ct,commit-stored", Options(linger: 0)), "commit-stored");
                _check("Port taken: TOOL_ERROR naming Storage Commitment", taken.Result == TestResultClass.TOOL_ERROR && taken.Explanation.Contains("Storage Commitment reports") && taken.Explanation.Contains(ListenPort.ToString()), Show(taken));
            }

            var options = Options(linger: 5, wait: 3);
            _check("Options: a linger longer than the wait is refused", options.Validate()?.Contains("longer than the wait") == true, options.Validate());
            _check("Options: negative times are refused", Options(linger: -1).Validate() != null && Options(linger: 0, grace: -1).Validate() != null, null);
            _check("Options: the defaults are 10, 60 and 30", new TestRunOptions().CommitLingerSeconds == 10 && new TestRunOptions().CommitWaitSeconds == 60 && new TestRunOptions().CommitGraceSeconds == 30 && new TestRunOptions().Validate() == null, null);
        }

        // ---------------------------------------------------------------- result files and the command line

        private static async Task TestResultFiles()
        {
            Console.WriteLine("\nStorage Commitment: result files");
            Fresh(CommitMode.NewAssociation);
            var first = await Execute(Store + ",commit-stored,commit-unknown-instance", Options(linger: 0));
            Fresh(CommitMode.NewAssociation);
            var second = await Execute(Store + ",commit-stored,commit-unknown-instance", Options(linger: 0));

            var json = ResultFileWriter.ToJson(first);
            _check("JSON: schema version 4", TestRunReport.CurrentSchemaVersion == 4 && json.Contains("\"schemaVersion\": 4"), null);
            _check("JSON: holds the commitment report", json.Contains("\"commitment\"") && json.Contains("\"reportVia\": \"SeparateAssociation\"") && json.Contains("\"reportAfterMs\"") && json.Contains("\"failureReason\": \"0x0112\""), null);
            _check("JSON: the chain has committed", json.Contains("\"committed\": {"), null);
            var firstJson = TestSuiteTests.NormalizeJson(json);
            var secondJson = TestSuiteTests.NormalizeJson(ResultFileWriter.ToJson(second));
            _check("Commitment, same seed: the same result, apart from times", firstJson == secondJson, TestSuiteTests.FirstDifference(firstJson, secondJson));

            var files = ResultFileWriter.Write(first, Path.Combine(_temp, "commit-files"));
            var chain = File.ReadAllLines(files.ChainCsv, Encoding.UTF8);
            _check("Chain CSV: committed column filled", chain.Length == 3 && chain.Skip(1).All(l => l.Split(',')[7] == "PASS"), string.Join(" | ", chain));
        }

        private static async Task TestCli()
        {
            Console.WriteLine("\nStorage Commitment: command line");
            var folder = Path.Combine(_temp, "commit-cli");
            Directory.CreateDirectory(folder);
            var profilePath = Path.Combine(folder, "profile.json");
            Profile().Save(profilePath);
            var logs = Path.Combine(folder, "logs");

            Fresh(CommitMode.NewAssociation);
            var run = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "store-ct,commit-stored", "--out", Path.Combine(folder, "out"), "--log-dir", logs,
                "--commit-linger", "0", "--commit-wait", "5", "--commit-grace", "1", "--seed", "3");
            _check("Command line: exit code 0", run.ExitCode == 0, run.Output);
            var json = File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "out"), "*.json").Single(), Encoding.UTF8);
            _check("Command line: the options reach the test", json.Contains("\"lingerSeconds\": 0") && json.Contains("\"waitSeconds\": 5") && json.Contains("\"graceSeconds\": 1"), null);

            var bad = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "commit-stored", "--out", folder, "--commit-wait", "soon");
            _check("Command line: a bad --commit-wait gives exit code 2", bad.ExitCode == 2 && bad.Output.Contains("--commit-wait"), bad.Output);
            var longer = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "commit-stored", "--out", folder, "--commit-linger", "20", "--commit-wait", "10");
            _check("Command line: a linger longer than the wait gives exit code 2", longer.ExitCode == 2 && longer.Output.Contains("longer than the wait"), longer.Output);
        }
    }
}
