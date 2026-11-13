using DCST4.Core.Generation.Modalities;
using DCST4.Core.TestData;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DCST4.Core.Generation
{
    /// <summary>Values the user has entered manually. Null means Auto.</summary>
    public sealed class IdentityOverrides
    {
        public string PatientName { get; set; }
        public string PatientId { get; set; }
        /// <summary>yyyyMMdd.</summary>
        public string PatientBirthDate { get; set; }
        public string PatientSex { get; set; }
        public string AccessionNumber { get; set; }
        public string StudyId { get; set; }
        public string StudyInstanceUid { get; set; }
        public string SeriesInstanceUid { get; set; }
        public string SopInstanceUid { get; set; }
        public DateTime? StudyDateTime { get; set; }
        public string ReferringPhysicianName { get; set; }
        public string StudyDescription { get; set; }
        public string SeriesDescription { get; set; }
        public string InstitutionName { get; set; }
        public string StationName { get; set; }
        /// <summary>Tags added in Settings. A null value means Auto.</summary>
        public IList<AdditionalTagValue> AdditionalTags { get; set; }
        /// <summary>Generated names lose their å, ä and ö, for a character set that cannot hold them. Manual values are not changed.</summary>
        public bool AutoValuesInBasicAscii { get; set; }
        /// <summary>
        /// How an Auto patient name is made: an invented Swedish name, or DCST4^TEST^ and a number. The Auto Referring Physician
        /// Name follows it, as a Swedish name or DCST4^REFERRING^ and a number. Swedish here, so that the
        /// seeded test datasets, which name their patients themselves, draw exactly as before. Generate &amp; Send passes the setting.
        /// </summary>
        public TestPatientNaming PatientNaming { get; set; } = TestPatientNaming.RegistrySwedishName;
    }

    /// <summary>The final values of one exam, Auto or manual. Series and SOP Instance UIDs stay null when each series or image gets its own.</summary>
    public sealed class ExamIdentity
    {
        public string PatientName { get; set; }
        public string PatientId { get; set; }
        /// <summary>yyyyMMdd, or empty if unknown (Type 2).</summary>
        public string PatientBirthDate { get; set; }
        /// <summary>M, F, O, or empty if unknown (Type 2).</summary>
        public string PatientSex { get; set; }
        public string AccessionNumber { get; set; }
        public string StudyId { get; set; }
        public string StudyInstanceUid { get; set; }
        public string SeriesInstanceUid { get; set; }
        public string SopInstanceUid { get; set; }
        public DateTime StudyDateTime { get; set; }
        /// <summary>True: the exam time is worked out backwards from "now" when the images are generated.</summary>
        public bool StudyDateTimeIsAuto { get; set; }
        public string ReferringPhysicianName { get; set; }
        public string StudyDescription { get; set; }
        public string SeriesDescription { get; set; }
        public string InstitutionName { get; set; }
        public string StationName { get; set; }
        /// <summary>Tags added in Settings, with their final values.</summary>
        public IList<AdditionalTagValue> AdditionalTags { get; set; } = new List<AdditionalTagValue>();

        /// <summary>A copy with another Series Description, for a series that has a different purpose in the same study.</summary>
        public ExamIdentity WithSeriesDescription(string description)
        {
            var copy = (ExamIdentity)MemberwiseClone();
            copy.SeriesDescription = description;
            return copy;
        }
    }

    /// <summary>Fills in everything the user has not entered manually.</summary>
    public sealed class IdentityGenerator
    {
        public const string DefaultInstitution = "DCST4 Test Hospital";

        private readonly TestPersonnummerRegistry _registry;
        private readonly Random _random;
        private readonly Func<DateTime> _clock;
        private readonly IUidSource _uids;

        public IdentityGenerator(TestPersonnummerRegistry registry = null, Random random = null, Func<DateTime> clock = null, IUidSource uids = null)
        {
            _registry = registry ?? TestPersonnummerRegistry.Default;
            _random = random ?? new Random();
            _clock = clock ?? (() => DateTime.Now);
            _uids = uids ?? GeneratedUidSource.Instance;
        }

        public ExamIdentity Generate(IdentityOverrides manual, ModalityProfile modality, ExamPreset preset, int minAge = 0, int maxAge = 100)
        {
            manual = manual ?? new IdentityOverrides();
            var now = _clock();
            var studyDate = manual.StudyDateTime ?? now;

            // Each value draws from its own random sequence. Otherwise a manual value would skip some draws and
            // change every Auto value after it, and a preview would jump around while the user types.
            var numberRandom = new Random(_random.Next());
            var nameRandom = new Random(_random.Next());
            var accessionRandom = new Random(_random.Next());
            var studyIdRandom = new Random(_random.Next());
            var referringRandom = new Random(_random.Next());
            var additionalSeed = _random.Next(); // Last, so that the values above are the same with or without additional tags.

            // Birth date and sex follow the personnummer whenever it can be read, so the tags agree with each other.
            Personnummer number = null;
            string patientId;
            if (manual.PatientId == null)
            {
                number = _registry.Pick(numberRandom, now, minAge, maxAge);
                patientId = number.Value;
            }
            else
            {
                patientId = manual.PatientId;
                Personnummer.TryParse(patientId, out number);
            }

            var sex = manual.PatientSex ?? number?.Sex ?? string.Empty;

            return new ExamIdentity
            {
                PatientId = patientId,
                PatientSex = sex,
                PatientBirthDate = manual.PatientBirthDate ?? number?.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
                PatientName = manual.PatientName ?? (manual.PatientNaming == TestPatientNaming.MarkedTestName
                    ? TestPatientNames.Marked(nameRandom)
                    : AutoText(manual, SwedishNames.Pick(nameRandom, sex))),
                AccessionNumber = manual.AccessionNumber ?? IdentifierGenerator.AccessionNumber(accessionRandom, studyDate),
                StudyId = manual.StudyId ?? IdentifierGenerator.StudyId(studyIdRandom),
                StudyInstanceUid = manual.StudyInstanceUid ?? _uids.Uid("study"),
                SeriesInstanceUid = manual.SeriesInstanceUid,
                SopInstanceUid = manual.SopInstanceUid,
                StudyDateTime = studyDate,
                StudyDateTimeIsAuto = manual.StudyDateTime == null,
                ReferringPhysicianName = manual.ReferringPhysicianName ?? (manual.PatientNaming == TestPatientNaming.MarkedTestName
                    ? TestPatientNames.MarkedReferring(referringRandom)
                    : AutoText(manual, SwedishNames.Pick(referringRandom, null))),
                StudyDescription = manual.StudyDescription ?? preset.StudyDescription,
                SeriesDescription = manual.SeriesDescription ?? preset.SeriesDescription,
                InstitutionName = manual.InstitutionName ?? DefaultInstitution,
                StationName = manual.StationName ?? "DCST4-" + modality.Code,
                AdditionalTags = ResolveAdditionalTags(manual, preset, additionalSeed),
            };
        }

        /// <summary>Each tag draws from a sequence of its own, so adding or removing one leaves the others as they are.</summary>
        private static List<AdditionalTagValue> ResolveAdditionalTags(IdentityOverrides manual, ExamPreset preset, int seed)
        {
            var resolved = new List<AdditionalTagValue>();
            if (manual.AdditionalTags == null) return resolved;

            foreach (var item in manual.AdditionalTags)
            {
                var tag = item.Tag;
                var common = CommonTagCatalog.Find(tag);
                var generated = common?.Generate(new Random(seed ^ ((tag.Group << 16) | tag.Element)), preset);
                var value = item.Value
                    ?? (generated == null ? null : AutoText(manual, generated))
                    ?? tag.DefaultValue;
                resolved.Add(new AdditionalTagValue(tag, value));
            }
            return resolved;
        }

        /// <summary>Applied after the draw, so that the character set changes the letters of a name and not who it is.</summary>
        private static string AutoText(IdentityOverrides manual, string generated) =>
            manual.AutoValuesInBasicAscii ? SwedishNames.ToAscii(generated) : generated;
    }
}
