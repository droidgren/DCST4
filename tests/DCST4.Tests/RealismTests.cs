using DCST4.Core.Generation;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Generation.Realism;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO.Buffer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DCST4.Tests
{
    /// <summary>1.12.0: the lossy compression settings and realistic images.</summary>
    internal static class RealismTests
    {
        private static Action<string, bool, string> _check;

        public static void Run(string temp, Action<string, bool, string> check)
        {
            _check = check;
            TestLossySettings();
            TestLossyEncoders();
            TestRealismDeterminism();
            TestRealismPixels();
            TestRealismShapes();
            TestTemporalCoherence();
            TestRealismDatasets(temp);
        }

        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        // ---------------------------------------------------------------- helpers

        private static ModalityProfile Named(string datasetProfileId) => ModalityCatalog.FindObject(datasetProfileId)?.Image;

        /// <summary>The same request always gives the same UIDs and identity, so two calls differ only in what is asked.</summary>
        private static ExamRequest Request(ModalityProfile modality, RealismLevel realism, int rows = 128, int columns = 128, int bits = 0, int frames = 1, int images = 1, bool text = false)
        {
            var uids = new DeterministicUidSource(null, "realism-tests/" + modality.DisplayName);
            var identity = new IdentityGenerator(random: new Random(7)).Generate(null, modality, modality.Presets[0]);
            identity.StudyInstanceUid = uids.Uid("study");
            identity.StudyDateTime = new DateTime(2026, 11, 16, 9, 0, 0);
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
                Realism = realism,
                BurnedInTags = text ? BurnedInTagCatalog.Default : null,
            };
        }

        /// <summary>A fixed clock, since Content Time is in the burned-in text.</summary>
        private static readonly DateTime Now = new DateTime(2026, 11, 16, 10, 0, 0);

        private static List<DicomFile> All(ExamRequest request) => new ExamGenerator(request, () => Now).GetImages(CancellationToken.None).Select(i => i.File).ToList();

        private static DicomFile First(ExamRequest request) => new ExamGenerator(request, () => Now).GetImages(CancellationToken.None).First().File;

        private static byte[] Saved(DicomFile file)
        {
            using (var stream = new MemoryStream())
            {
                file.Save(stream);
                return stream.ToArray();
            }
        }

        /// <summary>The samples of one native frame. RGB gives the green channel.</summary>
        private static int[] Samples(DicomDataset dataset, int frame = 0)
        {
            var pixels = DicomPixelData.Create(dataset);
            var data = pixels.GetFrame(frame).Data;
            var count = pixels.Width * pixels.Height;
            var samples = new int[count];
            for (var i = 0; i < count; i++)
            {
                if (pixels.SamplesPerPixel == 3) samples[i] = data[i * 3 + 1];
                else if (pixels.BitsAllocated > 8) samples[i] = data[2 * i] | (data[2 * i + 1] << 8);
                else samples[i] = data[i];
            }
            return samples;
        }

        private static int[] Decoded(DicomFile encoded) => Samples(encoded.Clone(DicomTransferSyntax.ExplicitVRLittleEndian).Dataset);

        private static long Bytes(DicomFile file) => TransferSyntaxConverter.PixelBytes(file.Dataset);

        private static long LosslessBytes(ExamRequest request) => Bytes(TransferSyntaxConverter.Convert(First(request), DicomTransferSyntax.JPEGLSLossless));

        private static DicomFile InMemoryCopy(DicomFile file)
        {
            var source = DicomPixelData.Create(file.Dataset);
            var size = (int)source.UncompressedFrameSize;
            var frames = Enumerable.Range(0, source.NumberOfFrames).Select(f => source.GetFrame(f).Data.Take(size).ToArray()).ToList();
            var copy = new DicomDataset(file.Dataset.InternalTransferSyntax);
            foreach (var item in file.Dataset.Where(i => i.Tag != DicomTag.PixelData)) copy.Add(item);
            var pixels = DicomPixelData.Create(copy, true);
            foreach (var frame in frames) pixels.AddFrame(new MemoryByteBuffer(frame));
            return new DicomFile(copy);
        }

        private static string Derivation(DicomFile file) => file.Dataset.GetSingleValueOrDefault(DicomTag.DerivationDescription, (string)null);

        // ---------------------------------------------------------------- lossy settings

        private static void TestLossySettings()
        {
            Console.WriteLine("\nLossy compression settings");

            var none = new LossyCompressionSettings();
            Check("Default: nothing set, valid, an empty key", none.IsDefault && none.Validate().Count == 0 && none.Key == string.Empty && none.Describe() == string.Empty, none.Key);

            var all = new LossyCompressionSettings { JpegQuality = 75, Jpeg2000Ratio = 10, JpegLsNear = 4, H264Mbps = 2.5 };
            Check("All set: valid, with a key and a description", all.Validate().Count == 0 && all.Key == "q75/j2k10/near4/h264-2.5"
                && all.Describe() == "JPEG quality 75, JPEG 2000 ratio 10:1, JPEG-LS NEAR 4, H.264 2.5 Mbit/s", all.Key + " | " + all.Describe());
            Check("Per syntax: each value names its own encoder only",
                all.Describe(DicomTransferSyntax.JPEGProcess1) == "JPEG quality 75" && all.Describe(DicomTransferSyntax.JPEGProcess2_4) == "JPEG quality 75"
                && all.Describe(DicomTransferSyntax.JPEG2000Lossy) == "JPEG 2000 ratio 10:1" && all.Describe(DicomTransferSyntax.JPEGLSNearLossless) == "JPEG-LS NEAR 4"
                && all.Describe(DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41) == "H.264 2.5 Mbit/s"
                && all.Describe(DicomTransferSyntax.HTJ2K) == null && all.Describe(DicomTransferSyntax.JPEGLSLossless) == null && all.Describe(DicomTransferSyntax.ExplicitVRLittleEndian) == null, null);

            Func<LossyCompressionSettings, bool> refused = s => s.Validate().Count == 1;
            Check("Out of range values are refused, one message each",
                refused(new LossyCompressionSettings { JpegQuality = 0 }) && refused(new LossyCompressionSettings { JpegQuality = 101 })
                && refused(new LossyCompressionSettings { Jpeg2000Ratio = 1 }) && refused(new LossyCompressionSettings { Jpeg2000Ratio = 201 })
                && refused(new LossyCompressionSettings { JpegLsNear = 0 }) && refused(new LossyCompressionSettings { JpegLsNear = 256 })
                && refused(new LossyCompressionSettings { H264Mbps = 0.05 }) && refused(new LossyCompressionSettings { H264Mbps = 51 }) && refused(new LossyCompressionSettings { H264Mbps = double.NaN }), null);
            Check("The limits themselves are allowed",
                new LossyCompressionSettings { JpegQuality = 1, Jpeg2000Ratio = 2, JpegLsNear = 1, H264Mbps = 0.1 }.Validate().Count == 0
                && new LossyCompressionSettings { JpegQuality = 100, Jpeg2000Ratio = 200, JpegLsNear = 255, H264Mbps = 50 }.Validate().Count == 0, null);

            var request = Request(ModalityCatalog.Find("CR"), RealismLevel.Off);
            request.Lossy = new LossyCompressionSettings { JpegQuality = 0 };
            Check("A bad value blocks an exam and is named", ExamValidator.Validate(request).Any(e => e.Contains("JPEG quality")), string.Join(" ", ExamValidator.Validate(request)));
            var dataset = new TestDatasetRequest { ProfileId = "cr", Lossy = new LossyCompressionSettings { JpegLsNear = 300 } };
            Check("A bad value blocks a dataset and is named", dataset.Validate().Any(e => e.Contains("NEAR")), string.Join(" ", dataset.Validate()));
            var options = new StoreRunOptions { Send = false, Save = true, SaveFolder = "x", Lossy = new LossyCompressionSettings { H264Mbps = 0 } };
            Check("A bad value blocks a send", options.Validate() != null && options.Validate().Contains("H.264"), options.Validate());
        }

        private static void TestLossyEncoders()
        {
            Console.WriteLine("\nLossy compression settings: the encoders");
            var cr = ModalityCatalog.Find("CR");
            var noisy8 = Request(cr, RealismLevel.Normal, 256, 256, bits: 8);
            var noisy12 = Request(cr, RealismLevel.Normal, 256, 256, bits: 12);

            foreach (var syntax in new[] { DicomTransferSyntax.JPEGProcess1, DicomTransferSyntax.JPEG2000Lossy, DicomTransferSyntax.JPEGLSNearLossless })
            {
                var plain = Saved(TransferSyntaxConverter.Convert(First(noisy8), syntax));
                var withDefaults = Saved(TransferSyntaxConverter.Convert(First(noisy8), syntax, lossy: new LossyCompressionSettings()));
                Check($"{syntax.UID.Name}: settings left at the default give the same file as no settings", plain.SequenceEqual(withDefaults), plain.Length + " vs " + withDefaults.Length);
            }
            var noDerivation = TransferSyntaxConverter.Convert(First(noisy8), DicomTransferSyntax.JPEGProcess1, lossy: new LossyCompressionSettings());
            Check("Default settings write no Derivation Description", Derivation(noDerivation) == null, Derivation(noDerivation));

            var low = TransferSyntaxConverter.Convert(First(noisy8), DicomTransferSyntax.JPEGProcess1, lossy: new LossyCompressionSettings { JpegQuality = 30 });
            var high = TransferSyntaxConverter.Convert(First(noisy8), DicomTransferSyntax.JPEGProcess1, lossy: new LossyCompressionSettings { JpegQuality = 95 });
            Check("JPEG Baseline: quality 30 is smaller than quality 95", Bytes(low) < Bytes(high) * 0.7, Bytes(low) + " vs " + Bytes(high));
            Check("JPEG Baseline: the quality is in Derivation Description", Derivation(low) == TransferSyntaxConverter.DerivationPrefix + "JPEG quality 30", Derivation(low));

            var low12 = TransferSyntaxConverter.Convert(First(noisy12), DicomTransferSyntax.JPEGProcess2_4, lossy: new LossyCompressionSettings { JpegQuality = 30 });
            var high12 = TransferSyntaxConverter.Convert(First(noisy12), DicomTransferSyntax.JPEGProcess2_4, lossy: new LossyCompressionSettings { JpegQuality = 95 });
            Check("JPEG Extended, 12 bits: quality 30 is smaller than quality 95", Bytes(low12) < Bytes(high12) * 0.8, Bytes(low12) + " vs " + Bytes(high12));

            foreach (var ratio in new[] { 5, 20 })
            {
                var j2k = TransferSyntaxConverter.Convert(First(noisy12), DicomTransferSyntax.JPEG2000Lossy, lossy: new LossyCompressionSettings { Jpeg2000Ratio = ratio });
                var recorded = double.Parse(j2k.Dataset.GetSingleValue<string>(DicomTag.LossyImageCompressionRatio), System.Globalization.CultureInfo.InvariantCulture);
                Check($"JPEG 2000 at {ratio}:1: the recorded ratio is within 25 % of it", recorded >= ratio * 0.75 && recorded <= ratio * 1.25, recorded.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Check($"JPEG 2000 at {ratio}:1: Derivation Description says so", Derivation(j2k) == TransferSyntaxConverter.DerivationPrefix + "JPEG 2000 ratio " + ratio + ":1", Derivation(j2k));
            }

            var original = Samples(First(noisy12).Dataset);
            long previous = long.MaxValue;
            foreach (var near in new[] { 2, 8 })
            {
                var jls = TransferSyntaxConverter.Convert(First(noisy12), DicomTransferSyntax.JPEGLSNearLossless, lossy: new LossyCompressionSettings { JpegLsNear = near });
                var decoded = Decoded(jls);
                var most = original.Zip(decoded, (a, b) => Math.Abs(a - b)).Max();
                Check($"JPEG-LS NEAR {near}: no sample is off by more than {near}, and some are off", most <= near && most > 0, most.ToString());
                Check($"JPEG-LS NEAR {near}: smaller than with a lower NEAR", Bytes(jls) < previous, Bytes(jls).ToString());
                previous = Bytes(jls);
            }
            var original8 = Samples(First(noisy8).Dataset);
            var capped = Decoded(TransferSyntaxConverter.Convert(First(noisy8), DicomTransferSyntax.JPEGLSNearLossless, lossy: new LossyCompressionSettings { JpegLsNear = 255 }));
            Check("JPEG-LS NEAR 255 at 8 bits is held to what the bit depth allows, and encodes", original8.Zip(capped, (a, b) => Math.Abs(a - b)).Max() <= 127, null);

            var cine = Named("us-multiframe");
            var h264Choice = CompressionCatalog.Find("h264");
            if (!CompressionCatalog.Probe(h264Choice, 8, 3).Available)
            {
                Console.WriteLine("  SKIP  H.264 bitrate: the encoder of Windows is not available here");
                return;
            }
            var video = Request(cine, RealismLevel.Normal, 128, 160, frames: 6);
            var slow = TransferSyntaxConverter.Convert(First(video), DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41, lossy: new LossyCompressionSettings { H264Mbps = 0.5 });
            var fast = TransferSyntaxConverter.Convert(First(video), DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41, lossy: new LossyCompressionSettings { H264Mbps = 8 });
            Check("H.264: 0.5 Mbit/s is smaller than 8 Mbit/s", Bytes(slow) < Bytes(fast), Bytes(slow) + " vs " + Bytes(fast));
            Check("H.264: the bitrate is in Derivation Description", Derivation(slow) == TransferSyntaxConverter.DerivationPrefix + "H.264 0.5 Mbit/s", Derivation(slow));
        }

        // ---------------------------------------------------------------- realism

        private static void TestRealismDeterminism()
        {
            Console.WriteLine("\nRealistic images: the same request gives the same pixels");
            foreach (var code in new[] { "CT", "MR", "CR", "US", "XA", "MG", "PT" })
            {
                var modality = ModalityCatalog.Find(code);
                var once = Saved(First(Request(modality, RealismLevel.Normal)));
                var twice = Saved(First(Request(modality, RealismLevel.Normal)));
                Check($"{code}: two realistic renders are byte-identical", once.SequenceEqual(twice), once.Length + " vs " + twice.Length);
            }

            var ct = ModalityCatalog.Find("CT");
            var randomA = Request(ct, RealismLevel.Normal);
            var randomB = Request(ct, RealismLevel.Normal);
            randomA.Uids = GeneratedUidSource.Instance;
            randomB.Uids = GeneratedUidSource.Instance;
            Check("Another series has other noise", !Samples(First(randomA).Dataset).SequenceEqual(Samples(First(randomB).Dataset)), null);

            var stack = All(Request(ct, RealismLevel.Normal, images: 2));
            Check("Two images of a series have their own noise", !Samples(stack[0].Dataset).SequenceEqual(Samples(stack[1].Dataset)), null);

            // The frames are drawn again when they are read again: they must come out the same.
            foreach (var id in new[] { "us-multiframe", "xa-cine", "enhanced-ct" })
            {
                var modality = Named(id);
                var request = Request(modality, RealismLevel.Normal, 64, 64, frames: 4);
                var whole = TransferSyntaxConverter.Convert(InMemoryCopy(First(request)), DicomTransferSyntax.JPEGProcess14SV1);
                var oneAtATime = TransferSyntaxConverter.Convert(First(request), DicomTransferSyntax.JPEGProcess14SV1);
                Check($"{id}: frame by frame gives the same file as all frames at once", Saved(whole).SequenceEqual(Saved(oneAtATime)), null);
            }
        }

        private static void TestRealismPixels()
        {
            Console.WriteLine("\nRealistic images: noise, range and overlays");

            foreach (var code in new[] { "CT", "CR", "US" })
            {
                var modality = ModalityCatalog.Find(code);
                var flat = LosslessBytes(Request(modality, RealismLevel.Off, 256, 256));
                var real = LosslessBytes(Request(modality, RealismLevel.Normal, 256, 256));
                Check($"{code}: a realistic image takes at least twice the bytes of the flat one in JPEG-LS lossless", real >= 2 * flat, flat + " vs " + real);
            }

            var cr = ModalityCatalog.Find("CR");
            var sizes = new[] { RealismLevel.Low, RealismLevel.Normal, RealismLevel.High }.Select(l => LosslessBytes(Request(cr, l, 256, 256))).ToList();
            Check("CR: Low < Normal < High in JPEG-LS lossless", sizes[0] < sizes[1] && sizes[1] < sizes[2], string.Join(" < ", sizes));

            foreach (var code in new[] { "CT", "MG", "PT", "MR", "XA" })
            {
                var modality = ModalityCatalog.Find(code);
                foreach (var bits in modality.BitDepths)
                {
                    var request = Request(modality, RealismLevel.High, bits: bits);
                    var samples = Samples(First(request).Dataset);
                    var most = modality.MaxStoredValue(bits);
                    Check($"{code} at {bits} bits, High: every value within 0..{most}", samples.All(v => v >= 0 && v <= most), samples.Max().ToString());
                }
            }
            var mg = Samples(First(Request(ModalityCatalog.Find("MG"), RealismLevel.Normal, 256, 256, bits: 14)).Dataset);
            Check("MG at 14 bits holds far more than 256 values, as the flat pattern cannot", mg.Distinct().Count() > 1000, mg.Distinct().Count().ToString());

            var ctFile = First(Request(ModalityCatalog.Find("CT"), RealismLevel.Normal));
            Check("CT: a realistic image opens in a soft-tissue window", ctFile.Dataset.GetSingleValue<string>(DicomTag.WindowCenter) == "40" && ctFile.Dataset.GetSingleValue<string>(DicomTag.WindowWidth) == "400"
                && ctFile.Dataset.GetSingleValue<string>(DicomTag.WindowCenterWidthExplanation) == "SOFT TISSUE", ctFile.Dataset.GetSingleValue<string>(DicomTag.WindowCenter));
            var flatCt = First(Request(ModalityCatalog.Find("CT"), RealismLevel.Off));
            Check("CT: the flat pattern keeps the full range", flatCt.Dataset.GetSingleValue<string>(DicomTag.WindowCenterWidthExplanation) == "FULL RANGE", null);

            // The overlays are opaque, so what they cover completely is the same whatever is under it. Their anti-aliased
            // edges are half covered, and are left out: the box's first row and column, and where two steps of the bar meet.
            foreach (var code in new[] { "CT", "US" })
            {
                var modality = ModalityCatalog.Find(code);
                var off = Samples(First(Request(modality, RealismLevel.Off, 256, 256, text: true)).Dataset);
                var normal = Samples(First(Request(modality, RealismLevel.Normal, 256, 256, text: true)).Dataset);
                var box = Region(256, 1, 30, 1, 60);
                Check($"{code}: the text box is the same as in the flat pattern", box.All(i => off[i] == normal[i]) && box.Any(i => off[i] != off[box[0]]), Differences(box, off, normal));
                var bar = Region(256, 250, 256, 0, 256).Where(i => i % 256 % 16 != 0).ToList();
                Check($"{code}: the gray bar is the same as in the flat pattern", bar.All(i => off[i] == normal[i]), Differences(bar, off, normal));
            }

            var cineOff = First(Request(Named("xa-cine"), RealismLevel.Off, 128, 128, frames: 3));
            var cineNormal = First(Request(Named("xa-cine"), RealismLevel.Normal, 128, 128, frames: 3));
            // Cells two pixels wide from column 94: the middle column of each is covered completely, in rows 3 to 5.
            var strip = Region(128, 3, 6, 95, 126).Where(i => i % 128 % 2 == 1).ToList();
            Check("XA cine: the frame number strip is the same as in the flat pattern", Enumerable.Range(0, 3).All(f =>
            {
                var a = Samples(cineOff.Dataset, f);
                var b = Samples(cineNormal.Dataset, f);
                return strip.All(i => a[i] == b[i]);
            }), Differences(strip, Samples(cineOff.Dataset), Samples(cineNormal.Dataset)));

            var capture = Named("secondary-capture");
            Check("Secondary Capture: has no realistic form, and is drawn as before", capture.Realism(capture.Presets[0]) == null
                && Saved(First(Request(capture, RealismLevel.Off))).SequenceEqual(Saved(First(Request(capture, RealismLevel.Normal)))), null);

            // A segmentation is a mask: its own UIDs follow the dataset's settings, its pixels do not.
            Func<RealismLevel, byte[]> mask = level =>
            {
                var generator = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "segmentation", Seed = 3, Rows = 64, Columns = 64, Realism = level });
                var target = generator.GetImages(CancellationToken.None).Select(i => i.File).Last(f => f.Dataset.GetString(DicomTag.SOPClassUID) == DicomUID.SegmentationStorage.UID);
                return target.Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            };
            Check("Segmentation: the mask is the same whatever the level", mask(RealismLevel.Off).SequenceEqual(mask(RealismLevel.Normal)), null);
        }

        private static string Differences(List<int> region, int[] a, int[] b)
        {
            var different = region.Where(i => a[i] != b[i]).ToList();
            return different.Count + " of " + region.Count + " differ" + (different.Count > 0 ? $", first at {different[0]}: {a[different[0]]} vs {b[different[0]]}" : string.Empty);
        }

        /// <summary>Pixel indices of rows [top, bottom) and columns [left, right).</summary>
        private static List<int> Region(int width, int top, int bottom, int left, int right)
        {
            var indices = new List<int>();
            for (var y = top; y < bottom; y++)
            {
                for (var x = left; x < right; x++) indices.Add(y * width + x);
            }
            return indices;
        }

        private static void TestRealismShapes()
        {
            Console.WriteLine("\nRealistic images: shapes");
            var us = Samples(First(Request(ModalityCatalog.Find("US"), RealismLevel.Normal, 128, 160)).Dataset);
            Check("US: outside the sector is black", us[3 * 160 + 157] == 0 && us[42 * 160 + 157] == 0 && us[42 * 160 + 2] == 0, null);
            Check("US: inside the sector there is speckle", Region(160, 108, 118, 60, 100).Select(i => us[i]).Distinct().Count() > 20, null);

            var ct = Samples(First(Request(ModalityCatalog.Find("CT"), RealismLevel.Normal, 128, 128)).Dataset);
            var flat = Samples(First(Request(ModalityCatalog.Find("CT"), RealismLevel.Off, 128, 128)).Dataset);
            Check("CT: outside the field of view is the lowest value, without noise", Region(128, 0, 8, 120, 128).All(i => ct[i] == 0) && flat[3 * 128 + 125] > 0, null);
            Check("CT: the air inside the field of view is noisy", Region(128, 60, 68, 2, 6).Select(i => ct[i]).Distinct().Count() > 3, null);

            var mr = Samples(First(Request(ModalityCatalog.Find("MR"), RealismLevel.Normal, 128, 128)).Dataset);
            Check("MR: the air shows a noise floor, never exactly flat", Region(128, 0, 8, 120, 128).Select(i => mr[i]).Distinct().Count() > 3, null);
        }

        private static void TestTemporalCoherence()
        {
            Console.WriteLine("\nRealistic images: frames of a cine loop");
            var us = Coherence(Named("us-multiframe"), 128, 160);
            var xa = Coherence(Named("xa-cine"), 128, 128);
            Check("US cine: the speckle is partly kept from frame to frame", us > 0.25 && us < 0.95, us.ToString("0.00"));
            Check("XA cine: the quantum noise is mostly new in each frame, more so than US speckle", xa < us - 0.1, xa.ToString("0.00"));
        }

        /// <summary>
        /// The correlation between frames 1 and 2 of the change from one pixel to the next, in the middle of the image and
        /// where the flat pattern has no edge: that is the noise, without the shapes and overlays that every frame shares.
        /// </summary>
        private static double Coherence(ModalityProfile modality, int rows, int columns)
        {
            var real = First(Request(modality, RealismLevel.Normal, rows, columns, frames: 3));
            var flat = First(Request(modality, RealismLevel.Off, rows, columns, frames: 3));
            var a = Samples(real.Dataset, 1);
            var b = Samples(real.Dataset, 2);
            var edges1 = Samples(flat.Dataset, 1);
            var edges2 = Samples(flat.Dataset, 2);

            var xs = new List<double>();
            var ys = new List<double>();
            for (var y = rows / 4; y < rows * 3 / 4; y++)
            {
                for (var x = columns / 4; x < columns * 3 / 4; x++)
                {
                    var i = y * columns + x;
                    var still = true;
                    for (var d = -1; d <= 2 && still; d++)
                    {
                        still = edges1[i + d] == edges1[i] && edges2[i + d] == edges2[i] && edges1[i + d * columns] == edges1[i] && edges2[i + d * columns] == edges2[i];
                    }
                    if (!still) continue;
                    xs.Add(a[i + 1] - a[i]);
                    ys.Add(b[i + 1] - b[i]);
                }
            }
            var mx = xs.Average();
            var my = ys.Average();
            var cov = xs.Zip(ys, (p, q) => (p - mx) * (q - my)).Sum();
            var vx = xs.Sum(p => (p - mx) * (p - mx));
            var vy = ys.Sum(q => (q - my) * (q - my));
            return vx > 0 && vy > 0 ? cov / Math.Sqrt(vx * vy) : 1;
        }

        // ---------------------------------------------------------------- test-suite datasets

        private static void TestRealismDatasets(string temp)
        {
            Console.WriteLine("\nRealistic images: test-suite datasets");
            Func<RealismLevel, LossyCompressionSettings, TestDatasetGenerator> generate = (level, lossy) => new TestDatasetGenerator(new TestDatasetRequest
            {
                ProfileId = "us-multiframe",
                Seed = 11,
                Rows = 64,
                Columns = 64,
                Frames = 3,
                Realism = level,
                Lossy = lossy,
            });

            var flat = generate(RealismLevel.Off, null);
            var flatFiles = flat.GetImages(CancellationToken.None).Select(i => i.File).ToList();
            var withDefaults = generate(RealismLevel.Off, new LossyCompressionSettings());
            var defaultFiles = withDefaults.GetImages(CancellationToken.None).Select(i => i.File).ToList();
            Check("Off with default lossy settings: the same UIDs and manifest as without them", flat.Manifest.ToJson() == withDefaults.Manifest.ToJson()
                && flatFiles[0].Dataset.GetString(DicomTag.SOPInstanceUID) == defaultFiles[0].Dataset.GetString(DicomTag.SOPInstanceUID), null);
            Check("Off: the manifest says nothing of realism or lossy settings", !flat.Manifest.ToJson().Contains("\"realism\"") && !flat.Manifest.ToJson().Contains("\"lossyCompression\""), null);

            var real = generate(RealismLevel.Normal, null);
            var realFiles = real.GetImages(CancellationToken.None).Select(i => i.File).ToList();
            Check("Normal: other SOP Instance UIDs than the flat dataset", realFiles[0].Dataset.GetString(DicomTag.SOPInstanceUID) != flatFiles[0].Dataset.GetString(DicomTag.SOPInstanceUID), null);
            Check("Normal: the manifest records the level", real.Manifest.Request.Realism == "Normal" && real.Manifest.ToJson().Contains("\"realism\": \"Normal\""), real.Manifest.Request.Realism);
            var path = Path.Combine(temp, "realism-dataset.dcm");
            realFiles[0].Save(path);
            var read = DicomFile.Open(path);
            Check("Normal: the frames in the file match the manifest's hashes", ManifestHashes.FrameHashes(read.Dataset).SequenceEqual(real.Manifest.Instances[0].FrameSha256), null);

            var again = generate(RealismLevel.Normal, null);
            again.GetImages(CancellationToken.None).ToList();
            Check("Normal: the same seed gives the same manifest", again.Manifest.Instances.Select(i => i.FileSha256).SequenceEqual(real.Manifest.Instances.Select(i => i.FileSha256)), null);

            var jpeg = new TestDatasetGenerator(new TestDatasetRequest
            {
                ProfileId = "cr",
                Seed = 11,
                Rows = 128,
                Columns = 128,
                BitsStored = 8,
                TransferSyntax = DicomTransferSyntax.JPEGProcess1,
                Lossy = new LossyCompressionSettings { JpegQuality = 40 },
            });
            var jpegFile = jpeg.GetImages(CancellationToken.None).First().File;
            Check("A dataset is encoded with its lossy settings, and the manifest records them", Derivation(jpegFile) == TransferSyntaxConverter.DerivationPrefix + "JPEG quality 40"
                && jpeg.Manifest.Request.LossyCompression == "JPEG quality 40", jpeg.Manifest.Request.LossyCompression);
        }
    }
}
