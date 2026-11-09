using FellowOakDicom;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DCST4.Core.Generation.Datasets
{
    /// <summary>Makes a dataset harder to store: private tags, values at maximum length, deep nesting.</summary>
    internal static class EdgeCaseVariants
    {
        public const string PrivateCreator = "DCST4 TEST";
        internal const ushort PrivateGroup = 0x0099;
        private const ushort PrivateBlock = 0x0010;

        private const int MaxShortString = 16;
        private const int MaxLongString = 64;
        /// <summary>PN allows 64 characters per component group, carets included.</summary>
        private const int MaxPersonNameGroup = 64;
        private const int MaxShortText = 1024;
        private const int MaxLongText = 10240;

        public static void Apply(DicomDataset dataset, EdgeCases edgeCases, string textSuffix)
        {
            if ((edgeCases & EdgeCases.PrivateTags) != 0) AddPrivateTags(dataset);
            if ((edgeCases & EdgeCases.LongStrings) != 0) AddLongStrings(dataset, textSuffix);
            if ((edgeCases & EdgeCases.NestedSequences) != 0) AddNestedSequences(dataset);
        }

        private static void AddPrivateTags(DicomDataset dataset)
        {
            foreach (var item in new[]
            {
                new AdditionalTagValue(new AdditionalTag(PrivateGroup, 0x1001, "LO", PrivateCreator), "DCST4 private text"),
                new AdditionalTagValue(new AdditionalTag(PrivateGroup, 0x1002, "DS", PrivateCreator), "1.5"),
                new AdditionalTagValue(new AdditionalTag(PrivateGroup, 0x1003, "UL", PrivateCreator), "305419896"),
                new AdditionalTagValue(new AdditionalTag(PrivateGroup, 0x1004, "UT", PrivateCreator), "DCST4 private unlimited text"),
            })
            {
                AdditionalTagWriter.Write(dataset, item);
            }

            dataset.AddOrUpdate(PrivateSequence(0x1010, PrivateSequence(0x1011, PrivateSequence(0x1012, null))));
        }

        /// <summary>A private sequence with one item, which holds the next level or a text at the bottom. Each item names its own creator, as PS3.5 7.8.1 requires.</summary>
        private static DicomSequence PrivateSequence(ushort element, DicomSequence inner)
        {
            var item = new DicomDataset();
            item.Add(DicomVR.LO, new DicomTag(PrivateGroup, PrivateBlock), PrivateCreator);
            if (inner != null) item.Add(inner);
            else item.Add(DicomVR.LO, new DicomTag(PrivateGroup, 0x1013, PrivateCreator), "Innermost level");

            return new DicomSequence(new DicomTag(PrivateGroup, element, PrivateCreator), item);
        }

        private static void AddLongStrings(DicomDataset dataset, string textSuffix)
        {
            var longStrings = new[] { DicomTag.StudyDescription, DicomTag.SeriesDescription, DicomTag.InstitutionName };
            foreach (var tag in longStrings) Replace(dataset, tag, Text(dataset.GetSingleValueOrDefault(tag, string.Empty), MaxLongString, textSuffix));

            foreach (var tag in new[] { DicomTag.ProtocolName }.Where(dataset.Contains)) Replace(dataset, tag, Text(dataset.GetSingleValueOrDefault(tag, string.Empty), MaxLongString, string.Empty));
            foreach (var tag in new[] { DicomTag.StationName, DicomTag.AccessionNumber, DicomTag.StudyID }) Replace(dataset, tag, Text("LONG", MaxShortString, string.Empty));

            var surnameLength = (MaxPersonNameGroup - 1) / 2;
            Replace(dataset, DicomTag.ReferringPhysicianName, Text("Referring", surnameLength, string.Empty) + "^" + Text("Long", MaxPersonNameGroup - 1 - surnameLength, string.Empty));
            Replace(dataset, DicomTag.DerivationDescription, Text("DCST4 long short text", MaxShortText, string.Empty));
            Replace(dataset, DicomTag.PatientComments, Text("DCST4 long text", MaxLongText, string.Empty));

            // Image Comments keeps the project's address first, as the marking of a test image.
            if (dataset.Contains(DicomTag.ImageComments))
            {
                Replace(dataset, DicomTag.ImageComments, Text(DicomImageBuilder.TestImageComment, MaxLongText, string.Empty));
            }
        }

        private static void Replace(DicomDataset dataset, DicomTag tag, string value)
        {
            dataset.AddOrUpdate(tag, value);
        }

        /// <summary>The text, filled up to exactly this length, with the suffix last.</summary>
        internal static string Text(string start, int length, string suffix)
        {
            var filler = new StringBuilder(start);
            while (filler.Length < length) filler.Append(filler.Length % 2 == 0 ? 'X' : 'Y');
            var text = filler.ToString(0, length - suffix.Length);
            return text + suffix;
        }

        private static void AddNestedSequences(DicomDataset dataset)
        {
            // Protocol Context Sequence holds content items, which need a value type and a concept name.
            var innermost = new DicomDataset
            {
                { DicomTag.ValueType, "TEXT" },
                new DicomSequence(DicomTag.ConceptNameCodeSequence, new DicomDataset
                {
                    { DicomTag.CodeValue, "DCST4-4" },
                    { DicomTag.CodingSchemeDesignator, "99DCST4" },
                    { DicomTag.CodeMeaning, "Third level" },
                }),
                { DicomTag.TextValue, "DCST4-3" },
            };
            var protocol = new DicomDataset
            {
                { DicomTag.CodeValue, "DCST4-2" },
                { DicomTag.CodingSchemeDesignator, "99DCST4" },
                { DicomTag.CodeMeaning, "Second level" },
                new DicomSequence(DicomTag.ProtocolContextSequence, innermost),
            };
            dataset.AddOrUpdate(new DicomSequence(DicomTag.RequestAttributesSequence, new DicomDataset
            {
                { DicomTag.RequestedProcedureID, "DCST4-1" },
                { DicomTag.ScheduledProcedureStepID, "DCST4-1" },
                new DicomSequence(DicomTag.ScheduledProtocolCodeSequence, protocol),
            }));
        }
    }
}
