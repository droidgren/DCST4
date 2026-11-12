using FellowOakDicom;
using FellowOakDicom.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCST4.Core.Generation.Datasets
{
    /// <summary>
    /// What was generated, so that later phases can check what a PACS returns. It has no wall-clock field, so the same request
    /// gives the same manifest, as far as the guarantee in <see cref="Guarantee"/> holds.
    /// </summary>
    public sealed class DatasetManifest
    {
        public const int CurrentSchemaVersion = 1;
        public const string Guarantee =
            "The same seed, parameters, personnummer list and reference date give byte-identical files within one DCST4 version, " +
            "one fo-dicom and PureCodecs version, and the same Windows GDI+ and Consolas rendering. On another computer the UIDs, names " +
            "and all non-pixel attributes still match, and the pixels match only if the rendering does.";

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string GuaranteeText { get; set; } = Guarantee;
        public ManifestTools Tools { get; set; }
        public ManifestRequest Request { get; set; }
        /// <summary>The list the Patient IDs came from: its source and fetch date.</summary>
        public string RegistrySource { get; set; }
        public List<ManifestInstance> Instances { get; set; } = new List<ManifestInstance>();
        public List<ManifestUnavailable> Unavailable { get; set; } = new List<ManifestUnavailable>();

        private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                // Keeps å, ä and ö readable.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        public void Save(string path) => File.WriteAllText(path, ToJson(), new UTF8Encoding(false));

        public static DatasetManifest Load(string path) => JsonSerializer.Deserialize<DatasetManifest>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
    }

    /// <summary>The versions the guarantee depends on.</summary>
    public sealed class ManifestTools
    {
        public string Dcst4Version { get; set; }
        public string FoDicomVersion { get; set; }
        public string PureCodecsVersion { get; set; }
        public string OsVersion { get; set; }
        public string ConsolasVersion { get; set; }

        internal static ManifestTools Current() => new ManifestTools
        {
            Dcst4Version = ProductInfo.Version,
            FoDicomVersion = typeof(DicomFile).Assembly.GetName().Version.ToString(),
            PureCodecsVersion = typeof(FellowOakDicom.PureCodecs.PureTranscoderManager).Assembly.GetName().Version.ToString(),
            OsVersion = Environment.OSVersion.VersionString,
            ConsolasVersion = ConsolasFileVersion(),
        };

        private static string ConsolasFileVersion()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf");
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
    }

    public sealed class ManifestRequest
    {
        public string ProfileId { get; set; }
        public int Seed { get; set; }
        public string UidRoot { get; set; }
        public string ReferenceDate { get; set; }
        public string PatientNaming { get; set; }
        public int Patients { get; set; }
        public int StudiesPerPatient { get; set; }
        public int SeriesPerStudy { get; set; }
        public int InstancesPerSeries { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int BitsStored { get; set; }
        public int Frames { get; set; }
        public string TransferSyntaxUid { get; set; }
        public string CharacterSetVariant { get; set; }
        public string EdgeCases { get; set; }
    }

    /// <summary>One generated object.</summary>
    public sealed class ManifestInstance
    {
        public const string RoleTarget = "target";
        public const string RoleSourceImage = "source-image";

        /// <summary>The profile's own object, or the image it refers to.</summary>
        public string Role { get; set; }
        public string SopClassUid { get; set; }
        public string SopInstanceUid { get; set; }
        public string StudyInstanceUid { get; set; }
        public string SeriesInstanceUid { get; set; }
        public string TransferSyntaxUid { get; set; }
        /// <summary>SHA-256 of the whole file, in hexadecimal.</summary>
        public string FileSha256 { get; set; }
        /// <summary>SHA-256 of each frame, taken from the raw frames before any encoding, so that no decoder is involved.</summary>
        public List<string> FrameSha256 { get; set; } = new List<string>();
        /// <summary>SHA-256 of the non-pixel attributes in normalized form. See <see cref="NormalizedAttributes"/>.</summary>
        public string NormalizedAttributesSha256 { get; set; }
    }

    /// <summary>A combination that was asked for and could not be produced.</summary>
    public sealed class ManifestUnavailable
    {
        public string ProfileId { get; set; }
        public string TransferSyntaxUid { get; set; }
        public int BitsStored { get; set; }
        public int SamplesPerPixel { get; set; }
        public string Reason { get; set; }
    }

    public static class ManifestHashes
    {
        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data));
            }
        }

        public static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>Each frame's bytes, without the padding that makes an odd length even. Empty for an object with no Pixel Data.</summary>
        public static List<string> FrameHashes(DicomDataset dataset)
        {
            var hashes = new List<string>();
            if (!dataset.Contains(DicomTag.PixelData)) return hashes;

            // Frames that are drawn when read are asked for by number: fo-dicom counts a frame's position in 32 bits, which
            // a long cine loop goes past.
            if (dataset.GetDicomItem<DicomElement>(DicomTag.PixelData)?.Buffer is RenderedFramesBuffer rendered)
            {
                for (var frame = 0; frame < rendered.Frames; frame++) hashes.Add(Sha256Hex(rendered.ReadFrame(frame)));
                return hashes;
            }

            var pixelData = DicomPixelData.Create(dataset);
            var frameSize = (int)pixelData.UncompressedFrameSize;
            for (var frame = 0; frame < pixelData.NumberOfFrames; frame++)
            {
                var data = pixelData.GetFrame(frame).Data;
                hashes.Add(Sha256Hex(data.Length == frameSize ? data : data.Take(frameSize).ToArray()));
            }
            return hashes;
        }

        /// <summary>Written through a hash, so that a large object does not have to be held in memory.</summary>
        public static string FileHash(DicomFile file)
        {
            using (var sha = SHA256.Create())
            using (var hashing = new HashingStream(sha))
            {
                file.Save(hashing);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        /// <summary>A stream that only counts and hashes what is written to it.</summary>
        private sealed class HashingStream : Stream
        {
            private readonly HashAlgorithm _hash;
            private long _length;

            public HashingStream(HashAlgorithm hash)
            {
                _hash = hash;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _length;
            public override long Position { get => _length; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _hash.TransformBlock(buffer, offset, count, null, 0);
                _length += count;
            }
        }
    }

    /// <summary>
    /// A hash of a dataset that survives what a PACS may rightly change. Left out: the file meta group, group lengths, Specific
    /// Character Set, Pixel Data, trailing padding and private sequences. VRs are left out too, since Implicit VR loses them. Text counts as Unicode
    /// without trailing padding, so a change of character set makes no difference. Private elements are keyed by creator and
    /// element, since a PACS may move them to another block.
    /// </summary>
    public static class NormalizedAttributes
    {
        public static string Hash(DicomDataset dataset)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8))
            {
                WriteDataset(writer, dataset);
                writer.Flush();
                return ManifestHashes.Sha256Hex(buffer.ToArray());
            }
        }

        private static void WriteDataset(BinaryWriter writer, DicomDataset dataset)
        {
            foreach (var item in dataset.OrderBy(i => i.Tag.Group).ThenBy(i => i.Tag.Element))
            {
                if (IsLeftOut(item.Tag) || IsPrivateSequence(item) || IsCountOfOneFrame(item)) continue;

                writer.Write(item.Tag.Group);
                writer.Write(item.Tag.IsPrivate ? (ushort)(item.Tag.Element & 0x00FF) : item.Tag.Element);
                writer.Write(item.Tag.PrivateCreator?.Creator ?? string.Empty);

                if (item is DicomSequence sequence)
                {
                    writer.Write(sequence.Items.Count);
                    foreach (var nested in sequence.Items) WriteDataset(writer, nested);
                }
                else if (item is DicomElement element)
                {
                    WriteElement(writer, element);
                }
            }
        }

        /// <summary>Absent means one frame, and a transcoder adds the tag, so the two must hash the same.</summary>
        private static bool IsCountOfOneFrame(DicomItem item) =>
            item.Tag == DicomTag.NumberOfFrames && item is DicomElement element && element.Get<int>(0) == 1;

        internal static bool IsLeftOut(DicomTag tag) =>
            tag.Group == 0x0002 || tag.Element == 0x0000
            || tag == DicomTag.SpecificCharacterSet
            || tag == DicomTag.PixelData || tag == DicomTag.FloatPixelData || tag == DicomTag.DoubleFloatPixelData
            || tag == DicomTag.DataSetTrailingPadding
            // Private creators: their text is part of the key of each private element.
            || (tag.IsPrivate && tag.Element >= 0x0010 && tag.Element <= 0x00FF);

        /// <summary>
        /// In Implicit VR a private sequence is read back as UN bytes, since the dictionary does not know it, and those bytes are
        /// not comparable with the parsed sequence. It is left out in both forms. The whole-file hash still covers it.
        /// </summary>
        private static bool IsPrivateSequence(DicomItem item)
        {
            if (!item.Tag.IsPrivate) return false;
            if (item is DicomSequence) return true;

            if (!(item is DicomElement element) || element.ValueRepresentation != DicomVR.UN || element.Buffer.Size < ItemTagLittleEndian.Length) return false;
            var start = new byte[ItemTagLittleEndian.Length];
            element.Buffer.GetByteRange(0, start.Length, start);
            return start.SequenceEqual(ItemTagLittleEndian);
        }

        private static readonly byte[] ItemTagLittleEndian = { 0xFE, 0xFF, 0x00, 0xE0 };

        private static void WriteElement(BinaryWriter writer, DicomElement element)
        {
            if (element.ValueRepresentation.IsString)
            {
                var text = string.Join("\\", element.Get<string[]>().Select(v => (v ?? string.Empty).TrimEnd(' ', '\0')));
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            else
            {
                var bytes = element.Buffer.Data;
                if (element.ValueRepresentation == DicomVR.UN) bytes = TrimPadding(bytes);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        /// <summary>A private text can arrive as UN, where the padding is still there to be seen.</summary>
        private static byte[] TrimPadding(byte[] bytes)
        {
            var length = bytes.Length;
            while (length > 0 && (bytes[length - 1] == 0x20 || bytes[length - 1] == 0x00)) length--;
            return bytes.Take(length).ToArray();
        }
    }
}
