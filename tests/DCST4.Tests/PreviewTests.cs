using DCST4.Core.Generation;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DCST4.Tests
{
    /// <summary>1.12.0: the image preview of the Exam tab, rendered in DCST4.Core.</summary>
    internal static class PreviewTests
    {
        private static Action<string, bool, string> _check;

        public static void Run(Action<string, bool, string> check)
        {
            _check = check;
            Console.WriteLine("\nImage preview");

            var ct = Render(ModalityCatalog.Find("CT"), DicomTransferSyntax.ExplicitVRLittleEndian, 256, 192);
            Check("CT: the size of the matrix", ct.Width == 192 && ct.Height == 256, ct.Width + " x " + ct.Height);
            Check("CT: four bytes for every pixel", ct.Bgra != null && ct.Bgra.Length == ct.Width * ct.Height * 4, ct.Bgra?.Length.ToString());
            Check("CT: not one flat value", Grays(ct).Distinct().Count() > 2);
            Check("CT: opaque", Enumerable.Range(0, ct.Width * ct.Height).All(i => ct.Bgra[i * 4 + 3] == 255));
            Check("CT: says what it shows, and the window", ct.Description.Contains("CT") && ct.Description.Contains("192 x 256") && ct.Description.Contains("Window"), ct.Description);
            Check("CT: no note", ct.Note == null, ct.Note);
            Check("CT: file size", ct.FileBytes > 192 * 256 * 2, ct.FileBytes?.ToString());

            var lossless = Render(ModalityCatalog.Find("CR"), DicomTransferSyntax.ExplicitVRLittleEndian, 128, 128, bits: 8);
            var lossy = Render(ModalityCatalog.Find("CR"), DicomTransferSyntax.JPEGProcess1, 128, 128, new LossyCompressionSettings { JpegQuality = 10 }, bits: 8);
            Check("JPEG Baseline: decoded and rendered", lossy.Width == 128 && lossy.Height == 128 && lossy.Note == null, lossy.Note);
            Check("JPEG Baseline: named in the description", lossy.Description.Contains(DicomTransferSyntax.JPEGProcess1.UID.Name), lossy.Description);
            Check("JPEG Baseline at quality 10: smaller than native", lossy.FileBytes < lossless.FileBytes, lossy.FileBytes + " vs " + lossless.FileBytes);
            Check("JPEG Baseline at quality 10: the pixels changed", !lossy.Bgra.SequenceEqual(lossless.Bgra));

            var colour = Render(new SecondaryCaptureProfile(), DicomTransferSyntax.ExplicitVRLittleEndian, 128, 128);
            var colourful = Enumerable.Range(0, colour.Width * colour.Height).Any(i => colour.Bgra[i * 4] != colour.Bgra[i * 4 + 2]);
            Check("Colour: rendered in colour, without a window", colourful && !colour.Description.Contains("Window"), colour.Description);

            var cine = ModalityCatalog.FindObject("us-multiframe").Image;
            var sw = Stopwatch.StartNew();
            var loop = Render(cine, DicomTransferSyntax.JPEGProcess1, 256, 256, frames: 300);
            Check("Cine loop of 300 frames: one frame, quickly", loop.Width == 256 && sw.Elapsed < TimeSpan.FromSeconds(10) && loop.Description.Contains("with one frame"), sw.ElapsedMilliseconds + " ms, " + loop.Description);

            var jpegXl = Render(ModalityCatalog.Find("CT"), TransferSyntaxOption.JpegXlLossless.Syntax, 128, 128);
            Check("JPEG XL: shown before compression, with a note", jpegXl.Width == 128 && jpegXl.Note != null && jpegXl.FileBytes == null, jpegXl.Note);

            foreach (var name in new[] { "SR", "DOC" })
            {
                var threw = false;
                try { Render(ModalityCatalog.ObjectsOf(ModalityCatalog.Find(name))[0].Image, DicomTransferSyntax.ExplicitVRLittleEndian, 128, 128); }
                catch (InvalidOperationException) { threw = true; }
                Check(name + ": no pixels to preview", threw);
            }

            var cancelled = false;
            try
            {
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    ImagePreview.Render(Request(ModalityCatalog.Find("CT"), 128, 128), DicomTransferSyntax.ExplicitVRLittleEndian, cts.Token);
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check("Cancelled before it starts", cancelled);
        }

        /// <summary>1.13.0: the tags preview, every tag of the first object as it would be sent.</summary>
        public static void RunTags(Action<string, bool, string> check)
        {
            _check = check;
            Console.WriteLine("\nTags preview");

            var ctRequest = Request(ModalityCatalog.Find("CT"), 128, 96);
            var ct = TagPreview.Build(ctRequest, DicomTransferSyntax.ExplicitVRLittleEndian, CancellationToken.None);
            Func<TagPreviewResult, string, TagPreviewRow> row = (result, tag) => result.Rows.FirstOrDefault(r => r.Tag == tag);
            Check("Tags: File Meta Information first, marked as such", ct.Rows.Count > 0 && ct.Rows[0].Tag.StartsWith("(0002,", StringComparison.Ordinal) && ct.Rows[0].IsFileMeta
                && ct.Rows.Where(r => r.Tag.StartsWith("(0002,", StringComparison.Ordinal)).All(r => r.IsFileMeta) && ct.Rows.Where(r => r.IsFileMeta).All(r => r.Tag.StartsWith("(0002,", StringComparison.Ordinal)), ct.Rows.FirstOrDefault()?.Tag);
            Check("Tags: the transfer syntax in the File Meta Information", row(ct, "(0002,0010)")?.Value == DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID, row(ct, "(0002,0010)")?.Value);
            Check("Tags: the Patient ID of the identity", row(ct, "(0010,0020)")?.Value == ctRequest.Identity.PatientId && ct.PatientId == ctRequest.Identity.PatientId, row(ct, "(0010,0020)")?.Value);
            Check("Tags: the name and VR from the dictionary", row(ct, "(0010,0020)")?.Name == "Patient ID" && row(ct, "(0010,0020)")?.Vr == "LO", row(ct, "(0010,0020)")?.Name);
            Check("Tags: Manufacturer DCST4", row(ct, "(0008,0070)")?.Value == "DCST4", row(ct, "(0008,0070)")?.Value);
            Check("Tags: Pixel Data as its size", row(ct, "(7FE0,0010)")?.Value == "(" + (128 * 96 * 2).ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " bytes)", row(ct, "(7FE0,0010)")?.Value);
            Check("Tags: binary numbers as numbers, Rows and Columns", row(ct, "(0028,0010)")?.Value == "128" && row(ct, "(0028,0011)")?.Value == "96" && row(ct, "(0028,0010)")?.Vr == "US",
                row(ct, "(0028,0010)")?.Value + " x " + row(ct, "(0028,0011)")?.Value);
            Check("Tags: multi-valued numbers with a backslash", (row(ct, "(0020,0037)")?.Value ?? string.Empty).Split('\\').Length == 6, row(ct, "(0020,0037)")?.Value);
            Check("Tags: in tag order within the dataset", ct.Rows.Where(r => r.Depth == 0 && !r.IsFileMeta).Select(r => r.Tag).SequenceEqual(ct.Rows.Where(r => r.Depth == 0 && !r.IsFileMeta).Select(r => r.Tag).OrderBy(t => t, StringComparer.Ordinal)), null);
            Check("Tags: the description names the object, syntax and size", ct.Description.Contains("CT") && ct.Description.Contains("the first of 1 object") && ct.Description.Contains(DicomTransferSyntax.ExplicitVRLittleEndian.UID.Name) && ct.Description.Contains("KB"), ct.Description);

            var several = Request(ModalityCatalog.Find("CT"), 128, 96);
            several.SeriesCount = 3;
            several.ImagesPerSeries = 50;
            var sw = Stopwatch.StartNew();
            var first = TagPreview.Build(several, DicomTransferSyntax.ExplicitVRLittleEndian, CancellationToken.None);
            Check("Tags: of an exam of 150 images, only the first is made", first.Description.Contains("the first of 150 objects") && row(first, "(0020,0013)")?.Value == "1" && sw.Elapsed < TimeSpan.FromSeconds(10), sw.ElapsedMilliseconds + " ms, " + first.Description);

            var lossyRequest = Request(ModalityCatalog.Find("CR"), 128, 128);
            lossyRequest.BitsStored = 8;
            lossyRequest.Lossy = new LossyCompressionSettings { JpegQuality = 50 };
            var lossy = TagPreview.Build(lossyRequest, DicomTransferSyntax.JPEGProcess1, CancellationToken.None);
            Check("Tags: a lossy syntax as it is sent", row(lossy, "(0002,0010)")?.Value == DicomTransferSyntax.JPEGProcess1.UID.UID && row(lossy, "(0028,2110)")?.Value == "01", row(lossy, "(0028,2110)")?.Value);
            Check("Tags: encapsulated Pixel Data as fragments", (row(lossy, "(7FE0,0010)")?.Value ?? string.Empty).StartsWith("(encapsulated, ", StringComparison.Ordinal), row(lossy, "(7FE0,0010)")?.Value);
            Check("Tags: a lossy setting in Derivation Description", (row(lossy, "(0008,2111)")?.Value ?? string.Empty).Contains("50"), row(lossy, "(0008,2111)")?.Value);

            var payloadRequest = Request(ModalityCatalog.Find("US"), 128, 128);
            payloadRequest.PrivatePayloadBytes = 1000;
            var payload = TagPreview.Build(payloadRequest, DicomTransferSyntax.ExplicitVRLittleEndian, CancellationToken.None);
            Check("Tags: a private tag with its creator", payload.Rows.Any(r => r.Name.StartsWith("Private tag (", StringComparison.Ordinal)) && payload.Rows.Any(r => r.Name == "Private Creator"), null);

            var srModality = ModalityCatalog.ObjectsOf(ModalityCatalog.Find("SR"))[0].Image;
            var sr = TagPreview.Build(Request(srModality, 128, 128), DicomTransferSyntax.ExplicitVRLittleEndian, CancellationToken.None);
            var content = sr.Rows.FirstOrDefault(r => r.Tag == "(0040,A730)");
            Check("Tags: SR works, though it has no pixels", sr.Rows.Count > 20 && row(sr, "(7FE0,0010)") == null, sr.Description);
            Check("Tags: a sequence with its items, one level deeper each", content != null && System.Text.RegularExpressions.Regex.IsMatch(content.Value, @"^\d+ items?$")
                && sr.Rows.Any(r => r.Depth == content.Depth + 1 && r.Name == "Item 1") && sr.Rows.Any(r => r.Depth == content.Depth + 2), content?.Value);
            Check("Tags: nested sequences go deeper", sr.Rows.Max(r => r.Depth) >= 3, sr.Rows.Max(r => r.Depth).ToString());
            Check("Tags: no value is longer than the limit", sr.Rows.Concat(ct.Rows).All(r => r.Value.Length <= TagPreview.MaxValueLength + 1), null);

            var cancelled = false;
            try
            {
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    TagPreview.Build(Request(ModalityCatalog.Find("CT"), 128, 128), DicomTransferSyntax.ExplicitVRLittleEndian, cts.Token);
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check("Tags: cancelled before it starts", cancelled);
        }

        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        private static PreviewImage Render(ModalityProfile modality, DicomTransferSyntax syntax, int rows, int columns, LossyCompressionSettings lossy = null, int frames = 1, int bits = 0)
        {
            var request = Request(modality, rows, columns, frames);
            if (bits > 0) request.BitsStored = bits;
            request.Lossy = lossy;
            return ImagePreview.Render(request, syntax, CancellationToken.None);
        }

        private static ExamRequest Request(ModalityProfile modality, int rows, int columns, int frames = 1)
        {
            var uids = new DeterministicUidSource(null, "preview-tests/" + modality.DisplayName);
            var identity = new IdentityGenerator(random: new Random(11)).Generate(null, modality, modality.Presets[0]);
            identity.StudyInstanceUid = uids.Uid("study");
            identity.StudyDateTime = new DateTime(2026, 11, 17, 9, 0, 0);
            identity.StudyDateTimeIsAuto = false;
            return new ExamRequest
            {
                Modality = modality,
                Preset = modality.Presets[0],
                Identity = identity,
                Rows = rows,
                Columns = columns,
                BitsStored = modality.DefaultBitDepth,
                FramesPerImage = modality.IsMultiFrame ? frames : 1,
                Uids = uids,
            };
        }

        private static byte[] Grays(PreviewImage image) => Enumerable.Range(0, image.Width * image.Height).Select(i => image.Bgra[i * 4]).ToArray();
    }
}
