using DCST4.Core.Generation;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DCST4.Core.Files
{
    /// <summary>The test patient that replaces one patient of a folder.</summary>
    public sealed class TestPatient
    {
        internal TestPatient(Personnummer number, string name)
        {
            Number = number;
            Name = name;
        }

        public Personnummer Number { get; }
        public string PatientId => Number.Value;
        public string Name { get; }
        public string BirthDate => Number.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        public string Sex => Number.Sex;
        public override string ToString() => PatientId + " " + Name;
    }

    /// <summary>
    /// Makes test data of existing files: each patient becomes a patient from <see cref="TestPersonnummerRegistry"/>, each study
    /// gets a new accession number and study ID, every UID of an instance is replaced by one under the profile's root, and the
    /// objects get DCST4's marking. Everything else, private tags included, stays as the modality wrote it, since that is the
    /// point of sending real files.
    /// </summary>
    /// <remarks>
    /// UIDs are mapped by a hash of the run's nonce and the original UID, with no table: the same UID gives the same new UID
    /// in every file of a run, so references between instances still hold, and a second run gives other UIDs, since nothing
    /// can be deleted from the system under test.
    /// </remarks>
    public sealed class FileReidentifier
    {
        /// <summary>The UIDs DICOM defines (SOP classes, transfer syntaxes, coding schemes...), which never identify an instance.</summary>
        public const string DicomUidRoot = "1.2.840.10008.";

        /// <summary>The marking of a generated object, which a re-identified one gets too.</summary>
        public const string Manufacturer = "DCST4";

        /// <summary>Tags that name a class, a syntax or a scheme rather than an instance. Their UIDs are kept even outside the DICOM root.</summary>
        private static readonly HashSet<DicomTag> KeptUidTags = new HashSet<DicomTag>
        {
            DicomTag.SOPClassUID, DicomTag.ReferencedSOPClassUID, DicomTag.MediaStorageSOPClassUID, DicomTag.AffectedSOPClassUID,
            DicomTag.RequestedSOPClassUID, DicomTag.ReferencedSOPClassUIDInFile, DicomTag.TransferSyntaxUID,
            DicomTag.ReferencedTransferSyntaxUIDInFile, DicomTag.ImplementationClassUID, DicomTag.CodingSchemeUID,
            DicomTag.PrivateInformationCreatorUID, DicomTag.ContextGroupExtensionCreatorUID, DicomTag.MappingResourceUID,
            DicomTag.RelatedGeneralSOPClassUID, DicomTag.OriginalSpecializedSOPClassUID, DicomTag.SOPClassesInStudy,
        };

        /// <summary>Identify the patient beside name, ID, birth date and sex, and are removed wherever they are.</summary>
        private static readonly HashSet<DicomTag> RemovedTags = new HashSet<DicomTag>
        {
            DicomTag.OtherPatientIDsRETIRED, DicomTag.OtherPatientIDsSequence, DicomTag.OtherPatientNames, DicomTag.PatientBirthName,
            DicomTag.PatientMotherBirthName, DicomTag.PatientAddress, DicomTag.PatientTelephoneNumbers, DicomTag.PatientBirthTime,
            DicomTag.MedicalRecordLocatorRETIRED, DicomTag.PatientComments, DicomTag.IssuerOfPatientID,
            DicomTag.IssuerOfPatientIDQualifiersSequence, DicomTag.AdditionalPatientHistory, DicomTag.PatientInsurancePlanCodeSequence,
            DicomTag.MilitaryRank, DicomTag.BranchOfService, DicomTag.CountryOfResidence, DicomTag.RegionOfResidence,
            DicomTag.PatientTelecomInformation, DicomTag.ResponsiblePerson,
        };

        /// <summary>
        /// Every other name of a person, of staff or in a report, private tags included. A name rather than an empty value,
        /// since some are Type 1, such as the Person Name of a PNAME item in a structured report.
        /// </summary>
        public const string OtherPersonName = "DCST4^PERSON";

        /// <summary>A private value of an implicit VR file has no VR, but text like this in it is a UID.</summary>
        private static readonly Regex UidLike = new Regex(@"^[0-9]+(\.[0-9]+){4,}$", RegexOptions.CultureInvariant);
        private const int MinUidLikeLength = 20;

        private readonly DeterministicUidSource _uids;
        private readonly Dictionary<string, TestPatient> _patients = new Dictionary<string, TestPatient>(StringComparer.Ordinal);
        private readonly Dictionary<string, StudyValues> _studies = new Dictionary<string, StudyValues>(StringComparer.Ordinal);
        private readonly TestPatientNaming _naming;

        /// <param name="scan">The files to be re-identified. Every patient and study gets its values here, in the order sent.</param>
        /// <param name="onePatient">Every file gets the same test patient, which a load run needs, since it puts files of any source patient in one study.</param>
        public FileReidentifier(FolderScan scan, string uidRoot, string runNonce, TestPatientNaming naming,
            TestPersonnummerRegistry registry = null, int? seed = null, DateTime? today = null, bool onePatient = false)
        {
            if (scan == null) throw new ArgumentNullException(nameof(scan));
            if (string.IsNullOrWhiteSpace(runNonce)) throw new ArgumentException("A run needs a nonce of its own.", nameof(runNonce));
            registry = registry ?? TestPersonnummerRegistry.Default;
            _naming = naming;
            _uids = new DeterministicUidSource(uidRoot, "files/" + runNonce);
            var random = new Random(seed ?? SeededValues.Int("files/" + runNonce));
            var now = (today ?? DateTime.Today).Date;

            TestPatient shared = null;
            foreach (var file in scan.Files)
            {
                if (!_patients.ContainsKey(file.PatientKey))
                {
                    // Born before the study, and about as old then as the patient replaced, so that the dates still agree.
                    var onDate = file.StudyDate.HasValue && file.StudyDate.Value <= now ? file.StudyDate.Value : now;
                    var patient = onePatient && shared != null ? shared : NewPatient(registry, random, onDate, file.PatientAge, file.PatientSex);
                    shared = shared ?? patient;
                    _patients[file.PatientKey] = patient;
                }
                if (!_studies.ContainsKey(file.StudyInstanceUid))
                {
                    _studies[file.StudyInstanceUid] = new StudyValues
                    {
                        AccessionNumber = IdentifierGenerator.AccessionNumber(random, file.StudyDate ?? now),
                        StudyId = IdentifierGenerator.StudyId(random),
                        ReferringPhysicianName = naming == TestPatientNaming.MarkedTestName ? TestPatientNames.MarkedReferring(random) : SwedishNames.Pick(random, null),
                    };
                }
            }
        }

        /// <summary>The test patients, in the order of the files. One per patient of the folder, or one in all.</summary>
        public IReadOnlyList<TestPatient> Patients => _patients.Values.Distinct().ToList();

        private sealed class StudyValues
        {
            public string AccessionNumber;
            public string StudyId;
            public string ReferringPhysicianName;
        }

        private TestPatient NewPatient(TestPersonnummerRegistry registry, Random random, DateTime onDate, int? age, string sex)
        {
            var number = registry.PickNear(random, onDate, age, sex);
            var name = _naming == TestPatientNaming.MarkedTestName ? TestPatientNames.Marked(random) : SwedishNames.Pick(random, number.Sex);
            return new TestPatient(number, name);
        }

        /// <summary>A new UID for an instance, series, study or anything else a file refers to. The same in every file of the run.</summary>
        public string MapUid(string original)
        {
            var value = (original ?? string.Empty).Trim().TrimEnd('\0').Trim();
            if (value.Length == 0 || value.StartsWith(DicomUidRoot, StringComparison.Ordinal)) return value;
            return _uids.Uid("map/" + value);
        }

        /// <summary>Re-identifies a file in memory, and burns the watermark into native pixels. The file on disk is not touched.</summary>
        public WatermarkOutcome Apply(DicomFile file, ScannedFile scanned)
        {
            var dataset = file.Dataset;
            var patient = _patients[scanned.PatientKey];
            var study = _studies[scanned.StudyInstanceUid];

            // Without a character set a file holds Basic ASCII only, so a Swedish name loses its å, ä and ö rather than the file getting a new character set.
            var ascii = string.IsNullOrWhiteSpace(FolderScanner.Text(dataset, DicomTag.SpecificCharacterSet));
            var names = new Names
            {
                Patient = ascii ? SwedishNames.ToAscii(patient.Name) : patient.Name,
                Referring = ascii ? SwedishNames.ToAscii(study.ReferringPhysicianName) : study.ReferringPhysicianName,
            };

            Rewrite(dataset, patient, study, names, true);
            UpdatePatientAge(dataset, patient);

            dataset.AddOrUpdate(DicomTag.Manufacturer, Manufacturer);
            if (dataset.Contains(DicomTag.PixelData)) dataset.AddOrUpdate(DicomTag.ImageComments, DicomImageBuilder.TestImageComment);

            var sopInstance = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
            if (file.FileMetaInfo != null)
            {
                file.FileMetaInfo.NotValidated().AddOrUpdate(DicomTag.MediaStorageSOPInstanceUID, sopInstance);
            }

            return PixelWatermark.Apply(dataset);
        }

        private sealed class Names
        {
            public string Patient;
            public string Referring;
        }

        private void Rewrite(DicomDataset dataset, TestPatient patient, StudyValues study, Names names, bool top)
        {
            // Values written by a modality do not always pass fo-dicom's checks, and they are sent as they are.
            dataset.NotValidated();
            foreach (var item in dataset.ToList())
            {
                var tag = item.Tag;
                if (RemovedTags.Contains(tag))
                {
                    dataset.Remove(tag);
                    continue;
                }
                if (item is DicomSequence sequence)
                {
                    foreach (var child in sequence.Items) Rewrite(child, patient, study, names, false);
                    continue;
                }
                if (!(item is DicomElement element)) continue;

                if (tag == DicomTag.PatientID) dataset.AddOrUpdate(DicomTag.PatientID, patient.PatientId);
                else if (tag == DicomTag.PatientName) dataset.AddOrUpdate(DicomTag.PatientName, names.Patient);
                else if (tag == DicomTag.PatientBirthDate) dataset.AddOrUpdate(DicomTag.PatientBirthDate, patient.BirthDate);
                else if (tag == DicomTag.PatientSex) dataset.AddOrUpdate(DicomTag.PatientSex, patient.Sex);
                else if (tag == DicomTag.AccessionNumber) ReplaceIfSet(dataset, element, study.AccessionNumber);
                else if (tag == DicomTag.StudyID) ReplaceIfSet(dataset, element, study.StudyId);
                else if (tag == DicomTag.ReferringPhysicianName) ReplaceIfSet(dataset, element, names.Referring);
                else if (element.ValueRepresentation == DicomVR.PN) ReplaceIfSet(dataset, element, OtherPersonName);
                else if (element.ValueRepresentation == DicomVR.UI) MapUids(dataset, element);
                else if (element.ValueRepresentation == DicomVR.UN && tag.IsPrivate) MapUidInUnknown(dataset, element);
            }

            // A file must say who the patient is, even if the modality left the tags out.
            if (top)
            {
                dataset.AddOrUpdate(DicomTag.PatientID, patient.PatientId);
                dataset.AddOrUpdate(DicomTag.PatientName, names.Patient);
                dataset.AddOrUpdate(DicomTag.PatientBirthDate, patient.BirthDate);
                dataset.AddOrUpdate(DicomTag.PatientSex, patient.Sex);
            }
        }

        /// <summary>An empty value stays empty, as the modality sent it: a Type 2 attribute without a value is worth testing too.</summary>
        private static void ReplaceIfSet(DicomDataset dataset, DicomElement element, string value)
        {
            if (element.Buffer.Size == 0 || Encoding.ASCII.GetString(element.Buffer.Data).Trim('\0', ' ').Length == 0) return;
            dataset.AddOrUpdate(element.ValueRepresentation, element.Tag, value);
        }

        private void MapUids(DicomDataset dataset, DicomElement element)
        {
            if (element.Count == 0 || KeepsUid(element.Tag)) return;
            var values = Encoding.ASCII.GetString(element.Buffer.Data).TrimEnd('\0', ' ').Split('\\');
            var mapped = values.Select(MapUid).ToArray();
            if (values.SequenceEqual(mapped)) return;
            dataset.AddOrUpdate(DicomVR.UI, element.Tag, mapped);
        }

        private void MapUidInUnknown(DicomDataset dataset, DicomElement element)
        {
            var size = element.Buffer.Size;
            if (size < MinUidLikeLength || size > DeterministicUidSource.MaxUidLength + 1) return;
            var text = Encoding.ASCII.GetString(element.Buffer.Data).TrimEnd('\0', ' ');
            if (text.Length < MinUidLikeLength || !UidLike.IsMatch(text)) return;
            var mapped = MapUid(text);
            if (mapped == text) return;
            var bytes = Encoding.ASCII.GetBytes(mapped.Length % 2 == 1 ? mapped + "\0" : mapped);
            dataset.AddOrUpdate(new DicomUnknown(element.Tag, new FellowOakDicom.IO.Buffer.MemoryByteBuffer(bytes)));
        }

        private static bool KeepsUid(DicomTag tag)
        {
            if (KeptUidTags.Contains(tag)) return true;
            var keyword = tag.DictionaryEntry?.Keyword ?? string.Empty;
            return keyword.EndsWith("ClassUID", StringComparison.Ordinal) || keyword.IndexOf("CodingScheme", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Patient's Age follows the new birth date, on the study date, in the form AS allows.</summary>
        private static void UpdatePatientAge(DicomDataset dataset, TestPatient patient)
        {
            if (!dataset.Contains(DicomTag.PatientAge) || dataset.GetDicomItem<DicomElement>(DicomTag.PatientAge)?.Buffer.Size == 0) return;
            var on = FolderScanner.Date(dataset, DicomTag.StudyDate) ?? FolderScanner.Date(dataset, DicomTag.SeriesDate) ?? FolderScanner.Date(dataset, DicomTag.AcquisitionDate) ?? DateTime.Today;
            dataset.AddOrUpdate(DicomTag.PatientAge, Age(patient.Number.BirthDate, on));
        }

        /// <summary>An age as Patient's Age (AS) writes it: years, or months or days under one year.</summary>
        public static string Age(DateTime birth, DateTime on)
        {
            if (on < birth) on = birth;
            var years = TestPersonnummerRegistry.AgeOn(birth, on);
            if (years >= 1) return Math.Min(999, years).ToString("000", CultureInfo.InvariantCulture) + "Y";
            var months = (on.Year - birth.Year) * 12 + on.Month - birth.Month - (on.Day < birth.Day ? 1 : 0);
            if (months >= 1) return months.ToString("000", CultureInfo.InvariantCulture) + "M";
            return ((int)(on - birth).TotalDays).ToString("000", CultureInfo.InvariantCulture) + "D";
        }
    }
}
