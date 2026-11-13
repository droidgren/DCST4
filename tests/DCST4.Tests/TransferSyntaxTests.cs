using DCST4.Core.Codecs;
using DCST4.Core.Codecs.JpegXl;
using DCST4.Core.Codecs.Video;
using DCST4.Core.Generation;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>
    /// 1.10.1: the Transfer syntax list by codec family, Deflated, DCST4's own JPEG XL encoder and H.264 through the encoder
    /// of Windows.
    /// </summary>
    internal static class TransferSyntaxTests
    {
        private static Action<string, bool, string> _check;
        private static int _port;

        private static readonly DicomTransferSyntax JpegXlLossless = TransferSyntaxOption.JpegXlLossless.Syntax;
        private static readonly DicomTransferSyntax JpegXl = TransferSyntaxOption.JpegXl.Syntax;
        private static readonly DicomTransferSyntax H264 = DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41;

        public static async Task Run(int port, string temp, Action<string, bool, string> check)
        {
            _check = check;
            _port = port;

            TestList();
            TestJpegXlEncoder();
            TestJpegXlImages();
            TestDeflated(temp);
            var h264 = TestH264();
            await TestSending(h264);
        }

        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        private static ModalityProfile Named(string datasetProfileId) => ModalityCatalog.FindObject(datasetProfileId)?.Image;

        private static ExamRequest Request(ModalityProfile modality, int frames = 1, int rows = 64, int columns = 64, int bits = 0, int images = 1)
        {
            var uids = new DeterministicUidSource(null, "transfer-syntax-tests/" + modality.DisplayName);
            var identity = new IdentityGenerator(random: new Random(5)).Generate(null, modality, modality.Presets[0]);
            identity.StudyInstanceUid = uids.Uid("study");
            identity.StudyDateTime = new DateTime(2026, 11, 13, 9, 0, 0);
            identity.StudyDateTimeIsAuto = false;
            return new ExamRequest
            {
                Modality = modality,
                Preset = modality.Presets[0],
                Identity = identity,
                ImagesPerSeries = images,
                Rows = rows,
                Columns = columns,
                BitsStored = bits > 0 ? bits : modality.DefaultBitDepth,
                FramesPerImage = modality.IsMultiFrame ? frames : 1,
                Uids = uids,
            };
        }

        private static DicomFile First(ExamRequest request) => new ExamGenerator(request).GetImages(CancellationToken.None).First().File;

        private static List<byte[]> NativeFrames(DicomDataset dataset)
        {
            var pixels = DicomPixelData.Create(dataset);
            var size = (int)pixels.UncompressedFrameSize;
            return Enumerable.Range(0, pixels.NumberOfFrames).Select(f => pixels.GetFrame(f).Data.Take(size).ToArray()).ToList();
        }

        private static byte[] Saved(DicomFile file)
        {
            using (var stream = new MemoryStream())
            {
                file.Save(stream);
                return stream.ToArray();
            }
        }

        private static DicomFragmentSequence Fragments(DicomDataset dataset) => dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) as DicomFragmentSequence;

        /// <summary>The samples of a decoded JPEG XL image in the byte layout of a native DICOM frame.</summary>
        private static byte[] AsNative(JpegXlSubsetDecoder.Image image, int bitsAllocated) => TestDataTests.JpegXlNative(image, bitsAllocated);

        // ---------------------------------------------------------------- the list

        private static void TestList()
        {
            Console.WriteLine("\nTransfer syntaxes: the list by codec family");
            var expected = new[]
            {
                "1.2.840.10008.1.2", "1.2.840.10008.1.2.1", "1.2.840.10008.1.2.1.99",
                "1.2.840.10008.1.2.5",
                "1.2.840.10008.1.2.4.50", "1.2.840.10008.1.2.4.51", "1.2.840.10008.1.2.4.70",
                "1.2.840.10008.1.2.4.80",
                "1.2.840.10008.1.2.4.90", "1.2.840.10008.1.2.4.91",
                "1.2.840.10008.1.2.4.201", "1.2.840.10008.1.2.4.203",
                "1.2.840.10008.1.2.4.110", "1.2.840.10008.1.2.4.112",
                "1.2.840.10008.1.2.4.102",
            };
            var uids = TransferSyntaxOption.All.Select(o => o.Syntax.UID.UID).ToList();
            Check("Generate & Send offers the 15 syntaxes, in family order", uids.SequenceEqual(expected), string.Join(", ", uids));

            var families = TransferSyntaxOption.All.Select(o => o.Family).ToList();
            var changes = families.Zip(families.Skip(1), (a, b) => a != b).Count(changed => changed);
            Check("Each family is one block, in the order of the families", families.Distinct().SequenceEqual(TransferSyntaxFamily.All) && changes == TransferSyntaxFamily.All.Count - 1, string.Join(", ", families));
            Check("The names are unique, and lossy ones say so", TransferSyntaxOption.All.Select(o => o.Name).Distinct().Count() == 15
                && TransferSyntaxOption.All.Where(o => o.Syntax.IsLossy).All(o => o.Name.Contains("lossy")), string.Join(" | ", TransferSyntaxOption.All.Select(o => o.Name)));
            Check("Every syntax of the list can be generated", TransferSyntaxOption.All.All(o => CompressionCatalog.For(o.Syntax) != null), string.Join(", ", TransferSyntaxOption.All.Where(o => CompressionCatalog.For(o.Syntax) == null).Select(o => o.Name)));
            Check("The command line knows deflated, jpeg-xl, jpeg-xl-lossy and h264",
                CompressionCatalog.Find("deflated")?.Syntax == DicomTransferSyntax.DeflatedExplicitVRLittleEndian && CompressionCatalog.Find("jpeg-xl")?.Syntax == JpegXlLossless
                && CompressionCatalog.Find("jpeg-xl-lossy")?.Syntax == JpegXl && CompressionCatalog.Find("h264")?.Syntax == H264, null);
            Check("Deflated is with the uncompressed syntaxes of the datasets", CompressionCatalog.Uncompressed.Any(c => c.Id == "deflated") && CompressionCatalog.Compressed.All(c => c.Id != "deflated"), null);
            Check("JPEG XL and H.264 cannot be decoded here, the PureCodecs syntaxes can", !OwnCodecs.CanDecode(JpegXlLossless) && !OwnCodecs.CanDecode(JpegXl) && !OwnCodecs.CanDecode(H264)
                && OwnCodecs.CanDecode(DicomTransferSyntax.RLELossless) && OwnCodecs.CanDecode(DicomTransferSyntax.HTJ2K) && OwnCodecs.CanDecode(DicomTransferSyntax.DeflatedExplicitVRLittleEndian), null);
        }

        // ---------------------------------------------------------------- JPEG XL

        private static void TestJpegXlEncoder()
        {
            Console.WriteLine("\nTransfer syntaxes: the JPEG XL encoder");
            var random = new Random(11);
            var cases = new[]
            {
                new { Name = "1 x 1, 8 bits", W = 1, H = 1, Bits = 8, Channels = 1, Noise = true },
                new { Name = "grey 8 bits, flat", W = 40, H = 30, Bits = 8, Channels = 1, Noise = false },
                new { Name = "grey 12 bits, 300 x 260 (four groups)", W = 300, H = 260, Bits = 12, Channels = 1, Noise = false },
                new { Name = "grey 16 bits, noise", W = 70, H = 50, Bits = 16, Channels = 1, Noise = true },
                new { Name = "grey 10 bits, 2100 x 12 (two LF groups)", W = 2100, H = 12, Bits = 10, Channels = 1, Noise = true },
                new { Name = "RGB 8 bits, 270 x 40", W = 270, H = 40, Bits = 8, Channels = 3, Noise = false },
            };
            foreach (var c in cases)
            {
                var max = (1 << c.Bits) - 1;
                var planes = Enumerable.Range(0, c.Channels).Select(ch => Enumerable.Range(0, c.W * c.H)
                    .Select(i => c.Noise ? random.Next(max + 1) : ((i % c.W) * 7 + (i / c.W) * 3 + ch * 50) % (max + 1)).ToArray()).ToList();
                var codestream = JpegXlEncoder.EncodeLossless(c.W, c.H, c.Bits, planes);
                JpegXlSubsetDecoder.Image decoded = null;
                string problem = null;
                try { decoded = JpegXlSubsetDecoder.Decode(codestream); }
                catch (FormatException ex) { problem = ex.Message; }
                Check($"JPEG XL {c.Name}: the samples come back exactly", decoded != null && decoded.Width == c.W && decoded.Height == c.H && decoded.Bits == c.Bits && decoded.Grey == (c.Channels == 1)
                    && Enumerable.Range(0, c.Channels).All(ch => decoded.Channels[ch].SequenceEqual(planes[ch])), problem);
            }

            var image = new[] { Enumerable.Range(0, 64 * 64).Select(i => i % 4096).ToArray() };
            var once = JpegXlEncoder.EncodeLossless(64, 64, 12, image);
            var twice = JpegXlEncoder.EncodeLossless(64, 64, 12, image);
            Check("JPEG XL: a bare codestream (FF 0A), the same bytes for the same image", once[0] == 0xFF && once[1] == 0x0A && once.SequenceEqual(twice), null);
            Check("JPEG XL: a smooth image compresses", once.Length < 64 * 64 * 2 / 3, once.Length.ToString());

            var refused = false;
            try { JpegXlEncoder.EncodeLossless(2, 2, 8, new[] { new[] { 0, 1, 256, 3 } }); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            Check("JPEG XL: a sample above Bits Stored is refused", refused, null);
        }

        private static void TestJpegXlImages()
        {
            Console.WriteLine("\nTransfer syntaxes: JPEG XL images");
            Action<string, ExamRequest, DicomTransferSyntax> exact = (name, request, syntax) =>
            {
                var original = First(request);
                var frames = NativeFrames(original.Dataset);
                var bitsAllocated = original.Dataset.GetSingleValue<int>(DicomTag.BitsAllocated);
                var progress = 0;
                var converted = TransferSyntaxConverter.Convert(original, syntax, CancellationToken.None, (done, total) => progress++);
                var dataset = DicomFile.Open(new MemoryStream(Saved(converted))).Dataset;
                var fragments = Fragments(dataset);
                var decoded = fragments?.Fragments.Select(f =>
                {
                    try { return AsNative(JpegXlSubsetDecoder.Decode(f.Data), bitsAllocated); }
                    catch (FormatException) { return null; }
                }).ToList();

                Check($"{name}: {syntax.UID.Name}, one codestream per frame", dataset.InternalTransferSyntax == syntax && fragments != null && fragments.Fragments.Count == frames.Count
                    && fragments.Fragments.All(f => f.Size % 2 == 0 && f.Data[0] == 0xFF && f.Data[1] == 0x0A), fragments?.Fragments.Count.ToString());
                Check($"{name}: every frame decodes to the pixels as drawn", decoded != null && decoded.Count == frames.Count && decoded.Zip(frames, (a, b) => a != null && a.SequenceEqual(b)).All(same => same), null);
                Check($"{name}: not marked lossy", dataset.GetSingleValueOrDefault(DicomTag.LossyImageCompression, "00") == "00", dataset.GetSingleValueOrDefault(DicomTag.LossyImageCompression, string.Empty));
                if (frames.Count > 1) Check($"{name}: progress for each frame", progress == frames.Count, progress.ToString());
            };

            exact("CR 12 bits", Request(ModalityCatalog.Find("CR"), rows: 128, columns: 96, bits: 12), JpegXlLossless);
            exact("MG 16 bits", Request(ModalityCatalog.Find("MG"), rows: 80, columns: 64, bits: 16), JpegXlLossless);
            exact("Secondary Capture RGB", Request(new SecondaryCaptureProfile(), rows: 48, columns: 64), JpegXl);
            exact("XA cine, 5 frames drawn on demand", Request(Named("xa-cine"), frames: 5, rows: 64, columns: 64, bits: 8), JpegXlLossless);
            exact("US cine RGB, 3 frames", Request(Named("us-multiframe"), frames: 3, rows: 40, columns: 50), JpegXl);

            var probes = CompressionCatalog.ProbeAll().Where(p => p.SyntaxId.StartsWith("jpeg-xl", StringComparison.Ordinal)).ToList();
            Check("JPEG XL: available at 8, 12 and 16 bits and for RGB, in both syntaxes", probes.Count == 8 && probes.All(p => p.Available), string.Join("; ", probes.Where(p => !p.Available).Select(p => p.SyntaxId + " " + p.BitsStored + ": " + p.Reason)));
            Check("JPEG XL: fine for CT in Generate & Send", ExamValidator.SyntaxProblems(Request(ModalityCatalog.Find("CT")), JpegXlLossless).Count == 0, string.Join(" ", ExamValidator.SyntaxProblems(Request(ModalityCatalog.Find("CT")), JpegXlLossless)));
        }

        // ---------------------------------------------------------------- Deflated

        private static void TestDeflated(string temp)
        {
            Console.WriteLine("\nTransfer syntaxes: Deflated Explicit VR Little Endian");
            var deflated = DicomTransferSyntax.DeflatedExplicitVRLittleEndian;
            var request = Request(ModalityCatalog.Find("CT"), rows: 128, columns: 128);
            var original = First(request);
            var path = Path.Combine(temp, "deflated.dcm");
            TransferSyntaxConverter.Convert(original, deflated).Save(path);
            var explicitBytes = Saved(TransferSyntaxConverter.Convert(original, DicomTransferSyntax.ExplicitVRLittleEndian)).Length;
            var reopened = DicomFile.Open(path);
            Check("Deflated: the saved file is that syntax, and smaller than Explicit VR", reopened.Dataset.InternalTransferSyntax == deflated && new FileInfo(path).Length < explicitBytes / 2,
                new FileInfo(path).Length + " / " + explicitBytes);
            Check("Deflated: the saved file reads back with the same pixels", NativeFrames(reopened.Dataset)[0].SequenceEqual(NativeFrames(original.Dataset)[0]), null);
            Check("Deflated: no problem for CT, and its size is measured, not calculated",
                ExamValidator.SyntaxProblems(request, deflated).Count == 0 && ExamSizeEstimator.MeasureImageBytes(request, deflated, CancellationToken.None) < ExamSizeEstimator.UncompressedImageBytes(request), null);
        }

        // ---------------------------------------------------------------- H.264

        /// <summary>Returns whether this computer has the encoder, which Windows N does not have without the Media Feature Pack.</summary>
        private static bool TestH264()
        {
            Console.WriteLine("\nTransfer syntaxes: H.264 High Profile / Level 4.1");
            var cine = Named("us-multiframe");
            var choice = CompressionCatalog.Find("h264");

            Func<ExamRequest, string> problems = r => string.Join(" ", ExamValidator.SyntaxProblems(r, H264));
            Check("H.264: XA cine is refused, since its IOD requires MONOCHROME2", problems(Request(Named("xa-cine"), frames: 4)).Contains("MONOCHROME2"), problems(Request(Named("xa-cine"), frames: 4)));
            Check("H.264: single-frame US and CT are refused", problems(Request(ModalityCatalog.Find("US"))).Contains("colour video") && problems(Request(ModalityCatalog.Find("CT"))).Contains("colour video"), problems(Request(ModalityCatalog.Find("US"))));
            Check("H.264: more than 1080 x 1920, or an odd size, is refused", problems(Request(cine, frames: 4, rows: 1200, columns: 1600)).Contains("1080") && problems(Request(cine, frames: 4, rows: 63, columns: 64)).Contains("even"),
                problems(Request(cine, frames: 4, rows: 1200, columns: 1600)));
            Check("H.264: the test suite's US cine fits, its XA cine does not", CompressionCatalog.FitProblem(choice, cine, 960, 1280) == null
                && new TestDatasetRequest { ProfileId = "xa-cine", TransferSyntax = H264 }.Validate().Any(p => p.Contains("MONOCHROME2")), null);
            Check("H.264: 1080 x 1920 at 30 frames per second is within Level 4.1, at 60 it is not", H264Encoder.LimitProblem(1920, 1080, 30) == null && H264Encoder.LimitProblem(1920, 1080, 60) != null, H264Encoder.LimitProblem(1920, 1080, 60));

            var probe = CompressionCatalog.Probe(choice, 8, 3);
            var grey = CompressionCatalog.Probe(choice, 8, 1);
            Check("H.264: grayscale is reported as not possible", !grey.Available && grey.Reason.Contains("colour"), grey.Reason);
            if (!probe.Available)
            {
                Check("H.264: this computer has no H.264 encoder, and the reason says what to install", probe.Reason.Contains("Media Feature Pack"), probe.Reason);
                return false;
            }
            Check("H.264: no problem for a US cine loop of 128 x 160", problems(Request(cine, frames: 6, rows: 128, columns: 160)).Length == 0, problems(Request(cine, frames: 6, rows: 128, columns: 160)));

            var original = First(Request(cine, frames: 6, rows: 128, columns: 160));
            var rgb = NativeFrames(original.Dataset);
            var progress = new List<string>();
            var converted = TransferSyntaxConverter.Convert(original, H264, CancellationToken.None, (done, total) => progress.Add(done + "/" + total));
            var dataset = DicomFile.Open(new MemoryStream(Saved(converted))).Dataset;
            var fragments = Fragments(dataset);
            var stream = fragments?.Fragments.Count == 1 ? fragments.Fragments[0].Data : new byte[0];

            Check("H.264: the whole loop is one fragment, with an empty Basic Offset Table", dataset.InternalTransferSyntax == H264 && fragments != null && fragments.Fragments.Count == 1 && fragments.OffsetTable.Count == 0 && stream.Length % 2 == 0, fragments?.Fragments.Count.ToString());
            Check("H.264: an MP4 container (PS3.5 8.2.7)", stream.Length > 8 && Encoding.ASCII.GetString(stream, 4, 4) == "ftyp", null);
            var text = Encoding.ASCII.GetString(stream);
            var avcC = text.IndexOf("avcC", StringComparison.Ordinal);
            Check("H.264: High Profile, Level 4.1 in the stream", avcC > 0 && stream[avcC + 5] == 100 && stream[avcC + 7] == 41, avcC > 0 ? $"profile {stream[avcC + 5]} level {stream[avcC + 7]}" : "no avcC");
            Check("H.264: YBR_PARTIAL_420, 8 bits, planar configuration 0, 6 frames",
                dataset.GetSingleValue<string>(DicomTag.PhotometricInterpretation) == "YBR_PARTIAL_420" && dataset.GetSingleValue<int>(DicomTag.BitsStored) == 8
                && dataset.GetSingleValue<int>(DicomTag.PlanarConfiguration) == 0 && dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == 6 && dataset.GetSingleValue<int>(DicomTag.SamplesPerPixel) == 3, null);
            Check("H.264: marked lossy with ISO_14496_10, Image Type DERIVED, square pixels",
                dataset.GetSingleValue<string>(DicomTag.LossyImageCompression) == "01" && dataset.GetSingleValue<string>(DicomTag.LossyImageCompressionMethod) == "ISO_14496_10"
                && dataset.GetSingleValue<double>(DicomTag.LossyImageCompressionRatio) > 1 && dataset.GetValues<string>(DicomTag.ImageType)[0] == "DERIVED" && !dataset.Contains(DicomTag.PixelAspectRatio), null);
            Check("H.264: progress for each frame", progress.Count == 6 && progress.Last() == "6/6", string.Join(",", progress));
            Check("H.264: the pixel data size is the stream", TransferSyntaxConverter.PixelBytes(dataset) == stream.Length, null);

            var decoded = H264Encoder.DecodeToNv12(stream, out var columns, out var rows);
            Check("H.264: Windows' decoder gives back 6 frames of 160 x 128", decoded.Count == 6 && columns == 160 && rows == 128, decoded.Count + " frames of " + columns + " x " + rows);
            if (decoded.Count > 0 && columns == 160)
            {
                var nv12 = new byte[160 * 128 * 3 / 2];
                H264Encoder.RgbToNv12(rgb[0], 160, 128, nv12);
                var difference = Enumerable.Range(0, 160 * 128).Average(i => Math.Abs(decoded[0][i] - nv12[i]));
                Check("H.264: the first frame's luma is close to the RGB as BT.601 limited range", difference < 4, difference.ToString("0.00"));
            }

            var cancelled = false;
            using (var cts = new CancellationTokenSource())
            {
                try { TransferSyntaxConverter.Convert(First(Request(cine, frames: 6, rows: 64, columns: 64)), H264, cts.Token, (done, total) => { if (done == 2) cts.Cancel(); }); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Check("H.264: Cancel stops the encoding", cancelled, null);
            return true;
        }

        // ---------------------------------------------------------------- sending

        private static StoreRunOptions SendOptions(DicomTransferSyntax syntax, bool fallback = false) => new StoreRunOptions
        {
            Send = true,
            TransferSyntax = syntax,
            AllowFallback = fallback,
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = _port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 30 },
        };

        private static async Task TestSending(bool h264)
        {
            Console.WriteLine("\nTransfer syntaxes: sending");
            var ct = Request(ModalityCatalog.Find("CT"), rows: 96, columns: 96, images: 2);
            var expected = new ExamGenerator(ct).GetImages(CancellationToken.None).Select(i => NativeFrames(i.File.Dataset)[0]).ToList();

            foreach (var syntax in new[] { DicomTransferSyntax.DeflatedExplicitVRLittleEndian, DicomTransferSyntax.RLELossless, DicomTransferSyntax.JPEGLSLossless, DicomTransferSyntax.JPEG2000Lossless, DicomTransferSyntax.HTJ2KLossless })
            {
                Scp.Reset();
                Scp.AcceptedSyntaxes = new[] { syntax };
                var summary = await new StoreRun(new ExamGenerator(ct), SendOptions(syntax)).ExecuteAsync(CancellationToken.None);
                var received = Scp.Received.ToList();
                var pixels = received.Select(r => NativeFrames(r.Syntax.IsEncapsulated ? new DicomFile(r.Dataset).Clone(DicomTransferSyntax.ExplicitVRLittleEndian).Dataset : r.Dataset)[0]).ToList();
                Check($"{syntax.UID.Name}: 2 CT images arrive in it, with the pixels as drawn", summary.Success == 2 && received.Count == 2 && received.All(r => r.Syntax == syntax)
                    && pixels.Zip(expected, (a, b) => a.SequenceEqual(b)).All(same => same), summary + " / " + summary.FatalError);
            }

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { JpegXlLossless };
            var jxl = await new StoreRun(new ExamGenerator(ct), SendOptions(JpegXlLossless)).ExecuteAsync(CancellationToken.None);
            var jxlReceived = Scp.Received.ToList();
            var jxlPixels = jxlReceived.Select(r => AsNative(JpegXlSubsetDecoder.Decode(r.Frame), 16)).ToList();
            Check("JPEG XL Lossless: 2 CT images arrive in it, and decode to the pixels as drawn", jxl.Success == 2 && jxlReceived.All(r => r.Syntax == JpegXlLossless)
                && jxlPixels.Zip(expected, (a, b) => a.SequenceEqual(b)).All(same => same), jxl + " / " + jxl.FatalError);

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
            var noFallback = await new StoreRun(new ExamGenerator(ct), SendOptions(JpegXl, fallback: true)).ExecuteAsync(CancellationToken.None);
            Check("JPEG XL to an SCP without it, fallback allowed: nothing is sent, and the error says there is no decoder", Scp.StoreCount == 0 && noFallback.Success == 0
                && noFallback.FatalError != null && noFallback.FatalError.Contains("no decoder"), noFallback.FatalError);

            if (h264)
            {
                Scp.Reset();
                Scp.AcceptedSyntaxes = new[] { H264 };
                var loop = Request(Named("us-multiframe"), frames: 4, rows: 64, columns: 96);
                var video = await new StoreRun(new ExamGenerator(loop), SendOptions(H264)).ExecuteAsync(CancellationToken.None);
                var arrived = Scp.Received.ToList();
                Check("H.264: a US cine loop arrives in it, as one MP4 stream of 4 frames", video.Success == 1 && arrived.Count == 1 && arrived[0].Syntax == H264
                    && arrived[0].Dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == 4 && Encoding.ASCII.GetString(arrived[0].Frame, 4, 4) == "ftyp" && video.PixelBytes == arrived[0].Frame.Length,
                    video + " / " + video.FatalError);
            }
            Scp.Reset();
        }
    }
}
