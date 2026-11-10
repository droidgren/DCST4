using DCST4.Core.Network;
using DCST4.Core.TestData;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>One instance to ask about.</summary>
    internal sealed class CommitmentTarget
    {
        public string ObjectType;
        public string SopClassUid;
        public string SopInstanceUid;
        public bool NeverStored;
    }

    /// <summary>How far one request got, for the test to judge.</summary>
    internal sealed class CommitmentRun
    {
        public TestResult Result;
        /// <summary>The test already has its result class, from something before the report.</summary>
        public bool Concluded;
        /// <summary>The report that came in time, or null.</summary>
        public CommitmentReport Report;
        public List<string> Problems = new List<string>();
    }

    /// <summary>Request Storage Commitment, wait for the report on either association, and compare it with what was asked.</summary>
    internal static class CommitmentSteps
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(CommitmentSteps));

        public static async Task<CommitmentRun> RunAsync(TestContext context, IReadOnlyList<CommitmentTarget> targets, string purpose, CancellationToken token)
        {
            var options = context.Session.Options;
            var profile = context.Profile;
            var result = new TestResult();
            var run = new CommitmentRun { Result = result };
            var timelines = new TimelineCollector();
            var tracker = new CommitmentTracker(timelines.Clock);

            var transactionUid = new DeterministicUidSource(profile.UidRoot, context.Session.SeedFor(purpose).ToString(CultureInfo.InvariantCulture)).Uid("transaction");
            var record = new CommitmentResult
            {
                TransactionUid = transactionUid,
                LingerSeconds = options.CommitLingerSeconds,
                WaitSeconds = options.CommitWaitSeconds,
                GraceSeconds = options.CommitGraceSeconds,
            };
            result.Commitment = record;
            result.Uids["transactionUid"] = transactionUid;
            result.Details["lingerSeconds"] = Fixtures.Int(options.CommitLingerSeconds);
            result.Details["waitSeconds"] = Fixtures.Int(options.CommitWaitSeconds);
            result.Details["graceSeconds"] = Fixtures.Int(options.CommitGraceSeconds);
            result.Details["localScpAe"] = profile.LocalScpAe;
            result.Details["localScpPort"] = Fixtures.Int(profile.LocalScpPort);
            result.Details["instances"] = Fixtures.Int(targets.Count);

            var request = new CommitmentRequest
            {
                TransactionUid = transactionUid,
                Instances = targets.Select(t => new ReferencedInstance { SopClassUid = t.SopClassUid, SopInstanceUid = t.SopInstanceUid }).ToList(),
            };

            var listener = RetrieveSteps.TryListen(profile, context.Session.NewFolder("commitment-" + purpose), null, out var problem, "Storage Commitment reports", tracker, timelines.Clock);
            if (listener == null)
            {
                Conclude(run, TestResultClass.TOOL_ERROR, problem);
                return run;
            }

            using (listener)
            {
                // Before the N-ACTION, since the report may come before its response.
                var waiter = tracker.Register(transactionUid);
                var late = new TaskCompletionSource<CommitmentReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                tracker.ReportReceived += report =>
                {
                    if (report.Late && report.TransactionUid == transactionUid) late.TrySetResult(report);
                };

                context.Report(options.CommitLingerSeconds > 0
                    ? $"N-ACTION sent. The association is kept open for {Fixtures.Int(options.CommitLingerSeconds)} s so that the report can come on it"
                    : "Sending the N-ACTION");
                var timeline = timelines.Next();
                var outcome = await StorageCommitmentService.RequestAsync(profile.Endpoint, profile.Network, request, options.CommitLingerSeconds, tracker, token, timeline).ConfigureAwait(false);
                timelines.Collect(timeline);

                Fixtures.AddContexts(result, outcome.Exchange.Contexts);
                if (outcome.ActionStatus.HasValue)
                {
                    Fixtures.AddStatus(result, "N-ACTION", outcome.ActionStatus.Value);
                    record.ActionStatus = AssociationTimeline.FormatStatus(outcome.ActionStatus.Value);
                }

                if (!ConcludeAction(run, outcome, waiter))
                {
                    context.Report($"Waiting for the Storage Commitment report, for up to {Fixtures.Int(options.CommitWaitSeconds)} s after the N-ACTION response");
                    var report = await WaitAsync(waiter, Remaining(tracker, outcome, options.CommitWaitSeconds), token).ConfigureAwait(false);
                    if (report != null)
                    {
                        run.Report = report;
                        Describe(record, report, outcome);
                    }
                    else if (token.IsCancellationRequested)
                    {
                        Conclude(run, TestResultClass.INDETERMINATE, "Cancelled while waiting for the Storage Commitment report.");
                    }
                    else
                    {
                        tracker.Expire(transactionUid);
                        context.Report($"No report in time. Listening {Fixtures.Int(options.CommitGraceSeconds)} s more to see whether it comes late");
                        var lateReport = await WaitAsync(late.Task, options.CommitGraceSeconds * 1000L, token).ConfigureAwait(false);
                        if (lateReport != null)
                        {
                            Describe(record, lateReport, outcome);
                            record.ReportVia = null;
                            record.Late = true;
                            record.LateVia = lateReport.Via.ToString();
                            record.Instances = Compare(targets, lateReport, record, new List<string>());
                        }
                        Conclude(run, token.IsCancellationRequested ? TestResultClass.INDETERMINATE : TestResultClass.TIMEOUT, TimeoutExplanation(context, lateReport, listener, token));
                    }
                }

                await listener.WaitForIncomingToEndAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                foreach (var incoming in listener.IncomingTimelines) timelines.Collect(incoming);
                record.RejectedReports = tracker.Rejected.Select(r => new RejectedCommitmentReport
                {
                    Via = r.Via.ToString(),
                    CallingAe = r.CallingAe,
                    TransactionUid = r.TransactionUid,
                    EventTypeId = r.EventTypeId,
                    AnsweredStatus = AssociationTimeline.FormatStatus(r.AnsweredStatus),
                    Problem = r.Problem,
                }).ToList();
                record.RepeatedReports = tracker.Repeated.Count;
                result.Details["rejectedReports"] = Fixtures.Int(record.RejectedReports.Count);
                result.Details["incomingAssociations"] = Fixtures.Int(listener.Associations.Count);
            }

            if (run.Report != null)
            {
                record.Instances = Compare(targets, run.Report, record, run.Problems);
                result.Details["reportVia"] = record.ReportVia;
                result.Details["eventType"] = Fixtures.Int(run.Report.EventTypeId);
                result.Details["committed"] = Fixtures.Int(record.Instances.Count(i => i.Outcome == CommitmentInstanceOutcome.Committed));
                result.Details["failed"] = Fixtures.Int(record.Instances.Count(i => i.Outcome == CommitmentInstanceOutcome.Failed));
            }

            result.Timeline = timelines.Events;
            return run;
        }

        /// <summary>True if the test ends here, before any wait for the report.</summary>
        private static bool ConcludeAction(CommitmentRun run, CommitmentOutcome outcome, Task<CommitmentReport> waiter)
        {
            var exchange = outcome.Exchange;
            var resultClass = Fixtures.ClassOf(exchange);
            var accepted = outcome.ActionStatus == 0x0000;

            if (resultClass == TestResultClass.NOT_SUPPORTED)
            {
                Conclude(run, resultClass, "The Storage Commitment Push Model SOP class was not accepted, so no N-ACTION was sent.");
                return true;
            }
            // An association that broke after a successful N-ACTION does not stop the report from coming on another one.
            if (resultClass != TestResultClass.PASS && !(accepted && resultClass != TestResultClass.INDETERMINATE))
            {
                var what = outcome.ActionStatus.HasValue ? "After the N-ACTION-RSP: " : "No N-ACTION-RSP: ";
                Conclude(run, resultClass, what + (exchange.Message ?? "the request was cancelled."));
                return true;
            }
            if (resultClass != TestResultClass.PASS) run.Result.Details["associationEnding"] = exchange.Message ?? string.Empty;

            if (!outcome.ActionStatus.HasValue)
            {
                Conclude(run, TestResultClass.FAIL, "The association ended without an N-ACTION-RSP.");
                return true;
            }
            if (!accepted)
            {
                var status = outcome.ActionStatus.Value;
                var text = $"The N-ACTION was refused with {AssociationTimeline.FormatStatus(status)} ({DimseStatusText.Describe(status)})";
                if (!string.IsNullOrEmpty(outcome.ErrorComment)) text += ", comment: " + outcome.ErrorComment;
                var unsupported = status == 0x0123 || status == 0x0211;
                Conclude(run, unsupported ? TestResultClass.NOT_SUPPORTED : TestResultClass.FAIL, text + ".");
                return true;
            }
            return false;
        }

        private static long Remaining(CommitmentTracker tracker, CommitmentOutcome outcome, int waitSeconds)
        {
            var from = outcome.ResponseAtMs ?? tracker.NowMs;
            return Math.Max(0, from + waitSeconds * 1000L - tracker.NowMs);
        }

        /// <summary>The result, or null if it did not come in time or the token was cancelled.</summary>
        private static async Task<T> WaitAsync<T>(Task<T> task, long milliseconds, CancellationToken token) where T : class
        {
            if (task.IsCompleted) return task.Result;
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var delay = Task.Delay(TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue)), stop.Token);
                var first = await Task.WhenAny(task, delay).ConfigureAwait(false);
                stop.Cancel();
                return first == task ? task.Result : null;
            }
        }

        private static void Describe(CommitmentResult record, CommitmentReport report, CommitmentOutcome outcome)
        {
            record.ReportVia = report.Via.ToString();
            record.ReportCallingAe = report.CallingAe;
            record.EventTypeId = report.EventTypeId;
            if (outcome.SentAtMs.HasValue) record.ReportAfterMs = Math.Max(0, report.ReceivedAtMs - outcome.SentAtMs.Value);
        }

        private static string TimeoutExplanation(TestContext context, CommitmentReport late, LocalStorageServer listener, CancellationToken token)
        {
            var options = context.Session.Options;
            if (token.IsCancellationRequested) return "Cancelled while listening for a late Storage Commitment report.";

            var text = $"No Storage Commitment report came within {Fixtures.Int(options.CommitWaitSeconds)} s of the N-ACTION-RSP.";
            if (late != null)
            {
                text += $" It came later, {Where(late.Via)}, within the {Fixtures.Int(options.CommitGraceSeconds)} s DCST4 went on listening.";
            }
            else
            {
                text += $" It did not come in a further {Fixtures.Int(options.CommitGraceSeconds)} s either.";
                if (options.CommitLingerSeconds == 0) text += " The association was released at once, so the report could only come on a new association.";
                if (listener.Associations.Count == 0)
                {
                    text += $" No association reached {context.Profile.LocalScpAe} at port {Fixtures.Int(context.Profile.LocalScpPort)}. The system must know that AE title with this computer's address and port, and Windows Defender Firewall must allow incoming TCP on the port.";
                }
            }
            return text;
        }

        public static string Where(CommitmentVia via) => via == CommitmentVia.SameAssociation
            ? "on the association of the N-ACTION"
            : "on an association the system opened to DCST4";

        /// <summary>Per instance asked for, what the report said. Anything the standard does not allow goes into <paramref name="problems"/>.</summary>
        public static List<CommitmentInstanceResult> Compare(IReadOnlyList<CommitmentTarget> targets, CommitmentReport report, CommitmentResult record, List<string> problems)
        {
            var committed = report.Committed.GroupBy(i => i.SopInstanceUid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var failed = report.Failed.GroupBy(i => i.SopInstanceUid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var results = new List<CommitmentInstanceResult>();

            foreach (var target in targets)
            {
                committed.TryGetValue(target.SopInstanceUid, out var asCommitted);
                failed.TryGetValue(target.SopInstanceUid, out var asFailed);
                var times = (asCommitted?.Count ?? 0) + (asFailed?.Count ?? 0);

                var item = new CommitmentInstanceResult
                {
                    ObjectType = target.ObjectType ?? string.Empty,
                    SopClassUid = target.SopClassUid,
                    SopInstanceUid = target.SopInstanceUid,
                    NeverStored = target.NeverStored,
                };

                if (times == 0)
                {
                    item.Outcome = CommitmentInstanceOutcome.NotReported;
                    problems.Add($"The report does not mention {Name(target)}, which must be in one of its two lists.");
                }
                else if (times > 1)
                {
                    item.Outcome = CommitmentInstanceOutcome.ListedTwice;
                    problems.Add($"The report lists {Name(target)} {Fixtures.Int(times)} times.");
                }
                else if (asCommitted != null)
                {
                    item.Outcome = CommitmentInstanceOutcome.Committed;
                    if (asCommitted[0].SopClassUid != target.SopClassUid) problems.Add($"The report gives {Name(target)} SOP Class UID {asCommitted[0].SopClassUid}, not {target.SopClassUid}.");
                }
                else
                {
                    var failure = asFailed[0];
                    item.Outcome = CommitmentInstanceOutcome.Failed;
                    if (failure.FailureReason.HasValue)
                    {
                        item.FailureReason = AssociationTimeline.FormatStatus(failure.FailureReason.Value);
                        item.FailureReasonText = CommitmentStatus.Describe(failure.FailureReason);
                    }
                    else
                    {
                        problems.Add($"The failure of {Name(target)} has no Failure Reason (0008,1197), which is required.");
                    }
                }
                results.Add(item);
            }

            var asked = new HashSet<string>(targets.Select(t => t.SopInstanceUid), StringComparer.Ordinal);
            record.UnrequestedInstances = committed.Keys.Concat(failed.Keys).Where(uid => !asked.Contains(uid)).Distinct().OrderBy(uid => uid, StringComparer.Ordinal).ToList();
            if (record.UnrequestedInstances.Count > 0) problems.Add($"The report names {Fixtures.Int(record.UnrequestedInstances.Count)} instances that were not asked for, such as {record.UnrequestedInstances[0]}.");

            if (report.EventTypeId == CommitmentReport.AllSucceeded && report.Failed.Count > 0) problems.Add("Event Type 1 says all succeeded, but the report has a Failed SOP Sequence with items.");
            if (report.EventTypeId == CommitmentReport.FailuresExist && report.Failed.Count == 0) problems.Add("Event Type 2 says there are failures, but the report has no Failed SOP Sequence items.");
            return results;
        }

        public static string Name(CommitmentTarget target) => target.NeverStored
            ? $"the instance that was never stored ({target.SopInstanceUid})"
            : $"{target.ObjectType} instance {target.SopInstanceUid}";

        public static string Reason(CommitmentInstanceResult item) =>
            item.FailureReason == null ? "no Failure Reason" : $"{item.FailureReason} ({item.FailureReasonText})";

        public static void Conclude(CommitmentRun run, TestResultClass resultClass, string explanation)
        {
            run.Concluded = true;
            Fixtures.Set(run.Result, resultClass, explanation);
            Logger.Information("Storage Commitment: {result}: {explanation}", resultClass.ToString(), explanation);
        }

        public static void AddFinding(TestResult result, IReadOnlyList<string> problems)
        {
            if (problems.Count > 0) result.Details["finding"] = problems[0];
        }
    }

    /// <summary>Storage Commitment for everything the store tests of this run stored. The Committed step of the chain.</summary>
    public sealed class CommitStoredTest : ITestCase
    {
        public string Id => "commit-stored";
        public string Name => "Storage Commitment: every stored instance";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var records = context.Session.Objects.Where(o => o.Stored.Any()).ToList();
            if (records.Count == 0) return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = RetrieveSteps.NothingStored };

            var targets = records.SelectMany(r => r.Stored.Select(i => new CommitmentTarget
            {
                ObjectType = r.ObjectType,
                SopClassUid = i.Manifest.SopClassUid,
                SopInstanceUid = i.Manifest.SopInstanceUid,
            })).ToList();

            var run = await CommitmentSteps.RunAsync(context, targets, "commit-stored", token).ConfigureAwait(false);
            var result = run.Result;

            if (run.Concluded)
            {
                foreach (var record in records) record.Committed = ChainStep.Of(result.Result, result.Explanation);
                return result;
            }

            var instances = result.Commitment.Instances;
            foreach (var record in records)
            {
                record.Committed = Step(instances.Where(i => i.ObjectType == record.ObjectType).ToList(), run);
            }

            CommitmentSteps.AddFinding(result, run.Problems);
            var where = CommitmentSteps.Where(run.Report.Via);
            var failures = instances.Where(i => i.Outcome == CommitmentInstanceOutcome.Failed).ToList();
            if (run.Problems.Count > 0)
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"The report came {where} but does not follow the standard: {run.Problems[0]}");
            }
            else if (failures.Count > 0)
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"{Fixtures.Int(failures.Count)} of {Fixtures.Int(instances.Count)} stored instances were not committed, Event Type {Fixtures.Int(run.Report.EventTypeId)}, {where}. {failures[0].ObjectType}: {CommitmentSteps.Reason(failures[0])}.");
            }
            else
            {
                Fixtures.Set(result, TestResultClass.PASS, $"All {Fixtures.Int(instances.Count)} stored instances were committed, Event Type {Fixtures.Int(run.Report.EventTypeId)}, {where}.");
            }
            return result;
        }

        private static ChainStep Step(List<CommitmentInstanceResult> instances, CommitmentRun run)
        {
            var where = CommitmentSteps.Where(run.Report.Via);
            var failed = instances.Where(i => i.Outcome == CommitmentInstanceOutcome.Failed).ToList();
            if (failed.Count > 0)
            {
                return ChainStep.Of(TestResultClass.FAIL, $"{Fixtures.Int(failed.Count)} of {Fixtures.Int(instances.Count)} instances were not committed: {string.Join(", ", failed.Select(CommitmentSteps.Reason).Distinct())}.");
            }
            var wrong = instances.FirstOrDefault(i => i.Outcome != CommitmentInstanceOutcome.Committed);
            if (wrong != null)
            {
                return ChainStep.Of(TestResultClass.FAIL, wrong.Outcome == CommitmentInstanceOutcome.NotReported
                    ? $"The report does not mention instance {wrong.SopInstanceUid}."
                    : $"The report lists instance {wrong.SopInstanceUid} more than once.");
            }
            return ChainStep.Of(TestResultClass.PASS, $"{Fixtures.Int(instances.Count)} of {Fixtures.Int(instances.Count)} instances were committed, {where}.");
        }
    }

    /// <summary>Storage Commitment that includes an instance never stored. The system must refuse that one and commit the rest.</summary>
    public sealed class CommitUnknownInstanceTest : ITestCase
    {
        public string Id => "commit-unknown-instance";
        public string Name => "Storage Commitment: an instance that was never stored";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var record = context.Session.Objects.Where(o => o.Stored.Any()).OrderBy(o => o.Stored.Count()).FirstOrDefault();
            if (record == null) return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = RetrieveSteps.NothingStored };

            var targets = record.Stored.Select(i => new CommitmentTarget
            {
                ObjectType = record.ObjectType,
                SopClassUid = i.Manifest.SopClassUid,
                SopInstanceUid = i.Manifest.SopInstanceUid,
            }).ToList();

            var unknownUid = new DeterministicUidSource(context.Profile.UidRoot, context.Session.SeedFor(Id).ToString(CultureInfo.InvariantCulture)).Uid("unknown-instance");
            var unknown = new CommitmentTarget { SopClassUid = targets[0].SopClassUid, SopInstanceUid = unknownUid, NeverStored = true };
            targets.Add(unknown);

            var run = await CommitmentSteps.RunAsync(context, targets, Id, token).ConfigureAwait(false);
            var result = run.Result;
            result.Uids["unknownSopInstanceUid"] = unknownUid;
            if (run.Concluded) return result;

            var instances = result.Commitment.Instances;
            var verdict = instances.Single(i => i.NeverStored);
            var stored = instances.Where(i => !i.NeverStored).ToList();
            var where = CommitmentSteps.Where(run.Report.Via);

            if (verdict.Outcome == CommitmentInstanceOutcome.Committed)
            {
                result.Details["finding"] = "The system reports as committed an instance it never received.";
                Fixtures.Set(result, TestResultClass.FAIL, $"The report, {where}, says that the instance that was never stored is committed. A system that commits to what it does not hold cannot be relied on for Storage Commitment.");
                return result;
            }

            CommitmentSteps.AddFinding(result, run.Problems);
            if (run.Problems.Count > 0)
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"The report came {where} but does not follow the standard: {run.Problems[0]}");
                return result;
            }

            var notCommitted = stored.FirstOrDefault(i => i.Outcome != CommitmentInstanceOutcome.Committed);
            if (notCommitted != null)
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"The instance that was never stored was refused with {CommitmentSteps.Reason(verdict)}, but stored instance {notCommitted.SopInstanceUid} of {record.ObjectType} was not committed either: {CommitmentSteps.Reason(notCommitted)}.");
                return result;
            }

            Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"The instance that was never stored was refused with {CommitmentSteps.Reason(verdict)}, and the {Fixtures.Int(stored.Count)} stored instances of {record.ObjectType} were committed. Event Type {Fixtures.Int(run.Report.EventTypeId)}, {where}.");
            return result;
        }
    }
}
