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
