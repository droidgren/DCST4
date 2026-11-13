using DCST4.Core.Generation.Datasets;
using DCST4.Core.Testing;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Verification
{
    /// <summary>An instance that was sent: the manifest's record and the file as it went out.</summary>
    public sealed class ExpectedInstance
    {
        public ManifestInstance Manifest { get; set; }
        public string SavedPath { get; set; }
    }

    /// <summary>An instance that came back.</summary>
    public sealed class ReturnedInstance
    {
        public string Path { get; set; }
        /// <summary>What the sender offered for it, on C-MOVE.</summary>
        public IReadOnlyList<string> OfferedTransferSyntaxUids { get; set; } = new string[0];
    }

    /// <summary>
    /// Compares what came back from a system with what was sent, and reports it level by level, so that a changed pixel is
    /// never mistaken for a changed attribute. It looks only at files and knows nothing about how they travelled.
    /// </summary>
    public static class IntegrityOracle
    {
        private sealed class Pair
        {
            public ExpectedInstance Expected;
            public DicomFile Original;
            public ReturnedInstance Returned;
            public DicomFile Retrieved;
        }

        public static IntegritySet Compare(string objectType, string method, IReadOnlyList<ExpectedInstance> expected, IReadOnlyList<ReturnedInstance> returned)
        {
            var set = new IntegritySet { ObjectType = objectType, Method = method, ExpectedCount = expected.Count, ReturnedCount = returned.Count };

            var unreadable = new List<string>();
            var readable = new List<Tuple<ReturnedInstance, DicomFile, string>>();
            foreach (var instance in returned)
            {
                try
                {
                    var file = DicomFile.Open(instance.Path);
                    readable.Add(Tuple.Create(instance, file, file.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty)));
                }
                catch (DicomFileException ex)
                {
                    unreadable.Add(ex.Message);
                }
            }

            var pairs = MakePairs(expected, readable, set);
            foreach (var pair in pairs) set.Instances.Add(CompareInstance(pair));

            set.Levels.Add(TransportLevel(set, unreadable));
            set.Levels.Add(IdentityLevel(set));
            set.Levels.Add(AttributesLevel(set));
            set.Levels.Add(PixelLevel(set));
            set.Levels.Add(SyntaxLevel(set));
            set.Result = ResultSeverity.Worst(set.Levels.Select(l => l.Result));
            return set;
        }

        // ---------------------------------------------------------------- pairing

        private static List<Pair> MakePairs(IReadOnlyList<ExpectedInstance> expected, List<Tuple<ReturnedInstance, DicomFile, string>> readable, IntegritySet set)
        {
            var pairs = new List<Pair>();
            var left = readable.ToList();
            var unmatched = new List<ExpectedInstance>();

            foreach (var instance in expected)
            {
                var match = left.FirstOrDefault(r => r.Item3 == instance.Manifest.SopInstanceUid);
                if (match == null)
                {
                    unmatched.Add(instance);
                    continue;
                }
                left.Remove(match);
                pairs.Add(new Pair { Expected = instance, Original = DicomFile.Open(instance.SavedPath), Returned = match.Item1, Retrieved = match.Item2 });
            }

            // A system that gives the object a new SOP Instance UID has still returned it, so this is an Identity finding and
            // not a Transport one. Position is all there is to go on.
            foreach (var instance in unmatched)
            {
                var original = DicomFile.Open(instance.SavedPath);
                var match = left.FirstOrDefault(r => SamePosition(original.Dataset, r.Item2.Dataset));
                if (match == null)
                {
                    set.Missing.Add(instance.Manifest.SopInstanceUid);
                    continue;
                }
                left.Remove(match);
                pairs.Add(new Pair { Expected = instance, Original = original, Returned = match.Item1, Retrieved = match.Item2 });
            }

            set.Extra.AddRange(left.Select(r => r.Item3));
            return pairs;
        }

        private static bool SamePosition(DicomDataset a, DicomDataset b) =>
            a.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty) == b.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty)
            && a.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty) == b.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty)
            && a.GetSingleValueOrDefault(DicomTag.InstanceNumber, string.Empty) == b.GetSingleValueOrDefault(DicomTag.InstanceNumber, string.Empty);

        private static InstanceIntegrity CompareInstance(Pair pair)
        {
            var manifest = pair.Expected.Manifest;
            var result = new InstanceIntegrity
            {
                SopInstanceUid = manifest.SopInstanceUid,
                SopClassUid = manifest.SopClassUid,
                Role = manifest.Role,
                TransferSyntaxSent = manifest.TransferSyntaxUid,
                TransferSyntaxReturned = pair.Retrieved.Dataset.InternalTransferSyntax.UID.UID,
                TransferSyntaxesOffered = pair.Returned.OfferedTransferSyntaxUids.ToList(),
                NormalizedHashMatchesManifest = NormalizedAttributes.Hash(pair.Retrieved.Dataset) == manifest.NormalizedAttributesSha256,
            };
            result.TranscodingInference = InferTranscoding(result);

            CompareIdentity(manifest, pair.Retrieved.Dataset, result);
            new AttributeComparer(pair.Original.Dataset, pair.Retrieved.Dataset, result).Run();
            PixelComparer.Compare(pair.Original, pair.Retrieved, manifest, result);
            return result;
        }

        private static void CompareIdentity(ManifestInstance manifest, DicomDataset retrieved, InstanceIntegrity result)
        {
            void Check(string name, string sent, DicomTag tag)
            {
                var returned = retrieved.GetSingleValueOrDefault(tag, string.Empty);
                if (returned != sent) result.IdentityChanges.Add($"{name}: {sent} became {(returned.Length == 0 ? "nothing" : returned)}");
            }

            Check("Study Instance UID", manifest.StudyInstanceUid, DicomTag.StudyInstanceUID);
            Check("Series Instance UID", manifest.SeriesInstanceUid, DicomTag.SeriesInstanceUID);
            Check("SOP Instance UID", manifest.SopInstanceUid, DicomTag.SOPInstanceUID);
        }

        private static string InferTranscoding(InstanceIntegrity result)
        {
            if (result.TransferSyntaxReturned == result.TransferSyntaxSent) return "Returned as it was sent.";
            if (result.TransferSyntaxesOffered.Count > 0 && !result.TransferSyntaxesOffered.Contains(result.TransferSyntaxSent))
            {
                return "It was not offered back in the syntax it was sent in, which suggests that it was transcoded on store.";
            }
            return "Returned in another syntax than it was sent in. From outside it cannot be told whether that happened on store or on retrieve.";
        }

        // ---------------------------------------------------------------- levels

        private static IntegrityLevelResult TransportLevel(IntegritySet set, List<string> unreadable)
        {
            if (set.ExpectedCount == set.ReturnedCount && set.Missing.Count == 0 && set.Extra.Count == 0 && unreadable.Count == 0)
            {
                return Level(IntegrityLevel.Transport, TestResultClass.PASS, $"{Int(set.ExpectedCount)} of {Int(set.ExpectedCount)} instances came back.");
            }

            var parts = new List<string> { $"{Int(set.ReturnedCount)} of {Int(set.ExpectedCount)} instances came back." };
            if (set.Missing.Count > 0) parts.Add($"{Int(set.Missing.Count)} sent were not returned.");
            if (set.Extra.Count > 0) parts.Add($"{Int(set.Extra.Count)} returned were not sent.");
            if (unreadable.Count > 0) parts.Add($"{Int(unreadable.Count)} could not be read as DICOM.");
            return Level(IntegrityLevel.Transport, TestResultClass.FAIL, string.Join(" ", parts));
        }

        private static IntegrityLevelResult IdentityLevel(IntegritySet set)
        {
            if (set.Instances.Count == 0) return Level(IntegrityLevel.Identity, TestResultClass.INDETERMINATE, "Nothing came back to compare.");

            var changed = set.Instances.SelectMany(i => i.IdentityChanges).ToList();
            return changed.Count == 0
                ? Level(IntegrityLevel.Identity, TestResultClass.PASS, "Study, Series and SOP Instance UIDs are unchanged.")
                : Level(IntegrityLevel.Identity, TestResultClass.FAIL, string.Join("; ", changed.Distinct()) + ".");
        }

        private static IntegrityLevelResult AttributesLevel(IntegritySet set)
        {
            if (set.Instances.Count == 0) return Level(IntegrityLevel.Attributes, TestResultClass.INDETERMINATE, "Nothing came back to compare.");

            var differences = set.Instances.SelectMany(i => i.Differences).ToList();
            int Count(AttributeChangeClass kind) => differences.Count(d => d.Class == kind);

            var parts = new List<string>();
            void Report(int count, string text)
            {
                if (count > 0) parts.Add(Int(count) + " " + text);
            }

            Report(Count(AttributeChangeClass.SilentModification), "silently modified");
            Report(Count(AttributeChangeClass.Truncated), "truncated");
            Report(Count(AttributeChangeClass.PrivateDropped), "private elements dropped");
            Report(Count(AttributeChangeClass.DocumentedCoercion), "documented in Original Attributes Sequence");
            Report(Count(AttributeChangeClass.Added), "added");
            Report(Count(AttributeChangeClass.EmptyDropped), "sent empty and dropped");
            Report(Count(AttributeChangeClass.VrLost), "with the VR lost");
            Report(Count(AttributeChangeClass.NotCompared), "not compared");
            Report(set.Instances.Sum(i => i.PrivateKept.Count), "private elements kept");
            Report(set.Instances.Count(i => i.CharacterSetChanged), "with another Specific Character Set");

            var failed = Count(AttributeChangeClass.SilentModification) + Count(AttributeChangeClass.Truncated) + Count(AttributeChangeClass.PrivateDropped) > 0;
            var text2 = parts.Count == 0 ? "Every attribute is unchanged." : string.Join(", ", parts) + ".";
            return Level(IntegrityLevel.Attributes, failed ? TestResultClass.FAIL : TestResultClass.PASS, text2);
        }

        private static IntegrityLevelResult PixelLevel(IntegritySet set)
        {
            if (set.Instances.Count == 0) return Level(IntegrityLevel.Pixel, TestResultClass.INDETERMINATE, "Nothing came back to compare.");

            var groups = set.Instances.GroupBy(i => i.Pixel).OrderBy(g => g.Key).Select(g => Int(g.Count()) + " " + g.Key).ToList();
            var summary = string.Join(", ", groups) + ".";
            var failed = set.Instances.Any(i => i.Pixel == PixelVerdict.LossyChanged || i.Pixel == PixelVerdict.Corrupt);
            if (failed)
            {
                var detail = set.Instances.Where(i => i.PixelDetail != null).Select(i => i.PixelDetail).FirstOrDefault();
                return Level(IntegrityLevel.Pixel, TestResultClass.FAIL, summary + (detail != null ? " " + detail : string.Empty));
            }
            return Level(IntegrityLevel.Pixel, set.Instances.Any(i => i.Pixel == PixelVerdict.NotCompared) ? TestResultClass.INDETERMINATE : TestResultClass.PASS, summary);
        }

        /// <summary>A finding, not a verdict: a PACS may keep an object in another syntax.</summary>
        private static IntegrityLevelResult SyntaxLevel(IntegritySet set)
        {
            if (set.Instances.Count == 0) return Level(IntegrityLevel.TransferSyntax, TestResultClass.INDETERMINATE, "Nothing came back to compare.");

            var changed = set.Instances.Where(i => i.TransferSyntaxReturned != i.TransferSyntaxSent).ToList();
            return Level(IntegrityLevel.TransferSyntax, TestResultClass.PASS, changed.Count == 0
                ? "Every instance came back in the syntax it was sent in."
                : $"{Int(changed.Count)} came back in another syntax. {changed[0].TranscodingInference}");
        }

        private static IntegrityLevelResult Level(IntegrityLevel level, TestResultClass result, string explanation) =>
            new IntegrityLevelResult { Level = level, Result = result, Explanation = explanation };

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The per-tag difference between one object as sent and as returned.</summary>
    internal sealed class AttributeComparer
    {
        private static readonly char[] Padding = { ' ', '\0' };

        private readonly DicomDataset _original;
        private readonly DicomDataset _retrieved;
        private readonly InstanceIntegrity _result;
        private readonly HashSet<string> _documented = new HashSet<string>(StringComparer.Ordinal);
        private readonly System.Text.Encoding _retrievedEncoding;

        public AttributeComparer(DicomDataset original, DicomDataset retrieved, InstanceIntegrity result)
        {
            _original = original;
            _retrieved = retrieved;
            _result = result;
            _retrievedEncoding = EncodingOf(retrieved);
        }

        public void Run()
        {
            _result.CharacterSetSent = CharacterSet(_original);
            _result.CharacterSetReturned = CharacterSet(_retrieved);
            _result.CharacterSetChanged = _result.CharacterSetSent != _result.CharacterSetReturned;

            // Only the top level: that is where a system records what it did to the object as a whole.
            if (_retrieved.TryGetSequence(DicomTag.OriginalAttributesSequence, out var originals))
            {
                foreach (var record in originals.Items)
                {
                    if (!record.TryGetSequence(DicomTag.ModifiedAttributesSequence, out var modified)) continue;
                    foreach (var item in modified.Items.SelectMany(i => i)) _documented.Add(Key(item.Tag));
                }
            }

            Walk(_original, _retrieved, string.Empty);
        }

        private static string CharacterSet(DicomDataset dataset) =>
            dataset.TryGetString(DicomTag.SpecificCharacterSet, out var value) ? value : string.Empty;

        private static System.Text.Encoding EncodingOf(DicomDataset dataset)
        {
            try
            {
                return dataset.TryGetValues<string>(DicomTag.SpecificCharacterSet, out var sets) && sets.Length > 0
                    ? DicomEncoding.GetEncodings(sets)[0]
                    : DicomEncoding.Default;
            }
            catch (ArgumentException)
            {
                return DicomEncoding.Default;
            }
        }

        private static bool Ignored(DicomTag tag) =>
            NormalizedAttributes.IsLeftOut(tag) || tag == DicomTag.OriginalAttributesSequence;

        /// <summary>A private element by creator and element number, since a system may move it to another block.</summary>
        private static string Key(DicomTag tag) => tag.IsPrivate
            ? tag.Group.ToString("X4", CultureInfo.InvariantCulture) + ":" + (tag.PrivateCreator?.Creator ?? string.Empty) + ":" + (tag.Element & 0x00FF).ToString("X2", CultureInfo.InvariantCulture)
            : tag.Group.ToString("X4", CultureInfo.InvariantCulture) + tag.Element.ToString("X4", CultureInfo.InvariantCulture);

        private void Walk(DicomDataset original, DicomDataset retrieved, string path)
        {
            var left = retrieved.Where(i => !Ignored(i.Tag)).ToDictionary(i => Key(i.Tag), StringComparer.Ordinal);

            foreach (var item in original.Where(i => !Ignored(i.Tag)))
            {
                var key = Key(item.Tag);
                var place = path + item.Tag;
                if (!left.TryGetValue(key, out var match))
                {
                    Dropped(item, key, place);
                    continue;
                }
                left.Remove(key);
                if (item.Tag.IsPrivate) _result.PrivateKept.Add(place);
                CompareItem(item, match, key, place);
            }

            foreach (var extra in left.Values)
            {
                Add(AttributeChangeClass.Added, path + extra.Tag, extra.Tag, null, Show(extra));
            }
        }

        private void Dropped(DicomItem item, string key, string place)
        {
            if (item.Tag.IsPrivate) _result.PrivateDropped.Add(place);

            var empty = (item is DicomElement element && element.Buffer.Size == 0) || (item is DicomSequence sequence && sequence.Items.Count == 0);
            var kind = _documented.Contains(key) ? AttributeChangeClass.DocumentedCoercion
                : empty ? AttributeChangeClass.EmptyDropped
                : item.Tag.IsPrivate ? AttributeChangeClass.PrivateDropped
                : AttributeChangeClass.SilentModification;
            Add(kind, place, item.Tag, Show(item), null);
        }

        private void CompareItem(DicomItem original, DicomItem retrieved, string key, string place)
        {
            if (original is DicomSequence sent)
            {
                if (retrieved is DicomSequence back)
                {
                    if (sent.Items.Count != back.Items.Count)
                    {
                        Changed(key, place, original.Tag, Int(sent.Items.Count) + " items", Int(back.Items.Count) + " items");
                        return;
                    }
                    for (var i = 0; i < sent.Items.Count; i++)
                    {
                        Walk(sent.Items[i], back.Items[i], place + "[" + Int(i) + "].");
                    }
                    return;
                }

                // What Implicit VR does to a private sequence: its bytes are there, but reading them would need its dictionary.
                if (retrieved.ValueRepresentation == DicomVR.UN) Add(AttributeChangeClass.NotCompared, place, original.Tag, "sequence", "UN bytes");
                else Changed(key, place, original.Tag, "sequence", Show(retrieved));
                return;
            }

            if (!(original is DicomElement sentElement) || !(retrieved is DicomElement backElement))
            {
                Changed(key, place, original.Tag, Show(original), Show(retrieved));
                return;
            }

            if (sentElement.ValueRepresentation != DicomVR.UN && backElement.ValueRepresentation == DicomVR.UN)
            {
                CompareAfterVrLoss(sentElement, backElement, key, place);
                return;
            }

            CompareValues(sentElement, backElement, key, place);
        }

        private void CompareAfterVrLoss(DicomElement sent, DicomElement back, string key, string place)
        {
            bool same;
            string shown;
            if (sent.ValueRepresentation.IsString)
            {
                shown = _retrievedEncoding.GetString(back.Buffer.Data).TrimEnd(Padding);
                same = shown == string.Join("\\", TextValues(sent));
            }
            else
            {
                shown = Convert.ToBase64String(back.Buffer.Data);
                same = Trim(back.Buffer.Data).SequenceEqual(Trim(sent.Buffer.Data));
            }

            if (same) Add(AttributeChangeClass.VrLost, place, sent.Tag, sent.ValueRepresentation.Code, "UN");
            else Changed(key, place, sent.Tag, Show(sent), shown);
        }

        private void CompareValues(DicomElement sent, DicomElement back, string key, string place)
        {
            if (sent.ValueRepresentation.IsString && back.ValueRepresentation.IsString)
            {
                var sentText = TextValues(sent);
                var backText = TextValues(back);
                if (SameText(sent.ValueRepresentation, sentText, backText)) return;

                var a = string.Join("\\", sentText);
                var b = string.Join("\\", backText);
                var truncated = b.Length > 0 && b.Length < a.Length && a.StartsWith(b, StringComparison.Ordinal);
                Changed(key, place, sent.Tag, a, b, truncated);
                return;
            }

            if (!Trim(sent.Buffer.Data).SequenceEqual(Trim(back.Buffer.Data))) Changed(key, place, sent.Tag, Show(sent), Show(back));
        }

        private static bool SameText(DicomVR vr, string[] sent, string[] back)
        {
            if (sent.Length != back.Length) return false;
            for (var i = 0; i < sent.Length; i++)
            {
                if (sent[i] == back[i]) continue;
                // The same number written differently, such as 1.50 and 1.5, is the same value.
                if ((vr != DicomVR.DS && vr != DicomVR.IS)
                    || !decimal.TryParse(sent[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    || !decimal.TryParse(back[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                    || a != b) return false;
            }
            return true;
        }

        private static string[] TextValues(DicomElement element)
        {
            try
            {
                return element.Get<string[]>().Select(v => (v ?? string.Empty).TrimEnd(Padding)).ToArray();
            }
            catch (DicomDataException)
            {
                return new[] { element.Get<string>() ?? string.Empty };
            }
        }

        private void Changed(string key, string place, DicomTag tag, string original, string retrieved, bool truncated = false)
        {
            var kind = _documented.Contains(key) ? AttributeChangeClass.DocumentedCoercion
                : truncated ? AttributeChangeClass.Truncated
                : AttributeChangeClass.SilentModification;
            Add(kind, place, tag, original, retrieved);
        }

        private void Add(AttributeChangeClass kind, string place, DicomTag tag, string original, string retrieved)
        {
            _result.Differences.Add(new AttributeDifference
            {
                Class = kind,
                Path = place,
                Name = tag.IsPrivate ? "Private element" : tag.DictionaryEntry.Name,
                Original = original,
                Retrieved = retrieved,
            });
        }

        private static string Show(DicomItem item)
        {
            if (item is DicomSequence sequence) return Int(sequence.Items.Count) + " items";
            if (item is DicomElement element)
            {
                try
                {
                    if (element.ValueRepresentation.IsString) return string.Join("\\", TextValues(element));
                }
                catch (DicomDataException)
                {
                    // A value that cannot be read as text is shown as bytes below.
                }
                return Int((int)element.Buffer.Size) + " bytes";
            }
            return item.ValueRepresentation.Code;
        }

        /// <summary>Padding that makes a length even, which a transcoding may add or remove.</summary>
        private static byte[] Trim(byte[] bytes)
        {
            var length = bytes.Length;
            while (length > 0 && (bytes[length - 1] == 0x00 || bytes[length - 1] == 0x20)) length--;
            return bytes.Take(length).ToArray();
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Compares the pixels of one object: first as encoded, then as decoded.</summary>
    internal static class PixelComparer
    {
        public static void Compare(DicomFile original, DicomFile retrieved, ManifestInstance manifest, InstanceIntegrity result)
        {
            var hadPixels = original.Dataset.Contains(DicomTag.PixelData);
            var hasPixels = retrieved.Dataset.Contains(DicomTag.PixelData);
            if (!hadPixels && !hasPixels)
            {
                result.Pixel = PixelVerdict.NoPixelData;
                return;
            }
            if (hadPixels != hasPixels)
            {
                Corrupt(result, hadPixels ? "Pixel Data is gone." : "Pixel Data appeared in an object that had none.");
                return;
            }

            try
            {
                CompareFrames(original, retrieved, manifest, result);
            }
            catch (Exception ex) when (ex is DicomException || ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is OverflowException)
            {
                Corrupt(result, "The pixel data could not be decoded: " + ex.Message);
            }
        }

        private static void CompareFrames(DicomFile original, DicomFile retrieved, ManifestInstance manifest, InstanceIntegrity result)
        {
            if (global::DCST4.Core.Codecs.OwnCodecs.IsVideo(original.Dataset.InternalTransferSyntax) || global::DCST4.Core.Codecs.OwnCodecs.IsVideo(retrieved.Dataset.InternalTransferSyntax))
            {
                CompareVideo(original, retrieved, manifest, result);
                return;
            }

            var sentFrames = DicomPixelData.Create(original.Dataset);
            var backFrames = DicomPixelData.Create(retrieved.Dataset);
            if (sentFrames.NumberOfFrames != backFrames.NumberOfFrames)
            {
                Corrupt(result, $"{Int(sentFrames.NumberOfFrames)} frames were sent and {Int(backFrames.NumberOfFrames)} came back.");
                return;
            }

            var sameSyntax = original.Dataset.InternalTransferSyntax == retrieved.Dataset.InternalTransferSyntax;
            if (sameSyntax && SameEncoded(sentFrames, backFrames))
            {
                result.Pixel = PixelVerdict.ByteIdentical;
                result.FramesMatchManifest = MatchesManifest(retrieved, manifest);
                return;
            }

            var syntaxes = new[] { original.Dataset.InternalTransferSyntax, retrieved.Dataset.InternalTransferSyntax };
            if (syntaxes.Any(s => s.IsEncapsulated && !Codecs.HasCodec(s)))
            {
                result.Pixel = PixelVerdict.NotCompared;
                result.PixelDetail = "No decoder for " + syntaxes.First(s => s.IsEncapsulated && !Codecs.HasCodec(s)).UID.Name + ".";
                return;
            }

            var sent = Decode(original);
            var back = Decode(retrieved);
            var diff = new DifferenceCounter(sent);
            for (var frame = 0; frame < sent.NumberOfFrames; frame++)
            {
                var a = sent.GetFrame(frame).Data;
                var b = back.GetFrame(frame).Data;
                if (a.Length != b.Length)
                {
                    Corrupt(result, $"Frame {Int(frame)} is {Int(b.Length)} bytes and should be {Int(a.Length)}.");
                    return;
                }
                diff.Add(a, b);
            }

            result.FramesMatchManifest = MatchesManifest(retrieved, manifest);
            if (diff.AllEqual)
            {
                result.Pixel = PixelVerdict.LosslessEquivalent;
                return;
            }

            result.Pixel = PixelVerdict.LossyChanged;
            result.MaxAbsoluteDifference = diff.Max;
            result.MeanAbsoluteDifference = diff.Mean;
            result.PixelDetail = string.Format(CultureInfo.InvariantCulture, "The largest absolute difference is {0}, and the mean {1:0.###}.", diff.Max, diff.Mean);
        }

        /// <summary>A video stream holds all frames at once, so it is compared as a whole. DCST4 cannot decode it to compare the pictures.</summary>
        private static void CompareVideo(DicomFile original, DicomFile retrieved, ManifestInstance manifest, InstanceIntegrity result)
        {
            var sentCount = original.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
            var backCount = retrieved.Dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
            if (sentCount != backCount)
            {
                Corrupt(result, $"{Int(sentCount)} frames were sent and {Int(backCount)} came back.");
                return;
            }

            var sameSyntax = original.Dataset.InternalTransferSyntax == retrieved.Dataset.InternalTransferSyntax;
            if (sameSyntax && Fragments(original).SequenceEqual(Fragments(retrieved)))
            {
                result.Pixel = PixelVerdict.ByteIdentical;
                result.FramesMatchManifest = MatchesManifest(retrieved, manifest);
                return;
            }

            result.Pixel = PixelVerdict.NotCompared;
            var undecodable = global::DCST4.Core.Codecs.OwnCodecs.IsVideo(retrieved.Dataset.InternalTransferSyntax) ? retrieved : original;
            result.PixelDetail = "No decoder for " + undecodable.Dataset.InternalTransferSyntax.UID.Name + ", and the stream is not the one that was sent.";
        }

        /// <summary>The encapsulated stream as one array, whatever fragments it was split into.</summary>
        private static byte[] Fragments(DicomFile file)
        {
            var sequence = file.Dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) as DicomFragmentSequence;
            if (sequence == null) return new byte[0];
            return sequence.Fragments.SelectMany(f => f.Data).ToArray();
        }

        private static readonly FellowOakDicom.PureCodecs.PureTranscoderManager Codecs = new FellowOakDicom.PureCodecs.PureTranscoderManager();

        private static DicomPixelData Decode(DicomFile file)
        {
            var decoded = file.Dataset.InternalTransferSyntax.IsEncapsulated ? file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian) : file;
            return DicomPixelData.Create(decoded.Dataset);
        }

        private static bool SameEncoded(DicomPixelData a, DicomPixelData b)
        {
            for (var frame = 0; frame < a.NumberOfFrames; frame++)
            {
                if (!a.GetFrame(frame).Data.SequenceEqual(b.GetFrame(frame).Data)) return false;
            }
            return true;
        }

        /// <summary>Null when a lossy original was sent, since the manifest hashes the frames before encoding.</summary>
        private static bool? MatchesManifest(DicomFile retrieved, ManifestInstance manifest)
        {
            if (manifest.FrameSha256.Count == 0) return null;
            if (DicomTransferSyntax.Parse(manifest.TransferSyntaxUid).IsLossy) return null;

            var decoded = retrieved.Dataset.InternalTransferSyntax.IsEncapsulated ? retrieved.Clone(DicomTransferSyntax.ExplicitVRLittleEndian) : retrieved;
            return ManifestHashes.FrameHashes(decoded.Dataset).SequenceEqual(manifest.FrameSha256);
        }

        private static void Corrupt(InstanceIntegrity result, string detail)
        {
            result.Pixel = PixelVerdict.Corrupt;
            result.PixelDetail = detail;
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Over all samples, as numbers and not as bytes.</summary>
        private sealed class DifferenceCounter
        {
            private readonly int _bytesPerSample;
            private readonly bool _signed;
            private long _count;
            private double _sum;

            public DifferenceCounter(DicomPixelData data)
            {
                _bytesPerSample = data.BitsAllocated > 8 ? 2 : 1;
                _signed = data.PixelRepresentation == PixelRepresentation.Signed;
            }

            public double Max { get; private set; }
            public double Mean => _count == 0 ? 0 : _sum / _count;
            public bool AllEqual => Max == 0;

            public void Add(byte[] a, byte[] b)
            {
                for (var i = 0; i + _bytesPerSample <= a.Length; i += _bytesPerSample)
                {
                    var difference = Math.Abs(Sample(a, i) - Sample(b, i));
                    if (difference > Max) Max = difference;
                    _sum += difference;
                    _count++;
                }
            }

            private double Sample(byte[] data, int offset)
            {
                if (_bytesPerSample == 1) return data[offset];
                var raw = (ushort)(data[offset] | (data[offset + 1] << 8));
                if (_signed) return (short)raw;
                return raw;
            }
        }
    }
}
