using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Generation.Datasets
{
    /// <summary>Which Specific Character Set a dataset uses, and whether its text holds å, ä and ö on purpose.</summary>
    public enum CharacterSetVariant
    {
        /// <summary>ISO_IR 100, with whatever names are drawn.</summary>
        Default,
        /// <summary>The tag is empty, which means Basic ASCII. Letters that the set cannot hold lose their diacritics.</summary>
        EmptyAscii,
        Latin1Swedish,
        Utf8Swedish,
    }

    [Flags]
    public enum EdgeCases
    {
        None = 0,
        /// <summary>Private elements under an own private creator, including a nested private sequence.</summary>
        PrivateTags = 1,
        /// <summary>Text values at the maximum length of their VR.</summary>
        LongStrings = 2,
        /// <summary>Sequences nested three levels deep.</summary>
        NestedSequences = 4,
    }

    /// <summary>What to generate. Zero for Rows, Columns, Bits Stored or Frames means the profile's default.</summary>
    public sealed class TestDatasetRequest
    {
        public const int MaxFrames = 10000;

        /// <summary>Everything dated follows from this, so that the same request gives the same result on any day.</summary>
        public static readonly DateTime DefaultReferenceDate = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Unspecified);

        public string ProfileId { get; set; }
        public int Seed { get; set; } = 1;
        /// <summary>Null or empty means 2.25 and a value hashed from the seed and counters.</summary>
        public string UidRoot { get; set; }
        public DateTime ReferenceDate { get; set; } = DefaultReferenceDate;
        public TestPatientNaming PatientNaming { get; set; } = TestPatientNaming.RegistrySwedishName;
        public int Patients { get; set; } = 1;
        public int StudiesPerPatient { get; set; } = 1;
        public int SeriesPerStudy { get; set; } = 1;
        public int InstancesPerSeries { get; set; } = 1;
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int BitsStored { get; set; }
        public int Frames { get; set; }
        public DicomTransferSyntax TransferSyntax { get; set; } = DicomTransferSyntax.ExplicitVRLittleEndian;
        public CharacterSetVariant CharacterSet { get; set; } = CharacterSetVariant.Default;
        public EdgeCases EdgeCases { get; set; } = EdgeCases.None;
        /// <summary>Null means the embedded list.</summary>
        public TestPersonnummerRegistry Registry { get; set; }

        internal DatasetProfile Profile => DatasetProfileCatalog.Find(ProfileId);

        internal int ResolvedRows => Rows > 0 ? Rows : Profile.Image?.DefaultRows ?? ExamRequestDefaults.ObjectMatrix;
        internal int ResolvedColumns => Columns > 0 ? Columns : Profile.Image?.DefaultColumns ?? ExamRequestDefaults.ObjectMatrix;
        internal int ResolvedBits => BitsStored > 0 ? BitsStored : Profile.Image?.DefaultBitDepth ?? 0;
        internal int ResolvedFrames => Frames > 0 ? Frames : Profile.DefaultFrames;
        internal string CharacterSetTag => CharacterSet == CharacterSetVariant.EmptyAscii ? DicomCharacterSets.Empty
            : CharacterSet == CharacterSetVariant.Utf8Swedish ? DicomCharacterSets.Utf8 : DicomCharacterSets.Latin1;

        /// <summary>Every problem with the request. Empty if it can be generated.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            var profile = Profile;
            if (profile == null)
            {
                problems.Add($"Unknown dataset profile '{ProfileId}'. Known profiles: {string.Join(", ", DatasetProfileCatalog.All.Select(p => p.Id))}.");
                return problems;
            }

            if (Patients < 1) problems.Add("Patients must be at least 1.");
            if (StudiesPerPatient < 1) problems.Add("Studies per patient must be at least 1.");
            if (SeriesPerStudy < 1) problems.Add("Series per study must be at least 1.");
            if (InstancesPerSeries < 1) problems.Add("Instances per series must be at least 1.");
            if (problems.Count == 0 && (long)Patients * StudiesPerPatient * SeriesPerStudy * InstancesPerSeries > ExamValidator.MaxTotalImages)
            {
                problems.Add($"A dataset can have at most {ExamValidator.MaxTotalImages} instances.");
            }

            if (!string.IsNullOrWhiteSpace(UidRoot) && (!ExamValidator.IsValidUid(UidRoot.Trim()) || UidRoot.Trim().Length > DeterministicUidSource.MaxRootLength))
            {
                problems.Add($"UID root '{UidRoot}' is invalid. It must be at most {DeterministicUidSource.MaxRootLength} characters, digits and dots, with no leading zeros in a component.");
            }

            if (TransferSyntax == null) problems.Add("No transfer syntax is selected.");
            else if (TransferSyntax.IsEncapsulated)
            {
                if (!profile.HasPixelData) problems.Add($"{profile.DisplayName} has no pixel data, so {TransferSyntax.UID.Name} does not apply.");
                else if (profile.Image.IsBinaryMask) problems.Add($"{profile.DisplayName} is generated uncompressed only.");
                else if (profile.Image.RendersFramesOnDemand) problems.Add($"{profile.DisplayName} is generated uncompressed only, since compressing it would need the whole object in memory.");
            }

            if (profile.IsImage) ValidateImage(problems, profile);
            return problems;
        }

        private void ValidateImage(List<string> problems, DatasetProfile profile)
        {
            var image = profile.Image;
            var rows = ResolvedRows;
            var columns = ResolvedColumns;

            if (rows < ExamRequest.MinMatrix || rows > ExamRequest.MaxMatrix) problems.Add($"Rows must be between {ExamRequest.MinMatrix} and {ExamRequest.MaxMatrix}.");
            if (columns < ExamRequest.MinMatrix || columns > ExamRequest.MaxMatrix) problems.Add($"Columns must be between {ExamRequest.MinMatrix} and {ExamRequest.MaxMatrix}.");
            if (image.IsBinaryMask && (rows % 8 != 0 || columns % 8 != 0)) problems.Add($"{profile.DisplayName} needs Rows and Columns that are multiples of 8, so that each frame fills whole bytes.");

            if (!image.BitDepths.Contains(ResolvedBits))
            {
                problems.Add($"{profile.DisplayName} does not allow {ResolvedBits.ToString(CultureInfo.InvariantCulture)} bits stored. Allowed: {string.Join(", ", image.BitDepths)}.");
            }

            var frames = ResolvedFrames;
            if (frames < 1 || frames > MaxFrames) problems.Add($"Frames must be between 1 and {MaxFrames}.");
            else if (frames > 1 && !image.IsMultiFrame) problems.Add($"{profile.DisplayName} images have one frame.");
        }
    }

    internal static class ExamRequestDefaults
    {
        /// <summary>The matrix of the image that a non-image object refers to.</summary>
        public const int ObjectMatrix = 256;
    }
}
