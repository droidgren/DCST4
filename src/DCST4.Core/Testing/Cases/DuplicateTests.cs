using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO.Buffer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>What the duplicate and conflict tests share: sending a fixture and getting it back by whichever retrieve works.</summary>
    internal static class DuplicateSupport
    {
        public static async Task<RunSummary> SendAsync(TestContext context, IImageSource source, DicomTransferSyntax syntax, string folderName, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var timeline = timelines.Next();
            var summary = await Fixtures.SendAsync(context, source, syntax, context.Session.NewFolder(folderName), timeline, token).ConfigureAwait(false);
            timelines.Collect(timeline);

            Fixtures.AddContexts(result, timeline.Negotiation);
            foreach (var image in summary.Results.Where(r => r.StatusCode.HasValue && r.AcceptedTransferSyntax != null)) Fixtures.AddStatus(result, "C-STORE", image.StatusCode.Value);
            return summary;
        }

        /// <summary>Waits for one instance to be indexed.</summary>
        public static async Task<IndexPollResult> FindAsync(TestContext context, DicomDataset dataset, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var poll = await Fixtures.WaitForInstancesAsync(context,
                dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
                dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID),
                new[] { dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID) }, timelines, token).ConfigureAwait(false);

            Fixtures.AddContexts(result, poll.Last.Exchange.Contexts);
            if (poll.Last.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", poll.Last.FinalStatus.Value);
            return poll;
        }

        /// <summary>The study back by C-GET, or else C-MOVE. The method and its failure are in the outcome.</summary>
        public static async Task<RetrieveOutcome> RetrieveAsync(TestContext context, DicomDataset dataset, string purpose, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var studyUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
            var sopClass = dataset.GetSingleValue<string>(DicomTag.SOPClassUID);
            var syntaxUid = dataset.InternalTransferSyntax.UID.UID;
            var target = RetrieveSteps.Target(studyUid, dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty), QueryRoot.Study);

            var timeline = timelines.Next();
            var get = await RetrieveService.GetAsync(context.Profile.Endpoint, context.Profile.Network, target, context.Session.NewFolder("retrieved-" + purpose), RetrieveSteps.Contexts(new[] { sopClass }, syntaxUid), token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);
            RetrieveSteps.Record(result, get, "C-GET");

            if (get.Files.Count > 0 || get.Exchange.Failure != ConnectivityFailure.ContextNotAccepted) return get;

            var listener = RetrieveSteps.TryListen(context.Profile, context.Session.NewFolder("retrieved-" + purpose), RetrieveSteps.Preferred(new[] { sopClass }, syntaxUid), out var problem);
            if (listener == null)
            {
                result.Details["retrieveProblem"] = problem;
                return get;
            }

            using (listener)
            {
                var moveTimeline = timelines.Next();
                var move = await RetrieveService.MoveAsync(context.Profile.Endpoint, context.Profile.Network, target, context.Profile.LocalScpAe, listener, token, moveTimeline).ConfigureAwait(false);
                timelines.Collect(moveTimeline);
                RetrieveSteps.Record(result, move, "C-MOVE");
                return move;
            }
        }

        public static bool IsStored(StoreResult result) => result.State == StoreState.Success || result.State == StoreState.Warning;

        /// <summary>A refusal the standard allows, as opposed to a failure.</summary>
        public const ushort DuplicateSopInstance = 0x0111;
    }

    /// <summary>The same object sent twice.</summary>
    public sealed class DuplicateIdenticalTest : ITestCase
    {
        public string Id => "duplicate-identical";
        public string Name => "Duplicate: the same SOP Instance UID, identical content";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult();
            var timelines = new TimelineCollector();
            var generator = new TestDatasetGenerator(Fixtures.Request(context, "ct", Id, DicomTransferSyntax.ExplicitVRLittleEndian));

            var first = await DuplicateSupport.SendAsync(context, generator, generator.TransferSyntax, "originals-" + Id, result, timelines, token).ConfigureAwait(false);
            var step = StoreMapping.Classify(first, "the first copy");
            if (step.Result != TestResultClass.PASS)
            {
                Fixtures.Set(result, step.Result == TestResultClass.TOOL_ERROR ? step.Result : TestResultClass.INDETERMINATE, "The first copy could not be stored, so the duplicate could not be tried. " + step.Explanation);
                result.Timeline = timelines.Events;
                return result;
            }

            var original = DicomFile.Open(first.Results.Single().SavedPath);
            result.Uids["sopInstanceUid"] = original.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
            result.Uids["studyInstanceUid"] = original.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);

            var second = await DuplicateSupport.SendAsync(context, new FileListSource(original), generator.TransferSyntax, "resent-" + Id, result, timelines, token).ConfigureAwait(false);
            var again = second.Results.SingleOrDefault();
            var poll = await DuplicateSupport.FindAsync(context, original.Dataset, result, timelines, token).ConfigureAwait(false);
            result.Timeline = timelines.Events;

            if (again == null || !again.StatusCode.HasValue)
            {
                var sent = StoreMapping.Classify(second, "the second copy");
                Fixtures.Set(result, sent.Result, sent.Explanation);
                return result;
            }

            result.Details["secondStatus"] = again.StatusCodeText;
            if (again.StatusCode == DuplicateSupport.DuplicateSopInstance)
            {
                Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, "The second copy was refused with 0x0111, Duplicate SOP Instance.");
                return result;
            }
            if (!DuplicateSupport.IsStored(again))
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"The second copy failed with {again.StatusCodeText} ({again.Description}), which is not the status for a duplicate.");
                return result;
            }

            var found = poll.Last.Matches.Count(m => m.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty) == result.Uids["sopInstanceUid"]);
            result.Details["instancesFound"] = Fixtures.Int(found);
            Fixtures.Set(result, found == 1 ? TestResultClass.PASS : TestResultClass.FAIL, found == 1
                ? $"The second copy was accepted with {again.StatusCodeText}, and there is still one instance."
                : $"The second copy was accepted with {again.StatusCodeText}, and a query finds {Fixtures.Int(found)} instances with that UID.");
            return result;
        }
    }

    /// <summary>The same SOP Instance UID sent again with other content: which version does the system keep?</summary>
    public sealed class DuplicateModifiedTest : ITestCase
    {
        private const string ChangedSuffix = " MODIFIED";
        private const string KeptFirst = "first";
        private const string KeptSecond = "second";

        public string Id => "duplicate-modified";
        public string Name => "Duplicate: the same SOP Instance UID, modified content";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult();
            var timelines = new TimelineCollector();
            var generator = new TestDatasetGenerator(Fixtures.Request(context, "ct", Id, DicomTransferSyntax.ExplicitVRLittleEndian));

            var first = await DuplicateSupport.SendAsync(context, generator, generator.TransferSyntax, "originals-" + Id, result, timelines, token).ConfigureAwait(false);
            var step = StoreMapping.Classify(first, "the first version");
            if (step.Result != TestResultClass.PASS)
            {
                Fixtures.Set(result, step.Result == TestResultClass.TOOL_ERROR ? step.Result : TestResultClass.INDETERMINATE, "The first version could not be stored, so the change could not be tried. " + step.Explanation);
                result.Timeline = timelines.Events;
                return result;
            }

            var original = DicomFile.Open(first.Results.Single().SavedPath);
            var firstDescription = original.Dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty);
            var firstFrame = DicomPixelData.Create(original.Dataset).GetFrame(0).Data.ToArray();
            result.Uids["sopInstanceUid"] = original.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
            result.Uids["studyInstanceUid"] = original.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);

            var changed = Modify(original, firstDescription + ChangedSuffix, firstFrame);
            var second = await DuplicateSupport.SendAsync(context, new FileListSource(changed), generator.TransferSyntax, "resent-" + Id, result, timelines, token).ConfigureAwait(false);
            var again = second.Results.SingleOrDefault();

            if (again == null || !again.StatusCode.HasValue)
            {
                var sent = StoreMapping.Classify(second, "the second version");
                Fixtures.Set(result, sent.Result, sent.Explanation);
                result.Timeline = timelines.Events;
                return result;
            }

            result.Details["secondStatus"] = again.StatusCodeText;
            if (again.StatusCode == DuplicateSupport.DuplicateSopInstance || !DuplicateSupport.IsStored(again))
            {
                var isExpected = again.StatusCode == DuplicateSupport.DuplicateSopInstance;
                Fixtures.Set(result, isExpected ? TestResultClass.EXPECTED_FAILURE : TestResultClass.FAIL, isExpected
                    ? "The second version was refused with 0x0111, Duplicate SOP Instance, so the first stays."
                    : $"The second version failed with {again.StatusCodeText} ({again.Description}), which is not the status for a duplicate.");
                result.Timeline = timelines.Events;
                return result;
            }

            var retrieved = await DuplicateSupport.RetrieveAsync(context, original.Dataset, Id, result, timelines, token).ConfigureAwait(false);
            result.Timeline = timelines.Events;
            if (retrieved.Files.Count != 1)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The second version was accepted, but the object could not be retrieved to see which one was kept. " + RetrieveSteps.Describe(retrieved, 1));
                return result;
            }

            var back = DicomFile.Open(retrieved.Files[0].Path);
            var description = back.Dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty).TrimEnd();
            var frame = DicomPixelData.Create(back.Clone(DicomTransferSyntax.ExplicitVRLittleEndian).Dataset).GetFrame(0).Data;

            var attributeKept = description == firstDescription.TrimEnd() ? KeptFirst : description == (firstDescription + ChangedSuffix).TrimEnd() ? KeptSecond : "neither";
            var pixelsKept = frame.SequenceEqual(firstFrame) ? KeptFirst : frame.SequenceEqual(Changed(firstFrame)) ? KeptSecond : "neither";
            result.Details["attributeVersionKept"] = attributeKept;
            result.Details["pixelVersionKept"] = pixelsKept;

            if (attributeKept == pixelsKept && attributeKept != "neither")
            {
                Fixtures.Set(result, TestResultClass.PASS, $"The second version was accepted with {again.StatusCodeText}, and the system keeps the {attributeKept} version.");
            }
            else
            {
                result.Details["finding"] = "mixed-versions";
                Fixtures.Set(result, TestResultClass.FAIL, $"The system returned the attributes of the {attributeKept} version and the pixels of the {pixelsKept} version: an object that was never sent.");
            }
            return result;
        }

        private static DicomFile Modify(DicomFile original, string description, byte[] frame)
        {
            var dataset = original.Dataset.Clone();
            dataset.AddOrUpdate(DicomTag.SeriesDescription, description);
            dataset.AddOrUpdate(new FellowOakDicom.DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(Changed(frame))));
            return new DicomFile(dataset);
        }

        /// <summary>Enough changed pixels that no compression could pass them off as the same.</summary>
        private static byte[] Changed(byte[] frame)
        {
            var changed = frame.ToArray();
            for (var i = 0; i < Math.Min(64, changed.Length); i++) changed[i] ^= 0x55;
            return changed;
        }
    }

    /// <summary>One Study Instance UID, two different patients.</summary>
    public sealed class ConflictDemographicsTest : ITestCase
    {
        private const int MaxPatientDraws = 8;

        public string Id => "conflict-demographics";
        public string Name => "Conflict: one Study Instance UID, two patients";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult();
            var timelines = new TimelineCollector();
            var firstGenerator = new TestDatasetGenerator(Fixtures.Request(context, "ct", Id, DicomTransferSyntax.ExplicitVRLittleEndian));

            var first = await DuplicateSupport.SendAsync(context, firstGenerator, firstGenerator.TransferSyntax, "originals-" + Id, result, timelines, token).ConfigureAwait(false);
            var step = StoreMapping.Classify(first, "the first patient's object");
            if (step.Result != TestResultClass.PASS)
            {
                Fixtures.Set(result, step.Result == TestResultClass.TOOL_ERROR ? step.Result : TestResultClass.INDETERMINATE, "The first object could not be stored, so the conflict could not be tried. " + step.Explanation);
                result.Timeline = timelines.Events;
                return result;
            }

            var original = DicomFile.Open(first.Results.Single().SavedPath);
            var studyUid = original.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
            var firstPatient = original.Dataset.GetSingleValue<string>(DicomTag.PatientID);
            result.Uids["studyInstanceUid"] = studyUid;
            result.Details["firstPatientId"] = firstPatient;

            var conflicting = SecondPatient(context, firstPatient, studyUid, token);
            if (conflicting == null)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, "DCST4 could not draw a second, different patient from the list of test personnummer.");
                result.Timeline = timelines.Events;
                return result;
            }

            var secondPatient = conflicting.Dataset.GetSingleValue<string>(DicomTag.PatientID);
            result.Details["secondPatientId"] = secondPatient;

            var second = await DuplicateSupport.SendAsync(context, new FileListSource(conflicting), firstGenerator.TransferSyntax, "conflict-" + Id, result, timelines, token).ConfigureAwait(false);
            var again = second.Results.SingleOrDefault();
            if (again == null || !again.StatusCode.HasValue)
            {
                var sent = StoreMapping.Classify(second, "the second patient's object");
                Fixtures.Set(result, sent.Result, sent.Explanation);
                result.Timeline = timelines.Events;
                return result;
            }

            result.Details["secondStatus"] = again.StatusCodeText;
            if (!DuplicateSupport.IsStored(again))
            {
                Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"The object with another patient under the same Study Instance UID was refused with {again.StatusCodeText} ({again.Description}).");
                result.Timeline = timelines.Events;
                return result;
            }

            var retrieved = await DuplicateSupport.RetrieveAsync(context, original.Dataset, Id, result, timelines, token).ConfigureAwait(false);
            result.Timeline = timelines.Events;
            if (retrieved.Files.Count == 0)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The second object was accepted, but the study could not be retrieved to see what happened to it. " + RetrieveSteps.Describe(retrieved, 2));
                return result;
            }

            var patients = retrieved.Files.Select(f => DicomFile.Open(f.Path).Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty)).Distinct().ToList();
            result.Details["patientIdsReturned"] = string.Join(",", patients);

            if (patients.Count >= 2)
            {
                result.Details["finding"] = "two-patients-in-one-study";
                Fixtures.Set(result, TestResultClass.FAIL, "The system keeps two different patients under one Study Instance UID without any change or warning.");
            }
            else if (patients.Count == 1 && patients[0] == firstPatient)
            {
                Fixtures.Set(result, TestResultClass.PASS, "The system kept the first patient's demographics for the whole study and changed the second object to match.");
            }
            else
            {
                Fixtures.Set(result, TestResultClass.PASS, "The system changed the demographics so that the study holds one patient.");
            }
            return result;
        }

        /// <summary>The object of another patient from the list, moved into the first patient's study.</summary>
        private static DicomFile SecondPatient(TestContext context, string firstPatient, string studyUid, CancellationToken token)
        {
            for (var draw = 0; draw < MaxPatientDraws; draw++)
            {
                var request = Fixtures.Request(context, "ct", "conflict-demographics/second/" + Fixtures.Int(draw), DicomTransferSyntax.ExplicitVRLittleEndian);
                var image = new TestDatasetGenerator(request).GetImages(token).Single();
                if (image.File.Dataset.GetSingleValue<string>(DicomTag.PatientID) == firstPatient) continue;

                image.File.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
                return image.File;
            }
            return null;
        }
    }
}
