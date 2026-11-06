using DCST4.Core.Generation.Modalities;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DCST4.Core.Generation
{
    /// <summary>A tag the user has added on top of the fixed identifier fields.</summary>
    public sealed class AdditionalTag
    {
        public AdditionalTag(ushort group, ushort element, string vrCode, string privateCreator = null, string defaultValue = null)
        {
            Group = group;
            Element = element;
            VrCode = vrCode ?? string.Empty;
            PrivateCreator = string.IsNullOrWhiteSpace(privateCreator) ? null : privateCreator.Trim();
            DefaultValue = defaultValue ?? string.Empty;
        }

        public ushort Group { get; }
        public ushort Element { get; }
        public string VrCode { get; }
        /// <summary>Required for a private tag (odd group), otherwise null.</summary>
        public string PrivateCreator { get; }
        /// <summary>What Auto uses for a custom tag. Common tags generate their value instead.</summary>
        public string DefaultValue { get; }

        public bool IsPrivate => (Group & 1) == 1;
        public string Number => "(" + Group.ToString("X4", CultureInfo.InvariantCulture) + "," + Element.ToString("X4", CultureInfo.InvariantCulture) + ")";
        /// <summary>Identifies the tag in lists: the same number under another private creator is another tag.</summary>
        public string Key => Number + (IsPrivate ? " " + PrivateCreator : string.Empty);

        /// <summary>The standard's name, or the number for a tag the dictionary does not know.</summary>
        public string Name
        {
            get
            {
                var entry = AdditionalTagParser.DictionaryEntry(Group, Element);
                return entry == null ? Number : entry.Name;
            }
        }

        /// <summary>For messages and the log: name and number, or only the number if there is no name.</summary>
        public string Label => Name == Number ? Number : Name + " " + Number;

        /// <summary>
        /// Written as a private tag once the creator is known. fo-dicom then finds the creator's block itself, which is
        /// why <see cref="AdditionalTagWriter"/> puts the creator in the block the user asked for first.
        /// </summary>
        public DicomTag DicomTag => IsPrivate && PrivateCreator != null ? new DicomTag(Group, Element, PrivateCreator) : new DicomTag(Group, Element);
    }

    /// <summary>A tag with its value. In <see cref="IdentityOverrides"/> a null value means Auto; in <see cref="ExamIdentity"/> it is final.</summary>
    public sealed class AdditionalTagValue
    {
        public AdditionalTagValue(AdditionalTag tag, string value)
        {
            Tag = tag;
            Value = value;
        }

        public AdditionalTag Tag { get; }
        public string Value { get; }
    }

    /// <summary>One entry in the Settings checklist.</summary>
    public sealed class CommonTag
    {
        private readonly Func<Random, ExamPreset, string> _auto;

        internal CommonTag(DicomTag tag, Func<Random, ExamPreset, string> auto)
        {
            Tag = new AdditionalTag(tag.Group, tag.Element, tag.DictionaryEntry.ValueRepresentations[0].Code);
            _auto = auto;
        }

        public AdditionalTag Tag { get; }
        public string Name => Tag.Name;

        public string Generate(Random random, ExamPreset preset) => _auto(random, preset);
    }

    /// <summary>
    /// Attributes at the top level of the image IODs that a PACS test often wants filled in. Left out on purpose: Other
    /// Patient IDs, since random patient IDs may only come from TestPersonnummerRegistry, and Requested Procedure ID and
    /// Scheduled Procedure Step ID, which in an image belong inside Request Attributes Sequence.
    /// </summary>
    public static class CommonTagCatalog
    {
        private static readonly Func<Random, ExamPreset, string> PersonName = (random, preset) => SwedishNames.Pick(random, null);
        private static readonly Func<Random, ExamPreset, string> Digits = (random, preset) => IdentifierGenerator.StudyId(random);

        private static Func<Random, ExamPreset, string> Fixed(string text) => (random, preset) => text;

        public static IReadOnlyList<CommonTag> All { get; } = new[]
        {
            new CommonTag(DicomTag.OperatorsName, PersonName),
            new CommonTag(DicomTag.PerformingPhysicianName, PersonName),
            new CommonTag(DicomTag.NameOfPhysiciansReadingStudy, PersonName),
            new CommonTag(DicomTag.InstitutionalDepartmentName, Fixed("DCST4 Test Department")),
            new CommonTag(DicomTag.IssuerOfPatientID, Fixed("DCST4")),
            new CommonTag(DicomTag.PatientComments, Fixed("DCST4 test patient")),
            new CommonTag(DicomTag.AdditionalPatientHistory, Fixed("DCST4 test patient, no real history")),
            new CommonTag(DicomTag.AdmissionID, Digits),
            new CommonTag(DicomTag.PerformedProcedureStepID, Digits),
            new CommonTag(DicomTag.PerformedProcedureStepDescription, (random, preset) => preset.StudyDescription ?? preset.Name),
        };

        public static CommonTag Find(AdditionalTag tag) =>
            tag.IsPrivate ? null : All.FirstOrDefault(c => c.Tag.Group == tag.Group && c.Tag.Element == tag.Element);
    }

    /// <summary>Turns what the user typed into a tag.</summary>
    public static class AdditionalTagParser
    {
        private static readonly Regex NumberPattern = new Regex(@"^\(?\s*([0-9A-Fa-f]{4})\s*,?\s*([0-9A-Fa-f]{4})\s*\)?$", RegexOptions.CultureInvariant);

        /// <summary>Accepts (0008,1070), 0008,1070 and 00081070.</summary>
        public static bool TryParseNumber(string text, out ushort group, out ushort element)
        {
            group = 0;
            element = 0;
            var match = NumberPattern.Match(text ?? string.Empty);
            if (!match.Success) return false;
            group = ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            element = ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Finds a tag by number, keyword or name. Returns false for text that is none of these.</summary>
        public static bool TryResolve(string text, out ushort group, out ushort element)
        {
            text = text?.Trim() ?? string.Empty;
            if (TryParseNumber(text, out group, out element)) return true;
            if (text.Length == 0) return false;

            // Current attributes first: a retired one can share its name with a newer one.
            var entry = DicomDictionary.Default
                .Where(e => string.Equals(e.Keyword, text, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.IsRetired)
                .FirstOrDefault();
            if (entry == null) return false;
            group = entry.Tag.Group;
            element = entry.Tag.Element;
            return true;
        }

        /// <summary>The dictionary's entry, or null for a private tag or one the dictionary does not know.</summary>
        public static DicomDictionaryEntry DictionaryEntry(ushort group, ushort element)
        {
            if ((group & 1) == 1) return null;
            var entry = DicomDictionary.Default[new DicomTag(group, element)];
            return entry == null || entry == DicomDictionary.UnknownTag || entry == DicomDictionary.PrivateCreatorTag ? null : entry;
        }

        /// <summary>The VRs the dictionary gives the tag that can be typed as text. Empty if the user has to choose.</summary>
        public static IReadOnlyList<string> KnownVrs(ushort group, ushort element)
        {
            var entry = DictionaryEntry(group, element);
            return entry == null ? new string[0] : entry.ValueRepresentations.Select(v => v.Code).Where(AdditionalTagRules.TextVrs.Contains).ToArray();
        }

        /// <summary>Creates a checked custom tag from the fields in Settings, or returns null and says what is wrong.</summary>
        public static AdditionalTag Create(string tagText, string vrCode, string privateCreator, string defaultValue, out string problem)
        {
            if (!TryResolve(tagText, out var group, out var element))
            {
                problem = $"'{tagText?.Trim()}' is not a tag. Enter a number such as (0008,1070) or a keyword such as OperatorsName.";
                return null;
            }

            // A tag with only one possible VR needs no choice, whatever the box says. One that cannot be typed, such as a
            // sequence, keeps its real VR so that the rules give the real reason.
            var known = KnownVrs(group, element);
            var entry = DictionaryEntry(group, element);
            var vr = known.Count == 1 ? known[0]
                : known.Count == 0 && entry != null ? entry.ValueRepresentations[0].Code
                : vrCode?.Trim();
            var tag = new AdditionalTag(group, element, vr, (group & 1) == 1 ? privateCreator : null, defaultValue?.Trim());

            problem = AdditionalTagRules.CheckTag(tag) ?? AdditionalTagRules.CheckValue(tag, tag.DefaultValue, DicomCharacterSets.Utf8);
            return problem == null ? tag : null;
        }
    }

    /// <summary>What may be added, so that a value cannot break the image or remove its marking as a test image.</summary>
    public static class AdditionalTagRules
    {
        /// <summary>VRs with a value that can be typed. Sequences, binary data and AT are not supported.</summary>
        public static readonly IReadOnlyList<string> TextVrs = new[]
        {
            "AE", "AS", "CS", "DA", "DS", "DT", "FD", "FL", "IS", "LO", "LT", "PN", "SH", "SL", "SS", "ST", "TM", "UC", "UI", "UL", "UR", "US", "UT",
        };

        private static readonly HashSet<string> NumericVrs = new HashSet<string> { "FD", "FL", "SL", "SS", "UL", "US" };
        private static readonly HashSet<string> SingleValueVrs = new HashSet<string> { "LT", "ST", "UR", "UT" };

        /// <summary>These have a field of their own on the Identifiers tab.</summary>
        private static readonly DicomTag[] IdentifierFieldTags =
        {
            DicomTag.PatientName, DicomTag.PatientID, DicomTag.PatientBirthDate, DicomTag.PatientSex, DicomTag.AccessionNumber, DicomTag.StudyID,
            DicomTag.StudyInstanceUID, DicomTag.SeriesInstanceUID, DicomTag.SOPInstanceUID, DicomTag.StudyDate, DicomTag.StudyTime,
            DicomTag.ReferringPhysicianName, DicomTag.StudyDescription, DicomTag.SeriesDescription, DicomTag.InstitutionName, DicomTag.StationName,
        };

        /// <summary>These keep the image valid and marked as a test image. Burned In Annotation is safe too, being in group 0028.</summary>
        private static readonly DicomTag[] ProtectedTags =
        {
            DicomTag.SOPClassUID, DicomTag.Modality, DicomTag.SpecificCharacterSet, DicomTag.ImageComments,
        };

        /// <summary>Whether the tag itself can be added, whatever its value. Null if it can.</summary>
        public static string CheckTag(AdditionalTag tag)
        {
            var label = tag.Number;
            if (tag.Group <= 0x0007) return $"{label} cannot be added: groups 0000 to 0007 are not part of the image data.";
            if (tag.Group >= 0xFFFE) return $"{label} cannot be added: this group is reserved by DICOM.";
            if (tag.Element == 0x0000) return $"{label} cannot be added: it is a group length.";

            if (tag.IsPrivate)
            {
                if (tag.PrivateCreator == null) return $"{label} is a private tag, so it needs a private creator.";
                if (tag.PrivateCreator.Length > 64) return $"The private creator of {label} is longer than 64 characters.";
                if (tag.PrivateCreator.IndexOf('\\') >= 0 || tag.PrivateCreator.Any(char.IsControl)) return $"The private creator of {label} contains a backslash or a control character.";
                if (tag.Element < 0x1000) return $"{label} cannot be added: private data elements are (gggg,1000) to (gggg,FFFF), and the creator element is written automatically.";
            }
            else
            {
                if (tag.Group == 0x0028 || tag.Group == 0x7FE0) return $"{label} cannot be added: groups 0028 and 7FE0 describe the pixel data, which the program writes itself.";
                if (Contains(IdentifierFieldTags, tag)) return $"{tag.Name} {label} already has a field on the Identifiers tab. Use that field.";
                if (Contains(ProtectedTags, tag)) return $"{tag.Name} {label} cannot be changed: the program needs it to keep the image valid and marked as a test image.";
            }

            if (string.IsNullOrEmpty(tag.VrCode)) return $"Select a VR for {label}.";
            if (!TextVrs.Contains(tag.VrCode))
            {
                return $"{label} cannot be added with VR {tag.VrCode}: sequences, binary data and attribute tags are not supported.";
            }

            var entry = AdditionalTagParser.DictionaryEntry(tag.Group, tag.Element);
            if (entry != null && entry.ValueRepresentations.All(v => v.Code != tag.VrCode))
            {
                var allowed = string.Join(" or ", entry.ValueRepresentations.Select(v => v.Code));
                return $"{tag.Name} {label} must have VR {allowed}, not {tag.VrCode}.";
            }
            return null;
        }

        /// <summary>Whether this value can be written to the tag in this character set. Null if it can.</summary>
        public static string CheckValue(AdditionalTag tag, string value, string characterSet)
        {
            value = value ?? string.Empty;
            var label = tag.Label;
            if (!AllowsSeveralValues(tag) && value.IndexOf('\\') >= 0)
            {
                return $"{label} allows only one value, and DICOM uses a backslash to separate values.";
            }

            if (!NumericVrs.Contains(tag.VrCode))
            {
                var problems = new List<string>();
                ExamValidator.CheckCharacters(problems, label, value, characterSet, allowBackslash: true);
                if (problems.Count > 0) return problems[0];
            }

            try
            {
                var scratch = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
                scratch.Add(DicomTag.SpecificCharacterSet, characterSet);
                AdditionalTagWriter.Write(scratch, new AdditionalTagValue(tag, value));
                return null;
            }
            catch (FormatException)
            {
                return $"{label} must be a number that fits VR {tag.VrCode}.";
            }
            catch (OverflowException)
            {
                return $"{label} must be a number that fits VR {tag.VrCode}.";
            }
            catch (Exception ex)
            {
                // Whatever fo-dicom objects to in this VR. Saying so here names the tag, unlike a failure in the middle of a run.
                return $"{label}: {ex.Message}";
            }
        }

        /// <summary>Everything wrong with a whole list of tags and values, including how they fit together.</summary>
        public static List<string> CheckAll(IEnumerable<AdditionalTagValue> values, string characterSet)
        {
            var problems = new List<string>();
            var list = (values ?? Enumerable.Empty<AdditionalTagValue>()).ToList();
            foreach (var item in list)
            {
                var problem = CheckTag(item.Tag) ?? CheckValue(item.Tag, item.Value, characterSet);
                if (problem != null) problems.Add(problem);
            }

            var problemInList = CheckCombination(list.Select(v => v.Tag));
            if (problemInList != null) problems.Add(problemInList);
            return problems;
        }

        /// <summary>
        /// A tag can only appear once, and each private block belongs to one creator. fo-dicom would otherwise move a
        /// private element to another block without saying so.
        /// </summary>
        public static string CheckCombination(IEnumerable<AdditionalTag> tags)
        {
            var seen = new HashSet<string>();
            var creatorOfBlock = new Dictionary<int, string>();
            var blockOfCreator = new Dictionary<string, int>();
            foreach (var tag in tags)
            {
                if (!seen.Add(tag.Number)) return $"{tag.Number} is in the list twice.";
                if (!tag.IsPrivate || tag.PrivateCreator == null) continue;

                var block = (tag.Group << 8) | (tag.Element >> 8);
                if (creatorOfBlock.TryGetValue(block, out var other) && other != tag.PrivateCreator)
                {
                    return $"{tag.Number} has private creator '{tag.PrivateCreator}', but its block already belongs to '{other}'.";
                }
                creatorOfBlock[block] = tag.PrivateCreator;

                var creatorInGroup = tag.Group.ToString("X4", CultureInfo.InvariantCulture) + " " + tag.PrivateCreator;
                if (blockOfCreator.TryGetValue(creatorInGroup, out var otherBlock) && otherBlock != block)
                {
                    return $"Private creator '{tag.PrivateCreator}' can only have one block in group {tag.Group:X4}, but {tag.Number} is in another one.";
                }
                blockOfCreator[creatorInGroup] = block;
            }
            return null;
        }

        internal static bool AllowsSeveralValues(AdditionalTag tag)
        {
            if (SingleValueVrs.Contains(tag.VrCode)) return false;
            var entry = AdditionalTagParser.DictionaryEntry(tag.Group, tag.Element);
            return entry == null || entry.ValueMultiplicity.Maximum > 1;
        }

        private static bool Contains(IEnumerable<DicomTag> tags, AdditionalTag tag) =>
            tags.Any(t => t.Group == tag.Group && t.Element == tag.Element);
    }

    /// <summary>Puts an additional tag into a dataset.</summary>
    public static class AdditionalTagWriter
    {
        public static void Write(DicomDataset dataset, AdditionalTagValue item)
        {
            var tag = item.Tag;
            var value = item.Value ?? string.Empty;

            // Claims the block the user chose. With its creator in place, fo-dicom keeps the element number as entered.
            if (tag.IsPrivate) dataset.AddOrUpdate(DicomVR.LO, new DicomTag(tag.Group, (ushort)(tag.Element >> 8)), tag.PrivateCreator);

            var vr = DicomVR.Parse(tag.VrCode);
            var target = tag.DicomTag;
            var parts = value.Length == 0 ? new string[0]
                : AdditionalTagRules.AllowsSeveralValues(tag) ? value.Split('\\')
                : new[] { value };

            // Parsed here so that "1.5" means the same on every Windows language.
            switch (tag.VrCode)
            {
                case "US": dataset.AddOrUpdate(vr, target, parts.Select(p => ushort.Parse(p, CultureInfo.InvariantCulture)).ToArray()); break;
                case "SS": dataset.AddOrUpdate(vr, target, parts.Select(p => short.Parse(p, CultureInfo.InvariantCulture)).ToArray()); break;
                case "UL": dataset.AddOrUpdate(vr, target, parts.Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray()); break;
                case "SL": dataset.AddOrUpdate(vr, target, parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray()); break;
                case "FL": dataset.AddOrUpdate(vr, target, parts.Select(p => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray()); break;
                case "FD": dataset.AddOrUpdate(vr, target, parts.Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray()); break;
                default: dataset.AddOrUpdate(vr, target, parts.Length == 0 ? new[] { string.Empty } : parts); break;
            }
        }
    }
}
