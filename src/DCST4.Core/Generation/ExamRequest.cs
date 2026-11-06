using DCST4.Core.Generation.Modalities;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Generation
{
    public static class DicomCharacterSets
    {
        public const string Latin1 = "ISO_IR 100";
        public const string Utf8 = "ISO_IR 192";
    }

    /// <summary>What to generate: one exam with fully resolved identity values.</summary>
    public sealed class ExamRequest
    {
        public const int MinMatrix = 64;
        public const int MaxMatrix = 4096;

        public ModalityProfile Modality { get; set; }
        public ExamPreset Preset { get; set; }
        public ExamIdentity Identity { get; set; }
        public int SeriesCount { get; set; } = 1;
        public int ImagesPerSeries { get; set; } = 1;
        public int Rows { get; set; } = 512;
        public int Columns { get; set; } = 512;
        public int BitsStored { get; set; } = 12;
        public string CharacterSet { get; set; } = DicomCharacterSets.Latin1;

        public int BitsAllocated => BitsStored <= 8 ? 8 : 16;
        public int TotalImages => SeriesCount * ImagesPerSeries;
    }

    /// <summary>Checks an exam before anything is generated, so that problems name the field instead of failing inside fo-dicom.</summary>
    public static class ExamValidator
    {
        public const int MaxTotalImages = 100000;

        /// <summary>Problems that block the run.</summary>
        public static List<string> Validate(ExamRequest request)
        {
            var errors = new List<string>();
            var id = request.Identity;

            if (request.Modality == null) errors.Add("Select a modality.");
            if (request.Preset == null) errors.Add("Select an exam type.");
            if (id == null) { errors.Add("The exam has no identity values."); return errors; }

            if (request.SeriesCount < 1) errors.Add("Number of series must be at least 1.");
            if (request.ImagesPerSeries < 1) errors.Add("Images per series must be at least 1.");
            if ((long)request.SeriesCount * request.ImagesPerSeries > MaxTotalImages) errors.Add($"A run can have at most {MaxTotalImages} images.");
            if (request.Rows < ExamRequest.MinMatrix || request.Rows > ExamRequest.MaxMatrix) errors.Add($"Rows must be between {ExamRequest.MinMatrix} and {ExamRequest.MaxMatrix}.");
            if (request.Columns < ExamRequest.MinMatrix || request.Columns > ExamRequest.MaxMatrix) errors.Add($"Columns must be between {ExamRequest.MinMatrix} and {ExamRequest.MaxMatrix}.");
            if (request.Modality != null && !request.Modality.BitDepths.Contains(request.BitsStored))
            {
                errors.Add($"{request.Modality.Code} does not allow {request.BitsStored} bits stored. Allowed: {string.Join(", ", request.Modality.BitDepths)}.");
            }

            if (request.CharacterSet != DicomCharacterSets.Latin1 && request.CharacterSet != DicomCharacterSets.Utf8)
            {
                errors.Add($"Unsupported Specific Character Set '{request.CharacterSet}'.");
            }

            CheckPersonName(errors, "Patient Name", id.PatientName, request.CharacterSet);
            CheckPersonName(errors, "Referring Physician", id.ReferringPhysicianName, request.CharacterSet);
            CheckText(errors, "Patient ID", id.PatientId, 64, request.CharacterSet);
            CheckText(errors, "Accession Number", id.AccessionNumber, 16, request.CharacterSet);
            CheckText(errors, "Study ID", id.StudyId, 16, request.CharacterSet);
            CheckText(errors, "Study Description", id.StudyDescription, 64, request.CharacterSet);
            CheckText(errors, "Series Description", id.SeriesDescription, 64, request.CharacterSet);
            CheckText(errors, "Institution Name", id.InstitutionName, 64, request.CharacterSet);
            CheckText(errors, "Station Name", id.StationName, 16, request.CharacterSet);

            CheckUid(errors, "Study Instance UID", id.StudyInstanceUid, required: true);
            CheckUid(errors, "Series Instance UID", id.SeriesInstanceUid, required: false);
            CheckUid(errors, "SOP Instance UID", id.SopInstanceUid, required: false);

            if (id.SeriesInstanceUid != null && request.SeriesCount > 1)
            {
                errors.Add("A manual Series Instance UID requires exactly one series.");
            }
            if (id.SopInstanceUid != null && request.TotalImages > 1)
            {
                errors.Add("A manual SOP Instance UID requires exactly one image.");
            }

            if (!string.IsNullOrEmpty(id.PatientBirthDate) && !IsDate(id.PatientBirthDate))
            {
                errors.Add("Patient Birth Date must be a valid date in the form YYYYMMDD.");
            }
            if (!string.IsNullOrEmpty(id.PatientSex) && id.PatientSex != "M" && id.PatientSex != "F" && id.PatientSex != "O")
            {
                errors.Add("Patient Sex must be M, F or O.");
            }

            return errors;
        }

        /// <summary>Things worth telling the user that do not block the run.</summary>
        public static List<string> Warnings(ExamRequest request, TestPersonnummerRegistry registry = null)
        {
            var warnings = new List<string>();
            var id = request.Identity;
            if (id != null && !string.IsNullOrEmpty(id.PatientId) && !(registry ?? TestPersonnummerRegistry.Default).Contains(id.PatientId))
            {
                warnings.Add($"Patient ID '{id.PatientId}' is not in Skatteverket's list of test personnummer. Make sure it is not a real person's number.");
            }
            return warnings;
        }

        private static bool IsDate(string text) =>
            DateTime.TryParseExact(text, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

        private static void CheckPersonName(List<string> errors, string field, string value, string characterSet)
        {
            if (string.IsNullOrEmpty(value)) return;
            CheckCharacters(errors, field, value, characterSet);
            if (value.Split('=').Any(group => group.Split('^').Length > 5))
            {
                errors.Add($"{field} has more than five name components.");
            }
            if (value.Split('=').SelectMany(group => group.Split('^')).Any(component => component.Length > 64))
            {
                errors.Add($"{field} has a name component longer than 64 characters.");
            }
        }

        private static void CheckText(List<string> errors, string field, string value, int maxLength, string characterSet)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.Length > maxLength) errors.Add($"{field} is longer than {maxLength} characters.");
            CheckCharacters(errors, field, value, characterSet);
        }

        private static void CheckCharacters(List<string> errors, string field, string value, string characterSet)
        {
            if (value.IndexOf('\\') >= 0) errors.Add($"{field} must not contain a backslash: DICOM uses it to separate values.");
            if (value.Any(char.IsControl)) errors.Add($"{field} must not contain control characters.");
            if (characterSet == DicomCharacterSets.Latin1 && value.Any(c => c > 0xFF))
            {
                var unsupported = string.Join(" ", value.Where(c => c > 0xFF).Distinct());
                errors.Add($"{field} contains characters that {DicomCharacterSets.Latin1} cannot represent ({unsupported}). Select {DicomCharacterSets.Utf8} or change the value.");
            }
        }

        private static void CheckUid(List<string> errors, string field, string value, bool required)
        {
            if (value == null)
            {
                if (required) errors.Add($"{field} is missing.");
                return;
            }
            if (!IsValidUid(value)) errors.Add($"{field} is not a valid UID: at most 64 characters, digits and dots, no leading zeros in a component.");
        }

        public static bool IsValidUid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (var component in value.Split('.'))
            {
                if (component.Length == 0 || !component.All(c => c >= '0' && c <= '9')) return false;
                if (component.Length > 1 && component[0] == '0') return false;
            }
            return true;
        }
    }
}
