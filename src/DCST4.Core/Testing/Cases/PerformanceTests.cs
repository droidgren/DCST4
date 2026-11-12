using DCST4.Core.Performance;
using FellowOakDicom;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>
    /// What the three load tests share: the settings, the pool, the series file, the client settings and how a run maps to a
    /// result class. The measured numbers go in <see cref="TestResult.Performance"/>, never in the explanation.
    /// </summary>
    public abstract class LoadTestBase : ITestCase
    {
        private static readonly ILogger Logger = Log.ForContext<LoadTestBase>();

        public abstract string Id { get; }
        public abstract string Name { get; }
        public TestCategory Category => TestCategory.Performance;
        public abstract string Description { get; }
        public string Group => TestGroups.Performance;
        public bool StoresObjects => true;
        public StorageEstimate Storage => null;
        public abstract LoadMode Mode { get; }

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult();
            var settings = (context.Session.Options.Load ?? new LoadSettings()).Clone();
            var problem = settings.Validate(Mode);
            if (problem != null)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, "The load settings cannot be used, which is a local problem: " + problem);
                return result;
            }

            var mix = settings.ResolvedMix;
            var target = RawSupport.Target(context);
            var performance = new PerformanceResult
            {
                Mode = Mode,
                Settings = settings,
                Mix = mix.Id,
                MixDescription = mix.Description,
                TransferSyntaxUid = mix.Syntax.UID.UID,
                MaxPdu = settings.ResolvedMaxPdu,
                PlannedInstances = settings.PlannedInstances(Mode),
                PlannedMegabytes = Math.Round(settings.PlannedMegabytes(Mode), 1),
            };
            result.Performance = performance;

            InstancePool pool;
            try
            {
                context.Report($"Generating and encoding the {mix.Id} pool before anything is sent");
                pool = await Task.Run(() => InstancePool.Build(mix, context.Session.SeedFor("load-pool/" + mix.Id), context.Profile.UidRoot, context.PatientNaming, token), token).ConfigureAwait(false);
            }
            catch (PoolUnavailableException ex)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, ex.Message + " This is a local problem, not a fault of " + target + ".");
                return result;
            }

            performance.PoolInstances = pool.Instances.Count;
            performance.AverageObjectBytes = pool.AverageWireBytes;
            var header = pool.Instances[0].Header;
            result.Details["patientId"] = header.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
            result.Details["patientName"] = header.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
            result.Details["mix"] = mix.Id;

            var run = new LoadRun
            {
                Context = context,
                Settings = settings,
                Pool = pool,
                Result = result,
                Performance = performance,
                Nonce = InstancePool.NewRunNonce(),
            };
            result.Details["runNonce"] = run.Nonce;

            Logger.Information("Load test {id} against {target}: mix {mix}, {planned} instances at most", Id, target, mix.Id, performance.PlannedInstances);
            try
            {
                run.Series = OpenFile(context, Id, MetricsFileWriter.SeriesColumns, "");
                performance.SeriesFile = run.Series?.Path;
                using (run.Tuning = new ClientTuning(MaxAssociations(settings)))
                {
                    await RunAsync(run, token).ConfigureAwait(false);
                    performance.Client = run.Tuning.Health();
                }
            }
            catch (IOException ex)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, "DCST4 could not write the series file, which is a local problem: " + ex.Message);
                return result;
            }
            finally
            {
                run.Series?.Dispose();
                run.Windows?.Dispose();
            }

            foreach (var status in run.Statuses) Fixtures.AddStatus(result, "C-STORE", status);
            Fixtures.AddContexts(result, run.Contexts);
            if (run.FirstStudy != null) result.Uids["firstStudyInstanceUid"] = run.FirstStudy;
            Logger.Information("Load test {id}: {result}", Id, result.Result.ToString());
            return result;
        }

        protected abstract Task RunAsync(LoadRun run, CancellationToken token);

        internal static string Associations(int count) => Fixtures.Int(count) + (count == 1 ? " association" : " associations");

        protected virtual int MaxAssociations(LoadSettings settings) => settings.Associations;

        /// <summary>A file named after the run, next to where its result files go. Null if the run has no such folder.</summary>
        internal static MetricsFileWriter OpenFile(TestContext context, string testId, string[] columns, string suffix)
        {
            var folder = context.Session.Options.MetricsFolder;
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var stem = Path.Combine(folder, "DCST4-run-" + context.Session.RunStartedUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + testId + suffix);
            var path = stem + ".csv";
            for (var n = 2; File.Exists(path); n++) path = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".csv";
            return new MetricsFileWriter(path, columns);
        }

        /// <summary>One engine run with what every mode needs.</summary>
        internal static LoadRunOptions EngineOptions(LoadRun run, int associations, double durationSeconds, int budget, double rate, int step, int workerOffset, int secondOffset)
        {
            var context = run.Context;
            return new LoadRunOptions
            {
                Endpoint = context.Profile.Endpoint,
                Network = context.Profile.Network,
                Pool = run.Pool,
                UidRoot = context.Profile.UidRoot,
                RunNonce = run.Nonce,
                Associations = associations,
                InstancesPerAssociation = run.Settings.InstancesPerAssociation,
                DurationSeconds = durationSeconds,
                Budget = budget,
                RatePerSecond = rate,
                MaxPdu = run.Settings.ResolvedMaxPdu,
                WarmUpSeconds = run.Settings.WarmUpSeconds,
                Step = step,
                WorkerOffset = workerOffset,
                SecondOffset = secondOffset,
                OnSecond = second =>
                {
                    run.Series?.WriteSecond(second);
                    if (second.Second % 10 == 9) run.Series?.Flush(false);
                    run.Tuning?.AddCpu(second.ClientCpuPercent);
                    context.ReportMetrics(second);
                },
                OnStored = (instance, arrived) =>
                {
                    if (run.FirstStudy == null) run.FirstStudy = instance.StudyInstanceUid;
                    run.Poller?.Offer(instance, arrived);
                },
            };
        }

        /// <summary>Records what an engine run negotiated and answered, for the result's status codes and contexts.</summary>
        internal static void Absorb(LoadRun run, LoadRunOutcome outcome)
        {
            foreach (var status in outcome.Statuses) run.Statuses.Add(status);
            if (outcome.FirstProblem != null && !run.Result.Details.ContainsKey("firstProblem")) run.Result.Details["firstProblem"] = outcome.FirstProblem;
            if (run.Contexts.Count == 0) run.Contexts.AddRange(outcome.Contexts);
            run.Performance.StoppedBy = outcome.StoppedBy;
        }

        /// <summary>
        /// The result class of a run that is over, for what all modes share: cancel, a stop before any measurement, and
        /// failures. Returns false when the mode's own verdict decides.
        /// </summary>
        internal static bool ConcludeCommon(LoadRun run, LoadRunOutcome outcome, string target, bool errorsAreFindings)
        {
            var result = run.Result;
            if (outcome.Cancelled)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled. What was measured until then is in the performance section, and the associations were released or aborted.");
                return true;
            }
            switch (outcome.Fatal)
            {
                case LoadFatal.NotSupported:
                    Fixtures.Set(result, TestResultClass.NOT_SUPPORTED, outcome.FatalMessage);
                    return true;
                case LoadFatal.ToolError:
                    Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {outcome.FatalMessage}");
                    return true;
                case LoadFatal.Timeout:
                    Fixtures.Set(result, TestResultClass.TIMEOUT, $"{target} did not answer: {outcome.FatalMessage}");
                    return true;
                case LoadFatal.ConnectFailed:
                    Fixtures.Set(result, TestResultClass.FAIL, $"DCST4 could not open an association with {target}: {outcome.FatalMessage}");
                    return true;
            }

            var errors = run.Performance.Summary?.Errors ?? outcome.Summary.Errors;
            if (outcome.Summary.Attempts > 0 && errors.NoResponse == outcome.Summary.Attempts)
            {
                Fixtures.Set(result, TestResultClass.TIMEOUT, $"{target} answered none of the C-STOREs within the response timeout.");
                return true;
            }
            if (errorsAreFindings && errors.Problems > 0)
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"Under load, {target} {ProblemWords(errors)}. The counts per class and raw status are in the performance section.");
                return true;
            }
            return false;
        }

        internal static string ProblemWords(StatusClassCounts errors)
        {
            var parts = new List<string>();
            if (errors.Failure + errors.Cancel > 0) parts.Add("refused C-STOREs with a failure status");
            if (errors.NoResponse > 0) parts.Add("left C-STOREs without a response");
            if (errors.AssociationLost > 0) parts.Add("ended associations while a C-STORE was outstanding");
            if (errors.AssociationRejected > 0) parts.Add("rejected associations");
            if (errors.ConnectFailed > 0) parts.Add("did not accept or answer some connections");
            return parts.Count == 0 ? "had problems" : string.Join(", ", parts.Take(parts.Count - 1)) + (parts.Count > 1 ? " and " : string.Empty) + parts.Last();
        }

        /// <summary>Starts the time-to-available poller if the settings ask for one.</summary>
        internal static Task<TimeToAvailable> StartPoller(LoadRun run, Task loadDone, CancellationToken token)
        {
            if (run.Settings.SampleEvery <= 0) return null;
            run.Poller = new AvailabilityPoller(run.Context.Profile.Endpoint, run.Context.Profile.Network, run.Settings.SampleEvery, run.Settings.PollIntervalSeconds, run.Settings.PollTimeoutSeconds);
            return Task.Run(() => run.Poller.RunAsync(loadDone, token));
        }

        internal static async Task FinishPoller(LoadRun run, Task<TimeToAvailable> poller)
        {
            if (poller == null) return;
            if (run.Settings.SampleEvery > 0) run.Context.Report($"Waiting up to {Fixtures.Int((int)Math.Ceiling(run.Settings.PollTimeoutSeconds))} s for the sampled instances to be found with C-FIND");
            run.Performance.TimeToAvailable = await poller.ConfigureAwait(false);
        }
    }

    /// <summary>The state of one load test while it runs.</summary>
    public sealed class LoadRun
    {
        internal TestContext Context;
        internal LoadSettings Settings;
        internal InstancePool Pool;
        internal TestResult Result;
        internal PerformanceResult Performance;
        internal string Nonce;
        internal MetricsFileWriter Series;
        internal MetricsFileWriter Windows;
        internal ClientTuning Tuning;
        internal AvailabilityPoller Poller;
        internal volatile string FirstStudy;
        internal readonly SortedSet<ushort> Statuses = new SortedSet<ushort>();
        internal readonly List<Network.NegotiatedContext> Contexts = new List<Network.NegotiatedContext>();
    }

    /// <summary>N associations at once, for a duration or a number of instances, optionally at a fixed rate.</summary>
    public sealed class LoadThroughputTest : LoadTestBase
    {
        public override string Id => "load-throughput";
        public override string Name => "Load: throughput and latency";
        public override string Description => "Stores many new objects on several associations at once, for a duration or a count, and measures throughput and latency.";
        public override LoadMode Mode => LoadMode.Load;

        protected override async Task RunAsync(LoadRun run, CancellationToken token)
        {
            var s = run.Settings;
            var target = RawSupport.Target(run.Context);
            var budget = s.PlannedInstances(LoadMode.Load);
            var duration = s.InstanceCount > 0 ? 0 : s.DurationSeconds;
            run.Context.Report(duration > 0
                ? $"Sending on {Associations(s.Associations)} for {Number(duration)} s, at most {Fixtures.Int(budget)} instances"
                : $"Sending {Fixtures.Int(budget)} instances on {Associations(s.Associations)}");

            var loadDone = new TaskCompletionSource<bool>();
            var poller = StartPoller(run, loadDone.Task, token);
            var engine = new LoadEngine(EngineOptions(run, s.Associations, duration, budget, s.RatePerSecond, 0, 0, 0));
            LoadRunOutcome outcome;
            try
            {
                outcome = await engine.RunAsync(token).ConfigureAwait(false);
            }
            finally
            {
                loadDone.TrySetResult(true);
            }
            await FinishPoller(run, poller).ConfigureAwait(false);

            Absorb(run, outcome);
            run.Performance.Summary = outcome.Summary;
            run.Performance.Seconds = outcome.Seconds;
            run.Performance.EndedByCeiling = duration > 0 && outcome.StoppedBy == "count";

            if (!ConcludeCommon(run, outcome, target, true))
            {
                Fixtures.Set(run.Result, TestResultClass.PASS,
                    $"Every C-STORE on {Associations(s.Associations)} was stored{(s.RatePerSecond > 0 ? " at the set rate" : string.Empty)}. Throughput, latency and association setup are in the performance section.");
            }

            // A fast system can use up the ceiling long before the duration, and then the summary covers a short window.
            if (run.Performance.EndedByCeiling)
            {
                var stop = run.Performance.DescribeStop();
                run.Result.Explanation += " Note: " + char.ToLowerInvariant(stop[0]) + stop.Substring(1) + " Raise Max instances (--load-max-instances), shorten the duration or set a rate.";
            }
        }

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>1, 2, 4, 8 ... associations in turn, to find where more associations stop helping.</summary>
    public sealed class LoadRampTest : LoadTestBase
    {
        public override string Id => "load-ramp";
        public override string Name => "Load: ramp to saturation";
        public override string Description => "Stores many new objects on 1, 2, 4, 8 ... associations in turn, to find where more associations stop helping.";
        public override LoadMode Mode => LoadMode.Ramp;

        protected override int MaxAssociations(LoadSettings settings) => settings.RampSteps.Max();

        protected override async Task RunAsync(LoadRun run, CancellationToken token)
        {
            var s = run.Settings;
            var target = RawSupport.Target(run.Context);
            var budget = s.ResolvedMaxInstances(LoadMode.Ramp);
            var steps = new List<RampStep>();
            run.Performance.Steps = steps;
            var workerOffset = 0;
            var used = 0L;
            LoadRunOutcome last = null;
            var stepErrors = new List<StatusClassCounts>();

            for (var k = 0; k < s.RampSteps.Count; k++)
            {
                var associations = s.RampSteps[k];
                var left = (int)Math.Max(0, budget - used);
                if (left == 0)
                {
                    run.Performance.StoppedBy = "count";
                    break;
                }

                run.Context.Report($"Ramp step {Fixtures.Int(k + 1)} of {Fixtures.Int(s.RampSteps.Count)}: {Associations(associations)} for {s.RampStepSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s");
                // The steps follow each other in the series, without the moment between them when associations are released and opened.
                var secondOffset = run.Performance.Seconds;
                var engine = new LoadEngine(EngineOptions(run, associations, s.RampStepSeconds, left, 0, k + 1, workerOffset, secondOffset));
                last = await engine.RunAsync(token).ConfigureAwait(false);
                workerOffset += associations;
                used += last.Summary.Attempts;
                Absorb(run, last);
                run.Performance.Seconds = secondOffset + last.Seconds;

                steps.Add(new RampStep { Step = k + 1, Associations = associations, Summary = last.Summary });
                stepErrors.Add(last.Summary.Errors);
                if (last.Cancelled || last.Fatal != LoadFatal.None) break;
                if (last.StoppedBy == "count" && k < s.RampSteps.Count - 1)
                {
                    run.Performance.StoppedBy = "count";
                    break;
                }
            }

            if (last == null) return;
            if (last.Fatal == LoadFatal.None && !last.Cancelled) run.Performance.StoppedBy = run.Performance.StoppedBy == "count" ? "count" : "duration";

            var measured = steps.Where(st => st.Summary.Stored > 0 || st.Summary.Attempts > 0).ToList();
            var verdict = SaturationDetector.Detect(measured, s.RampMinGainPercent, s.RampLatencyFactor, s.RampErrorRisePoints);
            run.Performance.Saturation = verdict;
            var best = steps.FirstOrDefault(st => st.Step == verdict.BestStep);
            run.Performance.Summary = best?.Summary ?? last.Summary;

            if (ConcludeCommon(run, last, target, false)) return;

            // Errors at or after the saturation step are what the ramp is there to find. Before it they are a fault.
            var limit = verdict.Found ? verdict.Step - 1 : steps.Count;
            var early = steps.Take(limit).Where(st => st.Summary.Errors.Problems > 0).ToList();
            if (early.Count > 0)
            {
                Fixtures.Set(run.Result, TestResultClass.FAIL, $"{target} {ProblemWords(early[0].Summary.Errors)} before the ramp reached saturation. The counts per step are in the performance section.");
                return;
            }

            var stopped = run.Performance.StoppedBy == "count" ? " The ramp stopped early at the instance ceiling." : string.Empty;
            Fixtures.Set(run.Result, TestResultClass.PASS, (verdict.Found
                ? "The ramp found where more associations stop helping."
                : "Throughput still rose with every step of the ramp.") + stopped + " The step, its reason and every step's throughput and latency are in the performance section.");
        }
    }

    /// <summary>Steady load for a long time, with snapshots written as it goes, to see whether the system gets worse.</summary>
    public sealed class LoadSoakTest : LoadTestBase
    {
        public override string Id => "load-soak";
        public override string Name => "Load: soak";
        public override string Description => "Stores new objects at a steady rate for a long time, with snapshots as it goes, to see whether the destination gets slower.";
        public override LoadMode Mode => LoadMode.Soak;

        protected override async Task RunAsync(LoadRun run, CancellationToken token)
        {
            var s = run.Settings;
            var target = RawSupport.Target(run.Context);
            var budget = s.PlannedInstances(LoadMode.Soak);
            run.Windows = OpenFile(run.Context, Id, MetricsFileWriter.WindowColumns, "-windows");
            run.Performance.WindowsFile = run.Windows?.Path;
            run.Context.Report($"Soak: {Associations(s.Associations)} at {s.SoakRatePerSecond.ToString("0.##", CultureInfo.InvariantCulture)} instances/s for {Duration(s.SoakSeconds)}, a snapshot every {Duration(s.SnapshotSeconds)}");

            var windows = new List<SoakWindow>();
            var options = EngineOptions(run, s.Associations, s.SoakSeconds, budget, s.SoakRatePerSecond, 0, 0, 0);
            options.SnapshotSeconds = s.SnapshotSeconds;
            options.OnWindow = window =>
            {
                lock (windows) windows.Add(window);
                run.Windows?.WriteWindow(window);
                run.Windows?.Flush(true);
                run.Series?.Flush(true);
                List<SoakWindow> copy;
                lock (windows) copy = windows.ToList();
                var sofar = DegradationDetector.Detect(copy, s.SoakLatencyRisePercent, s.SoakThroughputDropPercent);
                run.Context.Report($"Soak snapshot {Fixtures.Int(window.Window)} written{(sofar.Degraded ? ". Degradation so far: " + string.Join(", ", sofar.Reasons) : string.Empty)}");
            };

            var loadDone = new TaskCompletionSource<bool>();
            var poller = StartPoller(run, loadDone.Task, token);
            LoadRunOutcome outcome;
            try
            {
                outcome = await new LoadEngine(options).RunAsync(token).ConfigureAwait(false);
            }
            finally
            {
                loadDone.TrySetResult(true);
            }
            await FinishPoller(run, poller).ConfigureAwait(false);

            Absorb(run, outcome);
            run.Performance.Summary = outcome.Summary;
            run.Performance.Seconds = outcome.Seconds;
            run.Performance.Windows = outcome.Windows;
            var verdict = DegradationDetector.Detect(outcome.Windows, s.SoakLatencyRisePercent, s.SoakThroughputDropPercent);
            run.Performance.Degradation = verdict;

            if (ConcludeCommon(run, outcome, target, false)) return;
            if (verdict.Degraded)
            {
                Fixtures.Set(run.Result, TestResultClass.FAIL, $"{target} got worse during the soak: {string.Join(", ", verdict.Reasons)}. Reported as a finding. The windows are in the performance section and in the windows file.");
                return;
            }
            if (outcome.Summary.Errors.Problems > 0)
            {
                Fixtures.Set(run.Result, TestResultClass.FAIL, $"During the soak, {target} {ProblemWords(outcome.Summary.Errors)}. The counts are in the performance section.");
                return;
            }
            if (verdict.TooShort)
            {
                Fixtures.Set(run.Result, TestResultClass.INDETERMINATE, "Every C-STORE was stored, but " + verdict.Description.Substring(0, 1).ToLowerInvariant() + verdict.Description.Substring(1));
                return;
            }
            Fixtures.Set(run.Result, TestResultClass.PASS, "Every C-STORE was stored, and the system did not get worse over the soak. The windows are in the performance section and in the windows file.");
        }

        private static string Duration(double seconds)
        {
            if (seconds >= 3600 && seconds % 3600 == 0) return (seconds / 3600).ToString("0", CultureInfo.InvariantCulture) + " h";
            if (seconds >= 60 && seconds % 60 == 0) return (seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min";
            return seconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
        }
    }
}
