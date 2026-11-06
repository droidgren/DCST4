using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Generation
{
    /// <summary>The tags that can be burned into the image. All have a text value that is in the dataset before the pixels are drawn.</summary>
    public static class BurnedInTagCatalog
    {
        /// <summary>What the program used to burn in. It is a constant so that the App's default setting can use it.</summary>
        public const string DefaultKeywords = "PatientName;PatientID;AccessionNumber;Modality;SeriesNumber;InstanceNumber;ContentDate;ContentTime";

        private const char Separator = ';';

        /// <summary>In the order they are listed in Settings and drawn in the image.</summary>
        public static IReadOnlyList<DicomTag> All { get; } = new[]
        {
            DicomTag.PatientName,
            DicomTag.PatientID,
            DicomTag.PatientBirthDate,
            DicomTag.PatientSex,
            DicomTag.AccessionNumber,
            DicomTag.StudyID,
            DicomTag.StudyDate,
            DicomTag.StudyTime,
            DicomTag.StudyDescription,
            DicomTag.SeriesDescription,
            DicomTag.BodyPartExamined,
            DicomTag.Modality,
            DicomTag.SeriesNumber,
            DicomTag.InstanceNumber,
            DicomTag.ContentDate,
            DicomTag.ContentTime,
            DicomTag.InstitutionName,
            DicomTag.StationName,
            DicomTag.ReferringPhysicianName,
        };

        public static IReadOnlyList<DicomTag> Default { get; } = Parse(DefaultKeywords);

        /// <summary>The saved form of a selection: keywords, so that the setting can be read by a person.</summary>
        public static string Format(IEnumerable<DicomTag> tags) =>
            string.Join(Separator.ToString(), InCatalogOrder(tags).Select(t => t.DictionaryEntry.Keyword));

        /// <summary>Reads a saved selection. Anything that is not in the catalog is ignored.</summary>
        public static IReadOnlyList<DicomTag> Parse(string keywords)
        {
            var saved = (keywords ?? string.Empty).Split(Separator).Select(k => k.Trim()).ToList();
            return All.Where(t => saved.Contains(t.DictionaryEntry.Keyword, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        internal static IEnumerable<DicomTag> InCatalogOrder(IEnumerable<DicomTag> tags)
        {
            var selected = (tags ?? Enumerable.Empty<DicomTag>()).ToList();
            return All.Where(selected.Contains);
        }
    }

    /// <summary>Turns the selected tags into the lines drawn in the image.</summary>
    public static class BurnedInText
    {
        /// <summary>
        /// One line for each selected tag in the dataset. The values are read from the dataset, so that the image shows
        /// exactly what the tags hold. A tag that is present but empty gets a line with no value.
        /// </summary>
        public static IReadOnlyList<string> Lines(DicomDataset dataset, IEnumerable<DicomTag> tags)
        {
            var present = BurnedInTagCatalog.InCatalogOrder(tags).Where(dataset.Contains).ToList();
            if (present.Count == 0) return new string[0];

            var width = present.Max(t => t.DictionaryEntry.Name.Length);
            return present.Select(t => (t.DictionaryEntry.Name + ":").PadRight(width + 2) + Value(dataset, t)).ToList();
        }

        private static string Value(DicomDataset dataset, DicomTag tag) =>
            dataset.TryGetString(tag, out var value) ? value : string.Empty;
    }
}
