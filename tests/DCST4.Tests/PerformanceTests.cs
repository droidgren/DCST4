using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using DCST4.Core.Performance;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>Phase 7: the load, ramp and soak tests, their metrics on fixed input, and against the mock with scripted delays.</summary>
    internal static class PerformanceTests
    {
        private const int PerfPort = 41220;
        private const string Root = "1.2.826.0.1.3680043.10.999";

        private static string _temp;
        private static Action<string, bool, string> _check;

        public static async Task Run(string temp, Action<string, bool, string> check)
        {
            _temp = temp;
            _check = check;

            TestHistogram();
            TestSaturation();
            TestDegradation();
            TestRateGate();
            TestSettings();
            TestStamping();

            using (DicomServerFactory.Create<MockStorageScp>(PerfPort))
            {
                await TestScriptedDelay();
                await TestRateAndAssociations();
                await TestCancel();
                await TestRamp();
                await TestSoak();
                await TestTimeToAvailable();
                await TestMixes();
                await TestFrontEnds();
            }
            Scp.Reset();
        }

        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        /// <summary>
        /// A ramp against the mock, which stores four at a time, drawn as the Performance tab draws it, for a look at the chart.
        /// Not part of the test run. The rendering happens on this thread, which must be STA.
        /// </summary>
        public static async Task RenderChart(string path)
        {
            var seconds = new List<LoadSecond>();
            using (DicomServerFactory.Create<MockStorageScp>(PerfPort))
            {
                ResetScp();
                Scp.ResponseDelayMs = 50;
                Scp.StoreConcurrency = 4;
                Scp.StatusFor = i => i > 150 && i % 37 == 0 ? new DicomStatus("A700", DicomState.Failure, "Out of resources") : null;
                var result = await One("load-ramp", Settings(s => { s.RampSteps = new List<int> { 1, 2, 4, 8 }; s.RampStepSeconds = 6; s.WarmUpSeconds = 1; }),
                    progress: p => { if (p.Stage == TestProgressStage.Metrics) lock (seconds) seconds.Add(p.Metrics); }).ConfigureAwait(true);
                Console.WriteLine(Show(result) + " " + result.Performance?.Saturation?.Description);
            }
            Scp.Reset();

            var thread = new Thread(() =>
            {
                var chart = new DCST4.Controls.LoadChart { Series = seconds, Width = 1000, Height = 420 };
                chart.Measure(new System.Windows.Size(1000, 420));
                chart.Arrange(new System.Windows.Rect(0, 0, 1000, 420));
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1000, 420, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(chart);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (var file = File.Create(path)) encoder.Save(file);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Console.WriteLine("Chart written to " + path);
        }

        // ---------------------------------------------------------------- helpers

        private static TargetProfile Profile(int responseTimeout = 10) => new TargetProfile
        {
            Name = "Mock",
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = PerfPort, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = responseTimeout },
            UidRoot = Root,
        };

        /// <summary>A small CT, so that the mock spends its time in the scripted delay and not in parsing.</summary>
        private static DataMix SmallCt() => new DataMix("ct64", "CT 64 x 64 for the tests", DicomTransferSyntax.ExplicitVRLittleEndian, new DataMixEntry("ct", 64, 64, 16)) { PoolPerEntry = 2 };

        private static LoadSettings Settings(Action<LoadSettings> change = null)
        {
            var settings = new LoadSettings { MixOverride = SmallCt(), Mix = "ct64", SampleEvery = 0, WarmUpSeconds = 0 };
            change?.Invoke(settings);
            return settings;
        }

        private static TestRunOptions Options(LoadSettings settings, string metricsFolder = null) => new TestRunOptions { Seed = 21, Load = settings, MetricsFolder = metricsFolder };

        private static async Task<TestResult> One(string test, LoadSettings settings, string metricsFolder = null, CancellationToken token = default(CancellationToken), Action<TestProgress> progress = null)
        {
            var selected = TestCatalog.Select(test, out var error);
            if (selected == null) throw new InvalidOperationException(error);
            var runner = new TestRunner();
            if (progress != null) runner.Progress += progress;
            var report = await runner.RunAsync(Profile(), selected, token, TestPatientNaming.RegistrySwedishName, Options(settings, metricsFolder));
            return report.Results.Single();
        }

        private static string Show(TestResult result) => result.Result + ": " + result.Explanation;

        private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static void ResetScp()
        {
            Scp.Reset();
            Scp.RecordReceived = false;
            Scp.ReceiveInMemory = true;
        }

        private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds = 3000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > milliseconds) return false;
                await Task.Delay(20);
            }
            return true;
        }

        // ---------------------------------------------------------------- fixed input

        private static void TestHistogram()
        {
            Console.WriteLine("\nPerformance: percentiles on fixed input");

            var h = new LatencyHistogram();
            for (var v = 1; v <= 1000; v++) h.Record(v);
            Check("1..1000 µs: p50 is 500 (nearest rank)", h.Percentile(50) == 500, h.Percentile(50).ToString(CultureInfo.InvariantCulture));
            Check("1..1000 µs: p95 is 950", h.Percentile(95) == 950, h.Percentile(95).ToString(CultureInfo.InvariantCulture));
            Check("1..1000 µs: p99 is 990", h.Percentile(99) == 990, h.Percentile(99).ToString(CultureInfo.InvariantCulture));
            Check("1..1000 µs: max and p100 are exact", h.MaxMicros == 1000 && h.Percentile(100) == 1000);
            Check("1..1000 µs: count and mean", h.Count == 1000 && Math.Abs(h.MeanMicros - 500.5) < 1e-9, N(h.MeanMicros));

            Check("Empty: every percentile is 0", new LatencyHistogram().Percentile(50) == 0 && new LatencyHistogram().Percentile(100) == 0);
            var single = new LatencyHistogram();
            single.Record(12345);
            Check("One sample: every percentile is that sample", single.Percentile(1) == 12345 && single.Percentile(50) == 12345 && single.Percentile(99) == 12345);

            var low = new LatencyHistogram();
            var high = new LatencyHistogram();
            for (var v = 1; v <= 500; v++) low.Record(v);
            for (var v = 501; v <= 1000; v++) high.Record(v);
            low.Add(high);
            Check("Merged halves give the same percentiles as one histogram", low.Percentile(50) == 500 && low.Percentile(95) == 950 && low.Count == 1000);

            var wide = new LatencyHistogram();
            for (var v = 1; v <= 200000; v++) wide.Record(v);
            var p50 = wide.Percentile(50);
            var p99 = wide.Percentile(99);
            Check("Above 512 µs a percentile is at most 0.4 % below the true value",
                p50 <= 100000 && p50 >= 100000 * (1 - 1.0 / 256) && p99 <= 198000 && p99 >= 198000 * (1 - 1.0 / 256), p50 + " " + p99);
        }

        private static RampStep Step(int step, int associations, double perSecond, double p95, double errorRate = 0, long lost = 0) => new RampStep
        {
            Step = step,
            Associations = associations,
            Summary = new LoadSummary
            {
                StoredPerSecond = perSecond,
                Latency = new LatencySummary { P95Ms = p95 },
                ErrorRate = errorRate,
                Errors = new StatusClassCounts { AssociationLost = lost, Failure = errorRate > 0 ? 1 : 0 },
            },
        };

        private static void TestSaturation()
        {
            Console.WriteLine("\nPerformance: saturation on fixed input");

            var linear = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 20, 100), Step(3, 4, 40, 100), Step(4, 8, 80, 100) });
            Check("Doubling throughput: no saturation, best step 4", !linear.Found && linear.BestStep == 4, linear.Description);

            var plateau = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 20, 100), Step(3, 4, 21, 100), Step(4, 8, 22, 100) });
            Check("Plateau: saturated at step 3, reason throughput", plateau.Found && plateau.Step == 3 && plateau.Associations == 4 && plateau.Reason == "throughput", plateau.Description);
            Check("Plateau: the best step is still the highest throughput", plateau.BestStep == 4, plateau.BestStep.ToString(CultureInfo.InvariantCulture));

            var latency = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 20, 150), Step(3, 4, 40, 250) });
            Check("p95 above twice that of step 1: saturated at step 3, reason latency", latency.Found && latency.Step == 3 && latency.Reason == "latency", latency.Description);

            var errors = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 20, 100), Step(3, 4, 40, 100, 0.05) });
            Check("Error rate up 5 points: saturated at step 3, reason errors", errors.Found && errors.Step == 3 && errors.Reason == "errors", errors.Description);

            var lost = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 20, 100, 0, 1), Step(3, 4, 40, 100) });
            Check("A lost association: saturated at that step", lost.Found && lost.Step == 2 && lost.Reason == "errors", lost.Description);

            var gain = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 10.9, 100) });
            var enough = SaturationDetector.Detect(new[] { Step(1, 1, 10, 100), Step(2, 2, 11.1, 100) });
            Check("The 10 % gain threshold is applied as stated", gain.Found && !enough.Found);
        }

        private static List<SoakWindow> Windows(Func<int, double> p95, Func<int, double> perSecond, Func<int, long> failures = null, int count = 10) =>
            Enumerable.Range(1, count).Select(i => new SoakWindow
            {
                Window = i,
                Summary = new LoadSummary
                {
                    StoredPerSecond = perSecond(i),
                    Latency = new LatencySummary { P95Ms = p95(i) },
                    Errors = new StatusClassCounts { Failure = failures?.Invoke(i) ?? 0 },
                },
            }).ToList();

        private static void TestDegradation()
        {
            Console.WriteLine("\nPerformance: degradation on fixed input");

            var flat = DegradationDetector.Detect(Windows(i => 100, i => 10));
            Check("Flat soak: not degraded", !flat.Degraded && !flat.TooShort, flat.Description);

            var noisy = DegradationDetector.Detect(Windows(i => i % 2 == 0 ? 120 : 90, i => i % 2 == 0 ? 11 : 9));
            Check("Noisy but level soak: not degraded", !noisy.Degraded, noisy.Description);

            var rising = DegradationDetector.Detect(Windows(i => 100 + (i - 2) * 25, i => 10));
            Check("p95 rising from 100 to 300 ms: degraded, p95-trend", rising.Degraded && rising.Reasons.SequenceEqual(new[] { "p95-trend" }), rising.Description + " trend " + N(rising.P95TrendPercent));

            var dropping = DegradationDetector.Detect(Windows(i => 100, i => i >= 9 ? 5 : 10));
            Check("Throughput halved at the end: degraded, throughput-drop", dropping.Degraded && dropping.Reasons.SequenceEqual(new[] { "throughput-drop" }), dropping.Description);

            var errors = DegradationDetector.Detect(Windows(i => 100, i => 10, i => i == 7 ? 3 : 0));
            Check("Errors after an error-free start: degraded, errors", errors.Degraded && errors.Reasons.SequenceEqual(new[] { "errors" }), errors.Description);

            var warmUpOnly = DegradationDetector.Detect(Windows(i => i == 1 ? 900 : 100, i => i == 1 ? 1 : 10));
            Check("A slow first window is the warm-up and does not count", !warmUpOnly.Degraded, warmUpOnly.Description);

            var shortSoak = DegradationDetector.Detect(Windows(i => 100, i => 10, count: 2));
            Check("Two windows: too short to say", shortSoak.TooShort && !shortSoak.Degraded, shortSoak.Description);
        }

        private static void TestRateGate()
        {
            Console.WriteLine("\nPerformance: rate limit on a fake clock");

            long now = 0;
            var gate = new RateGate(10, () => now);
            var interval = Stopwatch.Frequency / 10;
            var first = new[] { gate.NextSlot(), gate.NextSlot(), gate.NextSlot() };
            Check("10/s: slots one interval apart from now", first[0] == 0 && first[1] == interval && first[2] == 2 * interval, string.Join(" ", first));

            now = 100 * interval;
            var late = gate.NextSlot();
            var after = gate.NextSlot();
            Check("A worker that falls behind sends now and does not burst", late == now && after == now + interval, late + " " + after);
            Check("0 is no limit", new RateGate(0).Unlimited);
        }

        private static void TestSettings()
        {
            Console.WriteLine("\nPerformance: settings and the planned total");

            var count = new LoadSettings { InstanceCount = 300 };
            Check("A count is the planned total, and said so exactly", count.PlannedInstances(LoadMode.Load) == 300 && count.DescribePlan(LoadMode.Load, "PACS").StartsWith("Stores 300 ", StringComparison.Ordinal), count.DescribePlan(LoadMode.Load, "PACS"));
            Check("A rate for a duration: rate x duration", new LoadSettings { RatePerSecond = 5, DurationSeconds = 20 }.PlannedInstances(LoadMode.Load) == 100);
            Check("No rate: the ceiling, 1000 by default", new LoadSettings().PlannedInstances(LoadMode.Load) == 1000 && new LoadSettings().DescribePlan(LoadMode.Load, "PACS").StartsWith("Stores up to 1,000 ", StringComparison.Ordinal));
            Check("Ramp: its own ceiling, 2000", new LoadSettings().PlannedInstances(LoadMode.Ramp) == 2000);
            Check("Soak: rate x duration", new LoadSettings { SoakRatePerSecond = 2, SoakSeconds = 1800 }.PlannedInstances(LoadMode.Soak) == 3600);
            Check("An explicit ceiling wins", new LoadSettings { MaxInstances = 50, InstanceCount = 300 }.PlannedInstances(LoadMode.Load) == 50);
            Check("The plan names the size and the destination", new LoadSettings().DescribePlan(LoadMode.Load, "PACS at h:104").Contains("MB, mix ct) on PACS at h:104.") && !new LoadSettings().DescribePlan(LoadMode.Load, "PACS at h:104").Contains("delete"));
            Check("Without a rate, the plan says from what rate the ceiling ends the run early", new LoadSettings().DescribePlan(LoadMode.Load, "PACS").EndsWith("A system that stores more than 50 objects per second has stored all 1,000 (Max instances) before the 20 s are up, and the run ends early. Set a rate, or raise Max instances, to measure the whole 20 s.", StringComparison.Ordinal)
                && !new LoadSettings { RatePerSecond = 5 }.DescribePlan(LoadMode.Load, "PACS").Contains("ends early"), new LoadSettings().DescribePlan(LoadMode.Load, "PACS"));
            var lines = new LoadSettings().DescribePlan(LoadMode.Load, "PACS at h:104", true).Split('\n');
            Check("For the question: the destination, a blank line, then one sentence per line", lines.Length == 4 && lines[0].EndsWith("on PACS at h:104.", StringComparison.Ordinal) && lines[1].Length == 0
                && lines[2].StartsWith("A system that stores", StringComparison.Ordinal) && lines[3].StartsWith("Set a rate", StringComparison.Ordinal)
                && !new LoadSettings().DescribePlan(LoadMode.Load, "PACS").Contains("\n"), string.Join(" | ", lines));

            Check("Defaults are valid in every mode", new LoadSettings().Validate(LoadMode.Load) == null && new LoadSettings().Validate(LoadMode.Ramp) == null && new LoadSettings().Validate(LoadMode.Soak) == null);
            Check("0 associations is refused", new LoadSettings { Associations = 0 }.Validate(LoadMode.Load) != null);
            Check("Falling ramp steps are refused", new LoadSettings { RampSteps = new List<int> { 4, 2 } }.Validate(LoadMode.Ramp) != null);
            Check("A soak without a rate is refused", new LoadSettings { SoakRatePerSecond = 0 }.Validate(LoadMode.Soak) != null);
            Check("An unknown mix is refused", new LoadSettings { Mix = "nope" }.Validate(LoadMode.Load) != null);
            Check("Every mix in the catalog has a valid profile", DataMixCatalog.All.All(m => m.Entries.All(e => DatasetProfileCatalog.Find(e.ProfileId) != null)) && DataMixCatalog.Find("ct") != null);

            double seconds;
            Check("Durations: 90, 90s, 30m, 8h, 1.5",
                LoadSettings.TryParseDuration("90", out seconds) && seconds == 90 && LoadSettings.TryParseDuration("90s", out seconds) && seconds == 90
                && LoadSettings.TryParseDuration("30m", out seconds) && seconds == 1800 && LoadSettings.TryParseDuration("8h", out seconds) && seconds == 28800
                && LoadSettings.TryParseDuration("1.5", out seconds) && seconds == 1.5 && !LoadSettings.TryParseDuration("x", out seconds));
            Check("Ramp steps parse, and garbage does not", LoadSettings.ParseSteps("1,2,4,8").SequenceEqual(new[] { 1, 2, 4, 8 }) && LoadSettings.ParseSteps("a") == null);
        }

        private static void TestStamping()
        {
            Console.WriteLine("\nPerformance: the pool and new UIDs per send");

            var pool = InstancePool.Build(SmallCt(), 5, Root, TestPatientNaming.RegistrySwedishName, CancellationToken.None);
            Check("The pool holds the generated objects", pool.Instances.Count == 2 && pool.SopClasses.Single() == DicomUID.CTImageStorage.UID);

            var stamper = new UidStamper(Root, "abc", 0, instancesPerStudy: 3);
            var item = pool.Instances[0];
            var sends = Enumerable.Range(0, 4).Select(i => stamper.Stamp(item)).ToList();
            Check("Every send has a new SOP Instance UID", sends.Select(s => s.SopInstanceUid).Distinct().Count() == 4);
            Check("A new study after the set number of sends", sends[0].StudyInstanceUid == sends[2].StudyInstanceUid && sends[3].StudyInstanceUid != sends[0].StudyInstanceUid);
            Check("All UIDs are under the root and at most 64 characters",
                sends.SelectMany(s => new[] { s.SopInstanceUid, s.StudyInstanceUid, s.SeriesInstanceUid }).All(u => u.StartsWith(Root + ".", StringComparison.Ordinal) && u.Length <= 64));
            Check("The pixel data is the same array in every send, not a copy", sends.All(s => ReferenceEquals(s.Parts[1].Array, item.Tail)));
            Check("Another run gives other UIDs", new UidStamper(Root, "abd", 0).Stamp(item).SopInstanceUid != new UidStamper(Root, "abc", 0).Stamp(item).SopInstanceUid);

            // What goes on the wire is read back by fo-dicom and compared with what the generator made.
            var original = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "ct", Seed = 5, UidRoot = Root, InstancesPerSeries = 2, Rows = 64, Columns = 64, BitsStored = 16, PatientNaming = TestPatientNaming.RegistrySwedishName })
                .GetImages(CancellationToken.None).First().File;
            var decoded = Decode(original, sends[1]);
            var stamped = new HashSet<DicomTag> { DicomTag.SOPInstanceUID, DicomTag.StudyInstanceUID, DicomTag.SeriesInstanceUID, DicomTag.AccessionNumber, DicomTag.SeriesNumber, DicomTag.InstanceNumber, DicomTag.FrameOfReferenceUID };
            var differing = original.Dataset.Where(e => !stamped.Contains(e.Tag) && !(e is DicomSequence)).Where(e => decoded.Dataset.GetString(e.Tag) != original.Dataset.GetString(e.Tag)).Select(e => e.Tag.ToString()).ToList();
            Check("The bytes sent read back as the generated object apart from the stamped attributes", differing.Count == 0, string.Join(" ", differing));
            Check("The stamped values are the ones sent", decoded.Dataset.GetString(DicomTag.SOPInstanceUID) == sends[1].SopInstanceUid && decoded.Dataset.GetString(DicomTag.InstanceNumber) == "2");
            var pixels = FellowOakDicom.Imaging.DicomPixelData.Create(decoded.Dataset).GetFrame(0).Data;
            var expected = FellowOakDicom.Imaging.DicomPixelData.Create(original.Dataset).GetFrame(0).Data;
            Check("The pixels read back unchanged", pixels.SequenceEqual(expected));
        }

        /// <summary>The meta header of the original, then the bytes of the send, opened as a file.</summary>
        private static DicomFile Decode(DicomFile original, StampedInstance send)
        {
            using (var stream = new MemoryStream())
            {
                original.Save(stream);
                var bytes = stream.ToArray();
                const int groupLengthValue = 128 + 4 + 8;
                var start = groupLengthValue + 4 + (int)BitConverter.ToUInt32(bytes, groupLengthValue);
                var file = new MemoryStream();
                file.Write(bytes, 0, start);
                foreach (var part in send.Parts) file.Write(part.Array, part.Offset, part.Count);
                file.Position = 0;
                return DicomFile.Open(file);
            }
        }

        // ---------------------------------------------------------------- against the mock

        /// <summary>The same limits as the checks below, which they report one by one.</summary>
        private static bool WithinDelayTolerance(LoadSummary s, int associations)
        {
            var fromLatency = associations * 1000.0 / s.Latency.MeanMs;
            return s.Latency.P50Ms >= 100 && s.Latency.P50Ms <= 130 && s.Latency.P95Ms >= 100 && s.Latency.P95Ms <= 150
                && s.StoredPerSecond >= 4000.0 / 130 && s.StoredPerSecond <= 4000.0 / 100 * 1.02
                && Math.Abs(s.StoredPerSecond - fromLatency) / fromLatency < 0.10;
        }

        private static async Task TestScriptedDelay()
        {
            Console.WriteLine("\nPerformance: latency and throughput against a scripted delay");
            const int associations = 4;
            List<LoadSecond> seconds = null;
            TestResult result = null;
            LoadSummary summary = null;

            // A timing test on a shared computer can be hit by a stall of the whole process, such as a full garbage
            // collection left over from the earlier sections. So the heap is collected first, and an attempt outside the
            // tolerance is tried once more. An implementation that is really slower fails both.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                ResetScp();
                Scp.ResponseDelayMs = 100;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var collected = new List<LoadSecond>();
                seconds = collected;
                result = await One("load-throughput", Settings(s => { s.Associations = associations; s.DurationSeconds = 3; s.WarmUpSeconds = 0.5; }),
                    progress: p => { if (p.Stage == TestProgressStage.Metrics) lock (collected) collected.Add(p.Metrics); });
                summary = result.Performance?.Summary;
                if (summary == null || WithinDelayTolerance(summary, associations)) break;
                if (attempt == 1) Console.WriteLine($"  INFO  first attempt outside the tolerance (p50 {N(summary.Latency.P50Ms)} ms, p95 {N(summary.Latency.P95Ms)} ms, {N(summary.StoredPerSecond)} stored/s), trying once more");
            }
            Check("Load against a 100 ms delay: PASS", result.Result == TestResultClass.PASS, Show(result));
            if (summary == null) return;

            Console.WriteLine($"  INFO  100 ms delay, {associations} associations: latency p50 {N(summary.Latency.P50Ms)} ms, p95 {N(summary.Latency.P95Ms)} ms, {N(summary.StoredPerSecond)} stored/s");
            // The delay is a Task.Delay, which Windows rounds up to its 15.6 ms timer tick, and the mock and DCST4 add a little.
            string Rows() { lock (seconds) return string.Join(" ", seconds.Select(r => r.Second + ":" + r.Stored + "/" + N(r.LatencyP50Ms) + "/" + N(r.LatencyP95Ms) + "/" + N(r.LatencyMaxMs))); }
            Check("Latency p50 is between 100 and 130 ms", summary.Latency.P50Ms >= 100 && summary.Latency.P50Ms <= 130, N(summary.Latency.P50Ms) + " rows " + Rows());
            Check("Latency p95 is at most 150 ms", summary.Latency.P95Ms >= 100 && summary.Latency.P95Ms <= 150, N(summary.Latency.P95Ms));
            Check("Throughput is between 4000/130 and 4000/100 per second", summary.StoredPerSecond >= 4000.0 / 130 && summary.StoredPerSecond <= 4000.0 / 100 * 1.02, N(summary.StoredPerSecond));
            var fromLatency = associations * 1000.0 / summary.Latency.MeanMs;
            Check("Throughput matches associations / mean latency within 10 %", Math.Abs(summary.StoredPerSecond - fromLatency) / fromLatency < 0.10, N(summary.StoredPerSecond) + " vs " + N(fromLatency));
            Check("Response wait is the delay too, on a small object", summary.ResponseWait.P50Ms >= 100 && summary.ResponseWait.P50Ms <= summary.Latency.P50Ms + 0.001);
            Check("One setup time per association", summary.AssociationSetup.Count == associations && summary.AssociationsOpened == associations, summary.AssociationSetup.Count.ToString(CultureInfo.InvariantCulture));
            Check("All attempts were Success, by raw code", summary.Errors.Success == summary.Attempts && summary.Errors.ByCode.Keys.SequenceEqual(new[] { "0x0000" }) && summary.ErrorRate == 0);
            Check("MB/s follows from bytes and the window", Math.Abs(summary.MegabytesPerSecond - summary.Bytes / 1e6 / (summary.WindowMs / 1000.0)) < 0.01, N(summary.MegabytesPerSecond));

            List<LoadSecond> rows;
            lock (seconds) rows = seconds.ToList();
            Check("One live row per second, in order", rows.Count >= 3 && rows.Select(r => r.Second).SequenceEqual(Enumerable.Range(0, rows.Count)), string.Join(",", rows.Select(r => r.Second)));
            Check("The per-second rows add up to every attempt", rows.Sum(r => r.Attempts) == summary.Attempts && rows.Sum(r => r.Stored) == summary.Errors.Success, rows.Sum(r => r.Attempts) + " vs " + summary.Attempts);
            Check("The warm-up is marked in the series", rows[0].WarmUp && !rows.Last().WarmUp);
            Check("The mock got what was stored", Scp.StoreCount == summary.Attempts, Scp.StoreCount + " vs " + summary.Attempts);
            Check("The result records the status, the context and the client", result.StatusCodes.Any(s => s.Code == "0x0000") && result.NegotiatedContexts.Count > 0 && result.Performance.Client.LogicalProcessors > 0);
        }

        private static async Task TestRateAndAssociations()
        {
            Console.WriteLine("\nPerformance: rate limit, instance count and associations");
            ResetScp();

            var rated = await One("load-throughput", Settings(s => { s.Associations = 2; s.DurationSeconds = 2; s.RatePerSecond = 25; }));
            var attempts = rated.Performance?.Summary?.Attempts ?? 0;
            Check("25/s for 2 s sends 50 within 15 %", rated.Result == TestResultClass.PASS && attempts >= 43 && attempts <= 57, attempts + " " + Show(rated));

            ResetScp();
            var counted = await One("load-throughput", Settings(s => { s.Associations = 2; s.InstanceCount = 20; s.InstancesPerAssociation = 5; }));
            var summary = counted.Performance?.Summary;
            Check("A count of 20 sends exactly 20", counted.Result == TestResultClass.PASS && summary?.Attempts == 20 && Scp.StoreCount == 20, Show(counted));
            // Two workers share the count, so the last association of each can hold fewer than 5: 4 or 5 associations, none empty.
            var perAssociation = Scp.Associations.Select(a => a.Stores).ToList();
            Check("5 per association: 4 or 5 associations, each with 1 to 5 stores, none opened empty, all released",
                summary?.AssociationsOpened == perAssociation.Count && perAssociation.Count >= 4 && perAssociation.Count <= 5 && perAssociation.All(n => n >= 1 && n <= 5) && perAssociation.Sum() == 20 && Scp.Associations.All(a => a.Released),
                string.Join(",", perAssociation) + ", " + Scp.Associations.Count(a => a.Released) + " released");
            Check("Stopped by the count", counted.Performance?.StoppedBy == "count", counted.Performance?.StoppedBy);
            Check("An exact count is not an early end", counted.Performance?.EndedByCeiling == false && !counted.Explanation.Contains("ceiling"), counted.Explanation);

            ResetScp();
            var capped = await One("load-throughput", Settings(s => { s.Associations = 2; s.DurationSeconds = 10; s.MaxInstances = 30; }));
            Check("A ceiling that ends a duration run early: said in the result and the explanation", capped.Result == TestResultClass.PASS && capped.Performance?.EndedByCeiling == true && capped.Explanation.Contains("Note: all 30 instances (Max instances) were stored after ")
                && capped.Explanation.Contains(" s of the 10 s, so throughput and latency come from a shorter window than asked for. Raise Max instances"), Show(capped));
            Check("A ceiling that ends a run early is not a problem", capped.Performance?.Summary?.Errors?.DescribeProblems() == null, capped.Performance?.Summary?.Errors?.DescribeProblems());

            ResetScp();
            Scp.StatusFor = i => i % 4 == 3 ? new DicomStatus("A700", DicomState.Failure, "Out of resources") : null;
            var refused = await One("load-throughput", Settings(s => { s.Associations = 1; s.InstanceCount = 8; }));
            Check("Failure statuses under load: FAIL, counted by raw code", refused.Result == TestResultClass.FAIL && refused.Performance.Summary.Errors.ByCode["0xA700"] == 2 && refused.Performance.Summary.Errors.Failure == 2, Show(refused));
            Check("Failure statuses under load: described with count and raw code", refused.Performance.Summary.Errors.DescribeProblems() == "2 not stored: 2 with status 0xA700", refused.Performance.Summary.Errors.DescribeProblems());
            refused.Details.TryGetValue("firstProblem", out var first);
            Check("The first problem is in the details, with its status, Error Comment and second", first != null && first.StartsWith("C-STORE status 0xA700 (Refused: Out of Resources), ", StringComparison.Ordinal) && first.Contains("In second 0 of the run."), first);
        }

        private static async Task TestCancel()
        {
            Console.WriteLine("\nPerformance: cancel");
            ResetScp();
            Scp.ResponseDelayMs = 400;

            using (var cancel = new CancellationTokenSource())
            {
                var run = One("load-throughput", Settings(s => { s.Associations = 8; s.DurationSeconds = 30; }), token: cancel.Token);
                await WaitFor(() => Scp.StoreCount >= 8, 10000);
                await Task.Delay(1000);
                var clock = Stopwatch.StartNew();
                cancel.Cancel();
                var finished = await Task.WhenAny(run, Task.Delay(10000)) == run;
                var took = clock.ElapsedMilliseconds;
                Check("The run returns within 5 s of the cancel", finished && took < 5000, took + " ms");
                if (!finished) return;

                var result = await run;
                Check("Cancelled: INDETERMINATE", result.Result == TestResultClass.INDETERMINATE, Show(result));
                var closed = await WaitFor(() => Scp.Associations.All(a => a.Closed), 2000);
                var all = Scp.Associations.ToList();
                Check("Every association of the mock has ended", closed && all.Count == 8, all.Count(a => a.Closed) + " of " + all.Count);
                Check("All were released, none aborted, since the delay is shorter than the grace", all.All(a => a.Released) && !all.Any(a => a.AbortedByPeer), all.Count(a => a.Released) + " released, " + all.Count(a => a.AbortedByPeer) + " aborted");
                Check("Stopped by the cancel", result.Performance?.StoppedBy == "cancelled", result.Performance?.StoppedBy);
            }
        }

        private static async Task TestRamp()
        {
            Console.WriteLine("\nPerformance: ramp to saturation");
            ResetScp();
            // About 80 images per step, so that one image more or less moves a step's throughput by about 1 %, well inside the 10 % threshold.
            Scp.ResponseDelayMs = 50;
            Scp.StoreConcurrency = 2;

            var folder = Path.Combine(_temp, "ramp");
            var result = await One("load-ramp", Settings(s => { s.RampSteps = new List<int> { 1, 2, 4 }; s.RampStepSeconds = 2.5; s.WarmUpSeconds = 0.5; }), folder);
            var verdict = result.Performance?.Saturation;
            Console.WriteLine("  INFO  ramp stored/s per step: " + string.Join(" / ", result.Performance?.Steps?.Select(st => N(st.Summary.StoredPerSecond)) ?? new string[0]));
            Check("Ramp: PASS", result.Result == TestResultClass.PASS, Show(result));
            Check("A system that stores 2 at a time saturates at step 3, 4 associations, reason throughput",
                verdict != null && verdict.Found && verdict.Step == 3 && verdict.Associations == 4 && verdict.Reason == "throughput",
                verdict?.Description + " " + string.Join(" / ", result.Performance?.Steps?.Select(st => N(st.Summary.StoredPerSecond)) ?? new string[0]));
            Check("Three steps, the third marked", result.Performance?.Steps?.Count == 3 && result.Performance.Steps[2].Saturated && !result.Performance.Steps[1].Saturated);

            var series = result.Performance?.SeriesFile;
            var lines = series != null && File.Exists(series) ? File.ReadAllLines(series) : new string[0];
            Check("The series file has the steps in order", lines.Length > 3 && lines.Skip(1).Select(l => l.Split(',')[1]).Distinct().SequenceEqual(new[] { "1", "2", "3" }), series);
            Check("The series file is named after the run", series != null && Path.GetFileName(series).StartsWith("DCST4-run-", StringComparison.Ordinal) && series.EndsWith("-load-ramp.csv", StringComparison.Ordinal));
        }

        private static async Task TestSoak()
        {
            Console.WriteLine("\nPerformance: soak");
            ResetScp();
            Scp.ResponseDelayFor = i => i < 50 ? 10 : 200;

            var folder = Path.Combine(_temp, "soak");
            var run = One("load-soak", Settings(s => { s.Associations = 2; s.SoakSeconds = 3.2; s.SoakRatePerSecond = 20; s.SnapshotSeconds = 1; }), folder);

            await Task.Delay(2300);
            var windowsFile = Directory.Exists(folder) ? Directory.GetFiles(folder, "*-windows.csv").FirstOrDefault() : null;
            var during = windowsFile == null ? 0 : ReadShared(windowsFile).Length;
            Check("The windows file has rows while the soak is still going", !run.IsCompleted && during >= 2, during + " lines");

            var result = await run;
            var verdict = result.Performance?.Degradation;
            Check("A rising delay: FAIL as a finding", result.Result == TestResultClass.FAIL && result.Explanation.Contains("finding"), Show(result));
            Check("Degradation flagged with the p95 trend", verdict != null && verdict.Degraded && verdict.Reasons.Contains("p95-trend"), verdict?.Description + " " + N(verdict?.P95TrendPercent ?? 0));
            Check("At least three windows were kept", result.Performance?.Windows?.Count >= 3, result.Performance?.Windows?.Count.ToString());
            var after = windowsFile == null ? 0 : File.ReadAllLines(windowsFile).Length;
            Check("Every window is in the file afterwards", after == (result.Performance?.Windows?.Count ?? -1) + 1, after + " lines");

            ResetScp();
            Scp.ResponseDelayMs = 50;
            var steady = await One("load-soak", Settings(s => { s.Associations = 2; s.SoakSeconds = 3.2; s.SoakRatePerSecond = 20; s.SnapshotSeconds = 1; }));
            Check("A steady system: PASS, no degradation", steady.Result == TestResultClass.PASS && !steady.Performance.Degradation.Degraded, Show(steady) + " " + steady.Performance?.Degradation?.Description);
        }

        private static string[] ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static async Task TestTimeToAvailable()
        {
            Console.WriteLine("\nPerformance: time-to-available");
            ResetScp();
            Scp.KeepStored = true;
            var hideFor = (long)(1.5 * Stopwatch.Frequency);
            Scp.HideFromQuery = uid =>
            {
                var stored = Scp.StoredAt.ToList();
                if (stored.Count > 0 && stored[0].Item1 == uid) return true;
                var entry = stored.FirstOrDefault(s => s.Item1 == uid);
                return entry == null || Stopwatch.GetTimestamp() - entry.Item2 < hideFor;
            };

            var result = await One("load-throughput", Settings(s => { s.Associations = 1; s.InstanceCount = 4; s.SampleEvery = 1; s.PollIntervalSeconds = 0.5; s.PollTimeoutSeconds = 2.5; }));
            var tta = result.Performance?.TimeToAvailable;
            Check("Load with polling: PASS", result.Result == TestResultClass.PASS, Show(result));
            Check("Every stored instance was sampled at 1 in 1", tta?.Sampled == 4, tta?.Sampled.ToString());
            Check("Three found, the one hidden for good never found", tta?.Found == 3 && tta.NeverFound == 1 && tta.QuerySupported == true, tta == null ? "none" : tta.Found + " found, " + tta.NeverFound + " never");
            Check("Found 1.5 to 2.5 s after the response, with a 0.5 s interval", tta?.Available != null && tta.Available.P50Ms >= 1500 && tta.Available.P50Ms <= 2500 && tta.Available.MaxMs <= 2600, N(tta?.Available?.P50Ms ?? 0) + " / " + N(tta?.Available?.MaxMs ?? 0));
            Check("At most 10 queries per round", tta != null && tta.Rounds > 0 && tta.Queries <= tta.Rounds * LoadSettings.MaxQueriesPerRound);

            ResetScp();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.CTImageStorage, ScpAnswer.Accept);
            Scp.ContextTable.Set(DicomUID.StudyRootQueryRetrieveInformationModelFind, ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported));
            var noFind = await One("load-throughput", Settings(s => { s.Associations = 1; s.InstanceCount = 3; s.SampleEvery = 1; s.PollIntervalSeconds = 0.3; s.PollTimeoutSeconds = 2; }));
            Check("Without C-FIND the load still passes and the poller says why", noFind.Result == TestResultClass.PASS && noFind.Performance.TimeToAvailable.QuerySupported == false && noFind.Performance.TimeToAvailable.Note != null, Show(noFind));
        }

        private static async Task TestMixes()
        {
            Console.WriteLine("\nPerformance: data mixes and PDU size");
            ResetScp();
            Scp.RecordReceived = true;

            var mix = DataMixCatalog.Find("small-lossless");
            var result = await One("load-throughput", new LoadSettings { Mix = mix.Id, Associations = 1, InstanceCount = 4, SampleEvery = 0, WarmUpSeconds = 0, MaxPdu = 16384 });
            var received = Scp.Received.ToList();
            Check("small-lossless: PASS, CR and US arrive in JPEG Lossless", result.Result == TestResultClass.PASS && received.Count == 4 && received.All(r => r.Syntax == DicomTransferSyntax.JPEGProcess14SV1)
                && received.Select(r => r.Dataset.GetString(DicomTag.Modality)).Distinct().OrderBy(m => m).SequenceEqual(new[] { "CR", "US" }), Show(result));

            var seed = new TestRunSession(new TestRunOptions { Seed = 21 }).SeedFor("load-pool/" + mix.Id);
            var pool = InstancePool.Build(mix, seed, Root, TestPatientNaming.RegistrySwedishName, CancellationToken.None);
            Check("The compressed frames arrive byte for byte as encoded in the pool", received.All(r => pool.Instances.Any(i => Contains(i.Tail, r.Frame))));
            Check("Sent with the PDU length asked for", result.Performance.MaxPdu == 16384);

            ResetScp();
            Scp.RecordReceived = true;
            var large = new DataMix("large-test", "Enhanced CT and Tomosynthesis, few frames", DicomTransferSyntax.ExplicitVRLittleEndian,
                new DataMixEntry("enhanced-ct", 256, 256, 16, 4), new DataMixEntry("tomosynthesis-large", 512, 512, 14, 2)) { PoolPerEntry = 1 };
            var big = await One("load-throughput", new LoadSettings { MixOverride = large, Mix = large.Id, Associations = 1, InstanceCount = 2, SampleEvery = 0, WarmUpSeconds = 0 });
            var frames = Scp.Received.Select(r => r.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1)).OrderBy(f => f).ToList();
            Check("A large mix: both object types, multi-frame, arrive", big.Result == TestResultClass.PASS && frames.SequenceEqual(new[] { 2, 4 }), Show(big) + " " + string.Join(",", frames));
            Check("Enhanced CT and Tomosynthesis are two SOP classes on one association", big.NegotiatedContexts.Count == 2 && Scp.Associations.Count == 1);

            ResetScp();
            Scp.ContextTable = new ScpContextTable();
            Scp.ContextTable.Set(DicomUID.CTImageStorage, DicomTransferSyntax.ExplicitVRLittleEndian, ScpAnswer.Reject(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported));
            var refused = await One("load-throughput", Settings(s => { s.InstanceCount = 4; }));
            Check("A syntax the system does not accept: NOT_SUPPORTED, nothing sent", refused.Result == TestResultClass.NOT_SUPPORTED && Scp.StoreCount == 0, Show(refused));
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle == null || needle.Length == 0 || needle.Length > haystack.Length) return false;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) continue;
                var j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static async Task TestFrontEnds()
        {
            Console.WriteLine("\nPerformance: catalog, command line and log");

            var all = TestCatalog.Select("all", out _);
            var performance = TestCatalog.Select("Performance", out _);
            Check("'all' leaves the Performance tests out", all.All(t => t.Category != TestCategory.Performance));
            Check("'Performance' selects the three", performance.Select(t => t.Id).SequenceEqual(new[] { "load-throughput", "load-ramp", "load-soak" }));

            ResetScp();
            Program.LogSink.Clear();
            var quiet = await One("load-throughput", Settings(s => { s.Associations = 4; s.InstanceCount = 200; }));
            var lines = Program.LogSink.Lines.Where(l => !l.StartsWith("Debug", StringComparison.Ordinal) && !l.StartsWith("Verbose", StringComparison.Ordinal)).ToList();
            Check("200 instances log fewer than 10 lines", quiet.Result == TestResultClass.PASS && lines.Count < 10, lines.Count + " lines: " + string.Join(" | ", lines.Take(12)));

            ResetScp();
            var profilePath = Path.Combine(_temp, "perf-profile.json");
            Profile().Save(profilePath);
            var outFolder = Path.Combine(_temp, "perf-cli");
            var cli = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "load-throughput", "--out", outFolder, "--log-dir", Path.Combine(_temp, "perf-log"),
                "--load-count", "20", "--load-associations", "2", "--tta-sample", "0", "--load-warmup", "0");
            Check("CLI load run: exit 0", cli.ExitCode == 0, cli.Output);
            Check("CLI prints the plan before sending", cli.Output.Contains("Plan for the load test: Stores 20 new objects"), cli.Output);
            Check("CLI writes the series file next to the results", Directory.Exists(outFolder) && Directory.GetFiles(outFolder, "*-load-throughput.csv").Length == 1);
            var json = Directory.Exists(outFolder) ? Directory.GetFiles(outFolder, "DCST4-run-*.json").Select(File.ReadAllText).FirstOrDefault() ?? string.Empty : string.Empty;
            Check("The JSON has schema 6 and the performance section", json.Contains("\"schemaVersion\": 6") && json.Contains("\"performance\"") && json.Contains("\"storedPerSecond\""));
            Check("Doubles are written short", !System.Text.RegularExpressions.Regex.IsMatch(json, @":\s*-?\d+\.\d{10,}\s*[,\r\n}]"));

            var bad = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "load-throughput", "--out", outFolder, "--load-associations", "0");
            Check("CLI refuses 0 associations: exit 2", bad.ExitCode == RunExitCodes.Usage, bad.Output);
            var badMix = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "load-throughput", "--out", outFolder, "--load-mix", "nope");
            Check("CLI refuses an unknown mix: exit 2", badMix.ExitCode == RunExitCodes.Usage && badMix.Output.Contains("list-mixes"), badMix.Output);
            var badSteps = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "load-ramp", "--out", outFolder, "--ramp-steps", "4,2");
            Check("CLI refuses falling ramp steps: exit 2", badSteps.ExitCode == RunExitCodes.Usage, badSteps.Output);
            var mixes = await TestSuiteTests.RunCli("list-mixes");
            Check("CLI lists the mixes", mixes.ExitCode == 0 && DataMixCatalog.All.All(m => mixes.Output.Contains(m.Id)), mixes.Output);
        }
    }
}
