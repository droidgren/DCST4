using DCST4.Core.Generation;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Pipeline
{
    /// <summary>One line of a tags preview: an element, a sequence, or an item in a sequence.</summary>
    public sealed class TagPreviewRow
    {
        /// <summary>0 at the top level. An item is one deeper than its sequence, and the item's elements one deeper again.</summary>
        public int Depth { get; set; }
        /// <summary>(gggg,eeee), or empty for an item.</summary>
        public string Tag { get; set; }
        public string Vr { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        /// <summary>True for the File Meta Information, group 0002, which a saved file has and a C-STORE does not send.</summary>
        public bool IsFileMeta { get; set; }
    }

    /// <summary>What a tags preview shows.</summary>
    public sealed class TagPreviewResult
    {
        public IReadOnlyList<TagPreviewRow> Rows { get; set; }
        /// <summary>The object, transfer syntax, number of elements and file size, on one line.</summary>
        public string Description { get; set; }
        /// <summary>The Patient ID of the object, so a caller can tell which identity it shows.</summary>
        public string PatientId { get; set; }
    }

    /// <summary>
    /// Generates the first object of an exam exactly as a run would, in the transfer syntax it would be sent in, and lists every
    /// tag it has. Nothing is sent or saved.
    /// </summary>
    public static class TagPreview
    {
        /// <summary>Longer values are cut here, so a long text or a long list of numbers does not fill the window.</summary>
        public const int MaxValueLength = 256;

        private static readonly HashSet<string> BinaryVrs = new HashSet<string>(StringComparer.Ordinal) { "OB", "OW", "OF", "OD", "OL", "OV", "UN" };

        public static TagPreviewResult Build(ExamRequest request, DicomTransferSyntax syntax, CancellationToken cancellationToken, Action<int, int> frameEncoded = null)
        {
            if (request?.Modality == null) throw new ArgumentNullException(nameof(request));
            if (syntax == null) throw new ArgumentNullException(nameof(syntax));

            // The images are made one at a time as they are asked for, so this makes only the first, with all its frames.
            var image = new ExamGenerator(request).GetImages(cancellationToken).First();
            cancellationToken.ThrowIfCancellationRequested();
            var file = TransferSyntaxConverter.Convert(image.File, syntax, cancellationToken, frameEncoded, request.Lossy);
            cancellationToken.ThrowIfCancellationRequested();

            var rows = new List<TagPreviewRow>();
            Walk(file.FileMetaInfo, 0, true, rows);
            Walk(file.Dataset, 0, false, rows);

            return new TagPreviewResult
            {
                Rows = rows,
                Description = Describe(request, file, rows),
                PatientId = file.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            };
        }

        private static void Walk(DicomDataset dataset, int depth, bool fileMeta, List<TagPreviewRow> rows)
        {
            foreach (var item in dataset)
            {
                var row = new TagPreviewRow
                {
                    Depth = depth,
                    Tag = string.Format(CultureInfo.InvariantCulture, "({0:X4},{1:X4})", item.Tag.Group, item.Tag.Element),
                    Vr = item.ValueRepresentation.Code,
                    Name = Name(item.Tag),
                    IsFileMeta = fileMeta,
                };
                rows.Add(row);

                if (item is DicomSequence sequence)
                {
                    row.Value = sequence.Items.Count == 1 ? "1 item" : sequence.Items.Count.ToString(CultureInfo.InvariantCulture) + " items";
                    for (var i = 0; i < sequence.Items.Count; i++)
                    {
                        rows.Add(new TagPreviewRow { Depth = depth + 1, Tag = string.Empty, Vr = string.Empty, Name = "Item " + (i + 1).ToString(CultureInfo.InvariantCulture), Value = string.Empty, IsFileMeta = fileMeta });
                        Walk(sequence.Items[i], depth + 2, fileMeta, rows);
                    }
                }
                else if (item is DicomFragmentSequence fragments)
                {
                    row.Value = string.Format(CultureInfo.InvariantCulture, "(encapsulated, {0} {1}, {2} bytes)",
                        fragments.Fragments.Count, fragments.Fragments.Count == 1 ? "fragment" : "fragments", Bytes(fragments.Fragments.Sum(f => f.Size)));
                }
                else if (item is DicomElement element)
                {
                    row.Value = Value(element);
                }
                else
                {
                    row.Value = string.Empty;
                }
            }
        }

        private static string Name(DicomTag tag)
        {
            if (tag.IsPrivate)
            {
                if (tag.Element >= 0x0010 && tag.Element <= 0x00FF) return "Private Creator";
                var creator = tag.PrivateCreator?.Creator;
                return string.IsNullOrEmpty(creator) ? "Private tag" : "Private tag (" + creator + ")";
            }
            var entry = tag.DictionaryEntry;
            return entry == null || entry == DicomDictionary.UnknownTag ? "Unknown tag" : entry.Name;
        }

        private static string Value(DicomElement element)
        {
            if (BinaryVrs.Contains(element.ValueRepresentation.Code)) return "(" + Bytes(element.Buffer?.Size ?? 0) + " bytes)";
            if (element.Count == 0) return string.Empty;
            try
            {
                var values = new List<string>();
                for (var i = 0; i < element.Count; i++)
                {
                    values.Add(element.ValueRepresentation.Code == "AT"
                        ? element.Get<DicomTag>(i).ToString()
                        : element.Get<string>(i));
                    if (values.Sum(v => v.Length + 1) > MaxValueLength) break;
                }
                return Cut(string.Join("\\", values));
            }
            catch (Exception ex) when (ex is DicomDataException || ex is InvalidCastException || ex is FormatException || ex is ArgumentException)
            {
                return "(" + Bytes(element.Buffer?.Size ?? 0) + " bytes)";
            }
        }

        private static string Cut(string text) =>
            text.Length <= MaxValueLength ? text : text.Substring(0, MaxValueLength) + "…";

        private static string Bytes(long count) => count.ToString("#,0", CultureInfo.InvariantCulture);

        private static string Describe(ExamRequest request, DicomFile file, List<TagPreviewRow> rows)
        {
            var total = request.TotalImages;
            var elements = rows.Count(r => r.Tag.Length > 0);
            return string.Format(CultureInfo.InvariantCulture, "{0}, the first of {1} {2}, {3}, {4} elements, file {5}.",
                request.Modality.DisplayName, total, total == 1 ? "object" : "objects", file.Dataset.InternalTransferSyntax.UID.Name, elements,
                ExamSizeEstimator.Format(SavedBytes(file)));
        }

        /// <summary>Counts the bytes of the saved file without keeping them, since a private payload can be large.</summary>
        private static long SavedBytes(DicomFile file)
        {
            using (var stream = new CountingStream())
            {
                file.Save(stream);
                return stream.Length;
            }
        }

        private sealed class CountingStream : Stream
        {
            private long _length;
            private long _position;

            public override bool CanRead => false;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => _length;
            public override long Position { get => _position; set => _position = value; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _position += count;
                if (_position > _length) _length = _position;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin: _position = offset; break;
                    case SeekOrigin.Current: _position += offset; break;
                    default: _position = _length + offset; break;
                }
                return _position;
            }

            public override void SetLength(long value) => _length = value;
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
