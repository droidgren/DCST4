using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>What the query tests know about one stored object, read from the copy that was sent.</summary>
    internal sealed class QueryScope
    {
        public DicomDataset Original;
        public string PatientId;
        public string StudyUid;
        public string SeriesUid;
        public string SopUid;

        /// <summary>A target object, not its source image, stored by this run. Null if there is none.</summary>
        public static QueryScope From(TestRunSession session)
        {
            var instance = session.Objects.SelectMany(o => o.Stored).Where(i => i.SavedPath != null)
                .OrderBy(i => i.Manifest.Role == ManifestInstance.RoleTarget ? 0 : 1).FirstOrDefault();
            if (instance == null) return null;

            var dataset = DicomFile.Open(instance.SavedPath).Dataset;
            return new QueryScope
            {
                Original = dataset,
                PatientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
                StudyUid = instance.Manifest.StudyInstanceUid,
                SeriesUid = instance.Manifest.SeriesInstanceUid,
                SopUid = instance.Manifest.SopInstanceUid,
            };
        }

        /// <summary>The keys that aim the query at this object at a level. Patient Root also needs the patient at each level.</summary>
        public DicomDataset Identifier(QueryRoot root, DicomQueryRetrieveLevel level)
        {
            var dataset = new DicomDataset();
            if (level == DicomQueryRetrieveLevel.Patient || root == QueryRoot.Patient) dataset.Add(DicomTag.PatientID, PatientId);
            if (level != DicomQueryRetrieveLevel.Patient) dataset.Add(DicomTag.StudyInstanceUID, StudyUid);
            if (level == DicomQueryRetrieveLevel.Series || level == DicomQueryRetrieveLevel.Image) dataset.Add(DicomTag.SeriesInstanceUID, SeriesUid);
            if (level == DicomQueryRetrieveLevel.Image) dataset.Add(DicomTag.SOPInstanceUID, SopUid);
            return dataset;
        }
    }

    internal static class QueryTestText
    {
        public const string NothingStored = "This test needs an object stored earlier in the same run, and no store test stored one.";

        public static TestResult NothingToQuery() => new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = NothingStored };

        public static string Status(ushort? status) => status.HasValue ? AssociationTimeline.FormatStatus(status.Value) : null;

        public static string LevelName(DicomQueryRetrieveLevel level) => level.ToString().ToUpperInvariant();
    }

    /// <summary>Whether what was stored can be found: the "indexed" step of the chain.</summary>
    public sealed class QueryIndexTest : ITestCase
    {
        public string Id => "query-index";
        public string Name => "C-FIND: stored objects are indexed";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var records = context.Session.Objects.Where(o => o.Stored.Any()).ToList();
            if (records.Count == 0) return QueryTestText.NothingToQuery();

            var result = new TestResult();
            var timelines = new TimelineCollector();

            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                record.Indexed = await CheckObjectAsync(context, record, result, timelines, token).ConfigureAwait(false);
            }

            result.Timeline = timelines.Events;
            var worst = ResultSeverity.Worst(records.Select(r => r.Indexed.Result));
            var found = records.Count(r => r.Indexed.Result == TestResultClass.PASS);
            result.Details["objectTypes"] = Fixtures.Int(records.Count);
            result.Details["indexed"] = Fixtures.Int(found);

            var trouble = records.FirstOrDefault(r => r.Indexed.Result == worst && worst != TestResultClass.PASS);
            Fixtures.Set(result, worst, trouble != null
                ? $"{Fixtures.Int(found)} of {Fixtures.Int(records.Count)} object types were found. {trouble.DisplayName}: {trouble.Indexed.Explanation}"
                : $"{Fixtures.Int(records.Count)} of {Fixtures.Int(records.Count)} object types were found with C-FIND at IMAGE level.");
            return result;
        }

        private static async Task<ChainStep> CheckObjectAsync(TestContext context, StoredObjectRecord record, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var missing = 0;
            var total = 0;
            foreach (var series in record.Stored.GroupBy(i => i.Manifest.SeriesInstanceUid))
            {
                var uids = series.Select(i => i.Manifest.SopInstanceUid).ToList();
                total += uids.Count;

                var poll = await Fixtures.WaitForInstancesAsync(context, record.StudyInstanceUid, series.Key, uids, timelines, token).ConfigureAwait(false);
                Fixtures.AddContexts(result, poll.Last.Exchange.Contexts);
                if (poll.Last.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", poll.Last.FinalStatus.Value);

                var failure = Fixtures.ClassOf(poll.Last.Exchange);
                if (failure != TestResultClass.PASS) return ChainStep.Of(failure, poll.Last.Exchange.Message ?? "The query was cancelled.");
                if (!poll.Last.Succeeded)
                {
                    return ChainStep.Of(TestResultClass.FAIL, $"The query ended with {QueryTestText.Status(poll.Last.FinalStatus) ?? "no final status"}.");
                }
                missing += uids.Count - poll.Found.Count;
            }

            return missing == 0
                ? ChainStep.Of(TestResultClass.PASS, $"{Fixtures.Int(total)} of {Fixtures.Int(total)} instances were found.")
                : ChainStep.Of(TestResultClass.FAIL, $"{Fixtures.Int(missing)} of {Fixtures.Int(total)} instances were accepted but could not be found within {Fixtures.Int(context.Session.Options.IndexWaitSeconds)} s.");
        }
    }

    /// <summary>Which levels work under which root.</summary>
    public sealed class QueryLevelsTest : ITestCase
    {
        private static readonly DicomQueryRetrieveLevel[] Levels = { DicomQueryRetrieveLevel.Patient, DicomQueryRetrieveLevel.Study, DicomQueryRetrieveLevel.Series, DicomQueryRetrieveLevel.Image };

        public string Id => "query-levels";
        public string Name => "C-FIND: levels under Patient Root and Study Root";
        public TestCategory Category => TestCategory.Capability;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var scope = QueryScope.From(context.Session);
            if (scope == null) return QueryTestText.NothingToQuery();

            var result = new TestResult { Query = new QueryReport() };
            var timelines = new TimelineCollector();

            foreach (var root in new[] { QueryRoot.Patient, QueryRoot.Study })
            {
                foreach (var level in Levels)
                {
                    token.ThrowIfCancellationRequested();
                    var timeline = timelines.Next();
                    var outcome = await QueryService.FindAsync(context.Profile.Endpoint, context.Profile.Network, root, level, scope.Identifier(root, level), token, timeline).ConfigureAwait(false);
                    timelines.Collect(timeline);

                    Fixtures.AddContexts(result, outcome.Exchange.Contexts);
                    if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", outcome.FinalStatus.Value);
                    result.Query.Levels.Add(Row(root, level, outcome));
                }
            }

            result.Timeline = timelines.Events;
            Conclude(result);
            return result;
        }

        private static QueryLevelRow Row(QueryRoot root, DicomQueryRetrieveLevel level, QueryOutcome outcome)
        {
            var row = new QueryLevelRow
            {
                Root = root.ToString(),
                Level = QueryTestText.LevelName(level),
                Status = QueryTestText.Status(outcome.FinalStatus),
                Matches = outcome.Matches.Count,
            };

            // PATIENT is not a level of the Study Root model, so a refusal is the right answer.
            var refusalIsRight = root == QueryRoot.Study && level == DicomQueryRetrieveLevel.Patient;

            var failure = Fixtures.ClassOf(outcome.Exchange);
            if (failure == TestResultClass.NOT_SUPPORTED)
            {
                row.Result = refusalIsRight ? TestResultClass.EXPECTED_FAILURE : TestResultClass.NOT_SUPPORTED;
                row.Explanation = outcome.Exchange.Message;
            }
            else if (failure != TestResultClass.PASS)
            {
                row.Result = failure;
                row.Explanation = outcome.Exchange.Message ?? "The query was cancelled.";
            }
            else if (!outcome.Succeeded)
            {
                row.Result = refusalIsRight ? TestResultClass.EXPECTED_FAILURE : TestResultClass.FAIL;
                row.Explanation = outcome.FinalStatus.HasValue ? DimseStatusText.Describe(outcome.FinalStatus.Value) : "No final response.";
            }
            else if (outcome.Matches.Count == 0)
            {
                row.Result = refusalIsRight ? TestResultClass.EXPECTED_FAILURE : TestResultClass.FAIL;
                row.Explanation = "The query succeeded and found nothing, although the object is there.";
            }
            else
            {
                row.Result = TestResultClass.PASS;
                row.Explanation = refusalIsRight ? "PATIENT is not a level of Study Root, and this system answered it anyway." : "The object was found.";
            }
            return row;
        }

        private static void Conclude(TestResult result)
        {
            var rows = result.Query.Levels;
            var real = rows.Where(r => !(r.Root == nameof(QueryRoot.Study) && r.Level == "PATIENT")).ToList();

            if (real.All(r => r.Result == TestResultClass.NOT_SUPPORTED))
            {
                Fixtures.Set(result, TestResultClass.NOT_SUPPORTED, "Neither Patient Root nor Study Root C-FIND is accepted.");
                return;
            }

            // A root that is not offered at all is not a defect: most systems offer only one of them.
            var worst = ResultSeverity.Worst(real.Where(r => r.Result != TestResultClass.NOT_SUPPORTED).Select(r => r.Result));
            var working = real.Where(r => r.Result == TestResultClass.PASS).Select(r => r.Root + "/" + r.Level);
            var firstBad = real.FirstOrDefault(r => r.Result == worst && worst != TestResultClass.PASS);
            Fixtures.Set(result, worst, firstBad != null
                ? $"{firstBad.Root} Root at {firstBad.Level}: {firstBad.Explanation} Working: {string.Join(", ", working)}."
                : $"Working: {string.Join(", ", working)}.");
        }
    }

    /// <summary>Which return keys the system fills in, of a broad set per level.</summary>
    public sealed class QueryReturnKeysTest : ITestCase
    {
        private sealed class Key
        {
            public readonly DicomTag Tag;
            public readonly bool Required;
            public Key(DicomTag tag, bool required) { Tag = tag; Required = required; }
        }

        private static Key R(DicomTag tag) => new Key(tag, true);
        private static Key O(DicomTag tag) => new Key(tag, false);

        private static readonly Key[] PatientKeys =
        {
            R(DicomTag.PatientName), R(DicomTag.PatientID), O(DicomTag.PatientBirthDate), O(DicomTag.PatientSex),
            O(DicomTag.NumberOfPatientRelatedStudies), O(DicomTag.NumberOfPatientRelatedSeries), O(DicomTag.NumberOfPatientRelatedInstances),
        };

        private static readonly Key[] StudyKeys =
        {
            R(DicomTag.StudyDate), R(DicomTag.StudyTime), R(DicomTag.AccessionNumber), R(DicomTag.PatientName), R(DicomTag.PatientID), R(DicomTag.StudyID), R(DicomTag.StudyInstanceUID),
            O(DicomTag.ModalitiesInStudy), O(DicomTag.ReferringPhysicianName), O(DicomTag.StudyDescription), O(DicomTag.PatientBirthDate), O(DicomTag.PatientSex),
            O(DicomTag.NumberOfStudyRelatedSeries), O(DicomTag.NumberOfStudyRelatedInstances),
        };

        private static readonly Key[] SeriesKeys =
        {
            R(DicomTag.Modality), R(DicomTag.SeriesNumber), R(DicomTag.SeriesInstanceUID),
            O(DicomTag.SeriesDescription), O(DicomTag.BodyPartExamined), O(DicomTag.SeriesDate), O(DicomTag.SeriesTime), O(DicomTag.NumberOfSeriesRelatedInstances),
        };

        private static readonly Key[] ImageKeys =
        {
            R(DicomTag.InstanceNumber), R(DicomTag.SOPInstanceUID),
            O(DicomTag.SOPClassUID), O(DicomTag.Rows), O(DicomTag.Columns), O(DicomTag.BitsAllocated), O(DicomTag.NumberOfFrames), O(DicomTag.ImageComments),
        };

        public string Id => "query-return-keys";
        public string Name => "C-FIND: return keys that are populated";
        public TestCategory Category => TestCategory.Capability;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var scope = QueryScope.From(context.Session);
            if (scope == null) return QueryTestText.NothingToQuery();

            var result = new TestResult { Query = new QueryReport() };
            var timelines = new TimelineCollector();
            var answers = new List<TestResultClass>();
            string problem = null;

            var plan = new[]
            {
                Tuple.Create(QueryRoot.Patient, DicomQueryRetrieveLevel.Patient, PatientKeys),
                Tuple.Create(QueryRoot.Study, DicomQueryRetrieveLevel.Study, StudyKeys),
                Tuple.Create(QueryRoot.Study, DicomQueryRetrieveLevel.Series, SeriesKeys),
                Tuple.Create(QueryRoot.Study, DicomQueryRetrieveLevel.Image, ImageKeys),
            };

            foreach (var step in plan)
            {
                token.ThrowIfCancellationRequested();
                var identifier = scope.Identifier(step.Item1, step.Item2);
                foreach (var key in step.Item3)
                {
                    // Empty, which asks for the value without matching on it.
                    if (!identifier.Contains(key.Tag)) identifier.AddOrUpdate(key.Tag.DictionaryEntry.ValueRepresentations[0], key.Tag, new string[0]);
                }

                var timeline = timelines.Next();
                var outcome = await QueryService.FindAsync(context.Profile.Endpoint, context.Profile.Network, step.Item1, step.Item2, identifier, token, timeline).ConfigureAwait(false);
                timelines.Collect(timeline);

                Fixtures.AddContexts(result, outcome.Exchange.Contexts);
                if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", outcome.FinalStatus.Value);

                var answer = Fixtures.ClassOf(outcome.Exchange);
                if (answer == TestResultClass.PASS && !outcome.Succeeded) answer = TestResultClass.FAIL;
                if (answer == TestResultClass.PASS && outcome.Matches.Count == 0) answer = TestResultClass.FAIL;
                answers.Add(answer);

                var name = QueryTestText.LevelName(step.Item2);
                if (answer != TestResultClass.PASS)
                {
                    problem = problem ?? (outcome.Exchange.Message ?? $"The query at {name} level found nothing or failed ({QueryTestText.Status(outcome.FinalStatus) ?? "no status"}).");
                    continue;
                }

                var match = outcome.Matches[0];
                foreach (var key in step.Item3) result.Query.ReturnKeys.Add(ReadKey(name, key, match, scope.Original));
            }

            result.Timeline = timelines.Events;
            Conclude(result, answers, problem);
            return result;
        }

        private static QueryKeyRow ReadKey(string level, Key key, DicomDataset match, DicomDataset original)
        {
            var row = new QueryKeyRow { Level = level, Keyword = key.Tag.DictionaryEntry.Keyword, Tag = key.Tag.ToString(), Required = key.Required };

            string returned = null;
            var present = match.Contains(key.Tag) && match.TryGetString(key.Tag, out returned);
            row.State = !match.Contains(key.Tag) ? ReturnKeyState.Absent : (present && !string.IsNullOrWhiteSpace(returned) ? ReturnKeyState.Populated : ReturnKeyState.Empty);
            row.Returned = returned?.TrimEnd(' ', '\0');

            if (original.Contains(key.Tag) && original.TryGetString(key.Tag, out var sent))
            {
                row.MatchesOriginal = row.State == ReturnKeyState.Populated ? (bool?)(sent.TrimEnd(' ', '\0') == row.Returned) : null;
            }
            return row;
        }

        private static void Conclude(TestResult result, List<TestResultClass> answers, string problem)
        {
            if (answers.All(a => a == TestResultClass.NOT_SUPPORTED))
            {
                Fixtures.Set(result, TestResultClass.NOT_SUPPORTED, "No C-FIND context needed for this test was accepted.");
                return;
            }

            var keys = result.Query.ReturnKeys;
            var populated = keys.Count(k => k.State == ReturnKeyState.Populated);
            var missing = keys.Where(k => k.Required && k.State != ReturnKeyState.Populated).ToList();
            result.Details["populated"] = Fixtures.Int(populated);
            result.Details["requested"] = Fixtures.Int(keys.Count);

            var worst = ResultSeverity.Worst(answers.Where(a => a != TestResultClass.NOT_SUPPORTED));
            if (worst != TestResultClass.PASS)
            {
                Fixtures.Set(result, worst, $"{problem} Of the keys that could be read, {Fixtures.Int(populated)} of {Fixtures.Int(keys.Count)} were populated.");
                return;
            }

            if (missing.Count > 0)
            {
                Fixtures.Set(result, TestResultClass.FAIL, $"{Fixtures.Int(missing.Count)} required keys came back empty or not at all: {string.Join(", ", missing.Select(k => k.Level + " " + k.Keyword))}.");
                return;
            }

            Fixtures.Set(result, TestResultClass.PASS, $"{Fixtures.Int(populated)} of {Fixtures.Int(keys.Count)} requested keys were populated, including every required one.");
        }
    }

    /// <summary>Wildcards, date ranges, case and Swedish characters, on two studies of one patient in two character sets.</summary>
    public sealed class QueryMatchingTest : ITestCase
    {
        private const string Latin1 = "ISO_IR 100";
        private const string Utf8 = "ISO_IR 192";
        private const int BothStudies = 2;

        public string Id => "query-matching";
        public string Name => "C-FIND: wildcards, date ranges, case and Swedish characters";
        public TestCategory Category => TestCategory.Capability;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var result = new TestResult { Query = new QueryReport() };
            var timelines = new TimelineCollector();

            var stored = new List<DicomDataset>();
            var uids = new List<Tuple<string, string, string>>();
            foreach (var variant in new[] { CharacterSetVariant.Latin1Swedish, CharacterSetVariant.Utf8Swedish })
            {
                var generator = new TestDatasetGenerator(Fixtures.Request(context, "ct", "query-matching", DicomTransferSyntax.ExplicitVRLittleEndian, variant));
                var timeline = timelines.Next();
                var summary = await Fixtures.SendAsync(context, generator, generator.TransferSyntax, context.Session.NewFolder("originals-query-matching"), timeline, token).ConfigureAwait(false);
                timelines.Collect(timeline);

                var step = StoreMapping.Classify(summary, "the fixture in " + variant);
                foreach (var image in summary.Results.Where(r => r.StatusCode.HasValue && r.AcceptedTransferSyntax != null)) Fixtures.AddStatus(result, "C-STORE", image.StatusCode.Value);
                if (step.Result != TestResultClass.PASS)
                {
                    var outcome = step.Result == TestResultClass.TOOL_ERROR || step.Result == TestResultClass.INDETERMINATE ? step.Result : TestResultClass.INDETERMINATE;
                    Fixtures.Set(result, outcome, $"The objects to query could not be stored, so matching could not be tried. {step.Explanation}");
                    result.Timeline = timelines.Events;
                    return result;
                }

                var manifest = generator.Manifest.Instances.Single();
                uids.Add(Tuple.Create(manifest.StudyInstanceUid, manifest.SeriesInstanceUid, manifest.SopInstanceUid));
                stored.Add(DicomFile.Open(summary.Results.Single().SavedPath).Dataset);
            }

            foreach (var ids in uids)
            {
                var poll = await Fixtures.WaitForInstancesAsync(context, ids.Item1, ids.Item2, new[] { ids.Item3 }, timelines, token).ConfigureAwait(false);
                Fixtures.AddContexts(result, poll.Last.Exchange.Contexts);
                var failure = Fixtures.ClassOf(poll.Last.Exchange);
                if (failure != TestResultClass.PASS)
                {
                    Fixtures.Set(result, failure, poll.Last.Exchange.Message ?? "Cancelled.");
                    result.Timeline = timelines.Events;
                    return result;
                }
            }

            var name = stored[0].GetSingleValue<string>(DicomTag.PatientName);
            var patientId = stored[0].GetSingleValue<string>(DicomTag.PatientID);
            result.Uids["studyInstanceUid.latin1"] = uids[0].Item1;
            result.Uids["studyInstanceUid.utf8"] = uids[1].Item1;

            foreach (var test in Cases(name))
            {
                token.ThrowIfCancellationRequested();
                result.Query.Matching.Add(await RunCaseAsync(context, test, patientId, result, timelines, token).ConfigureAwait(false));
            }

            result.Timeline = timelines.Events;
            var worst = ResultSeverity.Worst(result.Query.Matching.Select(r => r.Result));
            var firstBad = result.Query.Matching.FirstOrDefault(r => r.Result == worst && worst != TestResultClass.PASS);
            Fixtures.Set(result, worst, firstBad != null
                ? $"{firstBad.Case}: {firstBad.Note}"
                : $"All {Fixtures.Int(result.Query.Matching.Count)} matching cases gave an acceptable answer. See each case for what this system does.");
            return result;
        }

        private sealed class MatchCase
        {
            public string Name;
            public DicomTag Tag;
            public string Value;
            public string CharacterSet;
            public int Minimum;
            public int Expected;
            /// <summary>What the outcome says, when any answer is acceptable and only what it was is of interest.</summary>
            public Func<int, string> Observation;
        }

        private static IEnumerable<MatchCase> Cases(string name)
        {
            var star = name.Substring(0, 2) + "*";
            var question = name.Substring(0, 1) + "?" + name.Substring(2);
            var swedishIndex = name.IndexOfAny(new[] { 'å', 'ä', 'ö', 'Å', 'Ä', 'Ö' });
            var overSwedishLetter = swedishIndex >= 0 ? name.Substring(0, swedishIndex) + "?" + name.Substring(swedishIndex + 1) : null;

            MatchCase Name(string title, string value, string set, int min, Func<int, string> observation = null) =>
                new MatchCase { Name = title, Tag = DicomTag.PatientName, Value = value, CharacterSet = set, Minimum = min, Expected = BothStudies, Observation = observation };

            MatchCase Date(string title, string value, int expected) =>
                new MatchCase { Name = title, Tag = DicomTag.StudyDate, Value = value, CharacterSet = Latin1, Minimum = expected, Expected = expected };

            string Across(int matches) => matches >= BothStudies
                ? "It matches across character sets."
                : "It finds only the study stored in the same character set as the query, so it does not match across character sets.";

            yield return Name("exact name, query in ISO_IR 100", name, Latin1, 1, Across);
            yield return Name("exact name, query in ISO_IR 192", name, Utf8, 1, Across);
            yield return Name("wildcard *", star, Latin1, 1);
            yield return Name("wildcard ?", question, Latin1, 1);

            if (overSwedishLetter != null)
            {
                yield return Name("wildcard ? over a Swedish letter, query in ISO_IR 192", overSwedishLetter, Utf8, 0, matches => matches >= BothStudies
                    ? "? matches one character."
                    : matches == 1 ? "? matches in one character set only, which suggests that it counts bytes." : "? does not match a Swedish letter.");
            }

            yield return Name("case of the name, in upper case", name.ToUpperInvariant(), Utf8, 0, matches => matches > 0 ? "Matching is not case sensitive." : "Matching is case sensitive.");
            yield return Date("date exact", "20260115", BothStudies);
            yield return Date("date range around the study", "20260101-20260131", BothStudies);
            yield return Date("date from, including the study", "20260101-", BothStudies);
            yield return Date("date up to, including the study", "-20260131", BothStudies);
            yield return Date("date from, after the study", "20260116-", 0);
            yield return Date("date up to, before the study", "-20260114", 0);
        }

        private static async Task<QueryMatchRow> RunCaseAsync(TestContext context, MatchCase test, string patientId, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            // Always the one patient: a query with a wildcard must not be able to match a whole archive.
            var identifier = new DicomDataset
            {
                { DicomTag.SpecificCharacterSet, test.CharacterSet },
                { DicomTag.PatientID, patientId },
                { DicomTag.StudyInstanceUID, string.Empty },
                { test.Tag, test.Value },
            };

            var timeline = timelines.Next();
            var outcome = await QueryService.FindAsync(context.Profile.Endpoint, context.Profile.Network, QueryRoot.Study, DicomQueryRetrieveLevel.Study, identifier, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);

            Fixtures.AddContexts(result, outcome.Exchange.Contexts);
            if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", outcome.FinalStatus.Value);

            var row = new QueryMatchRow
            {
                Case = test.Name,
                Key = test.Tag.DictionaryEntry.Keyword,
                Value = test.Value,
                QueryCharacterSet = test.CharacterSet,
                MinimumMatches = test.Minimum,
                ExpectedMatches = test.Expected,
                Matches = outcome.Matches.Count,
            };

            var failure = Fixtures.ClassOf(outcome.Exchange);
            if (failure != TestResultClass.PASS)
            {
                row.Result = failure;
                row.Note = outcome.Exchange.Message ?? "Cancelled.";
            }
            else if (!outcome.Succeeded)
            {
                row.Result = TestResultClass.FAIL;
                row.Note = $"The query ended with {QueryTestText.Status(outcome.FinalStatus) ?? "no final status"}.";
            }
            else if (row.Matches > test.Expected)
            {
                row.Result = TestResultClass.FAIL;
                row.Note = $"It found {Fixtures.Int(row.Matches)} studies and only {Fixtures.Int(test.Expected)} can match.";
            }
            else if (row.Matches < test.Minimum)
            {
                row.Result = TestResultClass.FAIL;
                row.Note = $"It found {Fixtures.Int(row.Matches)} studies and at least {Fixtures.Int(test.Minimum)} should match.";
            }
            else
            {
                row.Result = TestResultClass.PASS;
                row.Note = test.Observation?.Invoke(row.Matches) ?? $"It found the {Fixtures.Int(row.Matches)} studies that match.";
            }
            return row;
        }
    }
}
