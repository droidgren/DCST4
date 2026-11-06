using DCST4.Core.Generation.Modalities;
using DCST4.Core.TestData;
using System;
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
    }

    /// <summary>Fills in everything the user has not entered manually.</summary>
    public sealed class IdentityGenerator
    {
        public const string DefaultInstitution = "DCST4 Test Hospital";

        private readonly TestPersonnummerRegistry _registry;
        private readonly Random _random;
        private readonly Func<DateTime> _clock;

        public IdentityGenerator(TestPersonnummerRegistry registry = null, Random random = null, Func<DateTime> clock = null)
        {
            _registry = registry ?? TestPersonnummerRegistry.Default;
            _random = random ?? new Random();
            _clock = clock ?? (() => DateTime.Now);
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
                PatientName = manual.PatientName ?? SwedishNames.Pick(nameRandom, sex),
                AccessionNumber = manual.AccessionNumber ?? IdentifierGenerator.AccessionNumber(accessionRandom, studyDate),
                StudyId = manual.StudyId ?? IdentifierGenerator.StudyId(studyIdRandom),
                StudyInstanceUid = manual.StudyInstanceUid ?? IdentifierGenerator.Uid(),
                SeriesInstanceUid = manual.SeriesInstanceUid,
                SopInstanceUid = manual.SopInstanceUid,
                StudyDateTime = studyDate,
                StudyDateTimeIsAuto = manual.StudyDateTime == null,
                ReferringPhysicianName = manual.ReferringPhysicianName ?? SwedishNames.Pick(referringRandom, null),
                StudyDescription = manual.StudyDescription ?? preset.StudyDescription,
                SeriesDescription = manual.SeriesDescription ?? preset.SeriesDescription,
                InstitutionName = manual.InstitutionName ?? DefaultInstitution,
                StationName = manual.StationName ?? "DCST4-" + modality.Code,
            };
        }
    }
}
