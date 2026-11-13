using DCST4.Core.Generation;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Network;
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
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>
    /// 1.11.0: multi-frame images in Generate &amp; Send, whose frames are drawn and encoded one at a time, JPEG Baseline,
    /// and the private payload.
    /// </summary>
    internal static class MultiFrameTests
    {
        private const int Frames = 5;

        private static Action<string, bool, string> _check;
        private static int _port;

        public static async Task Run(int port, string temp, Action<string, bool, string> check)
        {
            _check = check;
            _port = port;

            TestCatalog();
            TestFramesOnDemand();
            TestXrayObjects();
            TestFrameByFrameEncoding();
            TestProgressCancelAndMemory();
            TestPayload(temp);
            TestRules();
            TestSizeEstimate();
            await TestSending();
            await TestObjectGroups();
        }

        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        /// <summary>The profile that generates an object, by the id of the dataset profile that makes the same one.</summary>
        private static ModalityProfile Named(string datasetProfileId) => ModalityCatalog.FindObject(datasetProfileId)?.Image;

        private static ExamRequest Request(ModalityProfile modality, int frames = Frames, int rows = 64, int columns = 64, int bits = 0, long payload = 0)
        {
            // Fixed UIDs, so that the same request gives the same object, payload included.
            var uids = new DeterministicUidSource(null, "multi-frame-tests/" + modality.DisplayName);
            var identity = new IdentityGenerator(random: new Random(3)).Generate(null, modality, modality.Presets[0]);
            identity.StudyInstanceUid = uids.Uid("study");
            identity.StudyDateTime = new DateTime(2026, 11, 12, 9, 0, 0);
            identity.StudyDateTimeIsAuto = false;
            return new ExamRequest
            {
                Modality = modality,
                Preset = modality.Presets[0],
                Identity = identity,
                Rows = rows,
                Columns = columns,
                BitsStored = bits > 0 ? bits : modality.DefaultBitDepth,
                FramesPerImage = modality.IsMultiFrame ? frames : 1,
                PrivatePayloadBytes = payload,
                Uids = uids,
            };
        }

        private static DicomFile First(ExamRequest request) => new ExamGenerator(request).GetImages(CancellationToken.None).First().File;

        private static IByteBuffer PixelBuffer(DicomDataset dataset) => dataset.GetDicomItem<DicomElement>(DicomTag.PixelData)?.Buffer;

        private static byte[] Saved(DicomFile file)
        {
            using (var stream = new MemoryStream())
            {
                file.Save(stream);
                return stream.ToArray();
            }
        }

        private static DicomFile Reopened(DicomFile file) => DicomFile.Open(new MemoryStream(Saved(file)));

        private static List<byte[]> FramesOf(DicomDataset dataset)
        {
            var pixels = DicomPixelData.Create(dataset);
            var size = (int)pixels.UncompressedFrameSize;
            return Enumerable.Range(0, pixels.NumberOfFrames).Select(f => pixels.GetFrame(f).Data.Take(size).ToArray()).ToList();
        }

        /// <summary>The same object with every frame held in memory, as all multi-frame images were before 1.11.0.</summary>
        private static DicomFile InMemoryCopy(DicomFile file)
        {
            var frames = FramesOf(file.Dataset);
            var copy = new DicomDataset(file.Dataset.InternalTransferSyntax);
            foreach (var item in file.Dataset.Where(i => i.Tag != DicomTag.PixelData)) copy.Add(item);
            var pixels = DicomPixelData.Create(copy, true);
            foreach (var frame in frames) pixels.AddFrame(new MemoryByteBuffer(frame));
            return new DicomFile(copy);
        }

        // ---------------------------------------------------------------- the list in Generate & Send

        private static void TestCatalog()
        {
            Console.WriteLine("\nMulti-frame: the objects of a modality in Generate & Send");
            Check("The Modality list is the ten modalities, unchanged", ModalityCatalog.All.Select(m => m.Code).SequenceEqual(new[] { "CT", "MR", "CR", "DX", "US", "XA", "RF", "MG", "PT", "NM" }), string.Join(",", ModalityCatalog.All.Select(m => m.Code)));

            Func<string, IReadOnlyList<ImageObject>> objectsOf = code => ModalityCatalog.ObjectsOf(ModalityCatalog.Find(code));
            Func<string, string> listOf = code => string.Join(" | ", objectsOf(code).Select(o => o.Name + " (" + o.DatasetProfileId + ")"));
            Check("US: the single-frame image, the cine loop and the volume, in that order", listOf("US") == "Single frame (us) | Multi-frame (cine loop) (us-multiframe) | Enhanced US Volume (enhanced-us-volume)", listOf("US"));
            Check("CT, MR and MG: the single-frame image and the enhanced one", listOf("CT") == "Single frame (ct) | Enhanced CT (enhanced-ct)" && listOf("MR") == "Single frame (mr) | Enhanced MR (enhanced-mr)"
                && listOf("MG") == "Single frame (mg) | Breast Tomosynthesis (tomosynthesis-large)", listOf("CT") + " / " + listOf("MR") + " / " + listOf("MG"));
            Check("XA and RF: the single-frame image, the cine one and the enhanced IOD", listOf("XA") == "Single frame (xa) | Multi-frame (cine run) (xa-cine) | Enhanced XA (enhanced-xa)"
                && listOf("RF") == "Single frame (rf) | Multi-frame (fluoro loop) (rf-cine) | Enhanced XRF (enhanced-rf)", listOf("XA") + " / " + listOf("RF"));
            Check("The other four have one object each", new[] { "CR", "DX", "PT", "NM" }.All(c => objectsOf(c).Count == 1 && !objectsOf(c)[0].IsMultiFrame && objectsOf(c)[0].DatasetProfileId == c.ToLowerInvariant()),
                listOf("CR") + " / " + listOf("NM"));
            Check("The first object of a modality is the single-frame one, and the rest are multi-frame", ModalityCatalog.All.All(m =>
                !ModalityCatalog.ObjectsOf(m)[0].IsMultiFrame && ModalityCatalog.ObjectsOf(m)[0].Name == "Single frame" && ModalityCatalog.ObjectsOf(m).Skip(1).All(o => o.IsMultiFrame)));
            Check("A cine XA or RF is the SOP class of its single-frame image", Named("xa-cine").SopClass == DicomUID.XRayAngiographicImageStorage
                && Named("rf-cine").SopClass == DicomUID.XRayRadiofluoroscopicImageStorage && Named("enhanced-xa").SopClass == DicomUID.EnhancedXAImageStorage
                && Named("enhanced-rf").SopClass == DicomUID.EnhancedXRFImageStorage);
            Check("The US volume is Enhanced US Volume Storage, 8-bit grayscale", Named("enhanced-us-volume").SopClass == DicomUID.EnhancedUSVolumeStorage
                && Named("enhanced-us-volume").SamplesPerPixel == 1 && Named("enhanced-us-volume").BitDepths.SequenceEqual(new[] { 8 }));

            var all = ModalityCatalog.All.SelectMany(ModalityCatalog.ObjectsOf).ToList();
            Check("Nineteen objects in all, named once within their modality", all.Count == 19
                && ModalityCatalog.All.All(m => ModalityCatalog.ObjectsOf(m).Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == ModalityCatalog.ObjectsOf(m).Count),
                string.Join(", ", all.Select(o => o.Name)));
            Check("The same instances every time, so a selection stays selected", ModalityCatalog.ObjectsOf(ModalityCatalog.Find("US")).SequenceEqual(objectsOf("US")));
            Check("Each has a dataset profile of the same SOP class, for the custom mix", all.All(o =>
            {
                var profile = DatasetProfileCatalog.Find(o.DatasetProfileId);
                return profile != null && profile.IsImage && profile.Image.SopClass == o.Image.SopClass;
            }));
            Check("Found by id, within a modality and across all of them", ModalityCatalog.FindObject("us-multiframe").Image.SopClass == DicomUID.UltrasoundMultiFrameImageStorage
                && ModalityCatalog.FindObject(ModalityCatalog.Find("US"), "us-multiframe") != null && ModalityCatalog.FindObject(ModalityCatalog.Find("CT"), "us-multiframe") == null && ModalityCatalog.FindObject("nope") == null);
            Check("The dataset list is unchanged by them", DatasetProfileCatalog.All.Count(p => p.Id == "us" || p.Id == "ct") == 2 && ModalityCatalog.All.Count == 10);
            Check("Default frames: 30 for a cine loop or run, 16 for a fluoro loop, 8 for the enhanced images, 1 otherwise",
                Named("us-multiframe").DefaultFrames == 30 && Named("xa-cine").DefaultFrames == 30 && Named("rf-cine").DefaultFrames == 16
                && Named("enhanced-ct").DefaultFrames == 8 && Named("enhanced-mr").DefaultFrames == 8 && Named("tomosynthesis-large").DefaultFrames == 8
                && Named("enhanced-xa").DefaultFrames == 8 && Named("enhanced-rf").DefaultFrames == 8 && Named("enhanced-us-volume").DefaultFrames == 8 && Named("ct").DefaultFrames == 1);
            Check("A modality's objects keep its exam types, so the selection survives the change", new[] { "xa-cine", "rf-cine", "us-multiframe" }.All(id =>
            {
                var image = ModalityCatalog.FindObject(id).Image;
                var modality = ModalityCatalog.Find(image.Code);
                return image.Presets.Select(p => p.Name).SequenceEqual(modality.Presets.Select(p => p.Name));
            }));
        }

        // ---------------------------------------------------------------- frames on demand

        private static void TestFramesOnDemand()
        {
            Console.WriteLine("\nMulti-frame: frames drawn as they are written");
            foreach (var id in new[] { "us-multiframe", "enhanced-ct", "enhanced-mr", "tomosynthesis-large" })
            {
                var modality = Named(id);
                var name = modality.ObjectName;
                var file = First(Request(modality));
                var dataset = file.Dataset;
                var buffer = PixelBuffer(dataset);
                var bytesPerSample = modality.DefaultBitDepth > 8 ? 2 : 1;

                Check($"{name}: {Frames} frames, and its own SOP class", dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == Frames && dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == modality.SopClass);
                Check($"{name}: the frames are not held in memory", buffer != null && !buffer.IsMemory && buffer.Size == 64L * 64 * modality.SamplesPerPixel * bytesPerSample * Frames, buffer?.Size.ToString());
                Check($"{name}: Pixel Data is OB for 8 bits and OW for 16", dataset.GetDicomItem<DicomItem>(DicomTag.PixelData).ValueRepresentation == (bytesPerSample == 1 ? DicomVR.OB : DicomVR.OW));

                var original = FramesOf(dataset);
                var back = FramesOf(Reopened(file).Dataset);
                Check($"{name}: a saved file reads back with the same frames", back.Count == Frames && Enumerable.Range(0, Frames).All(f => back[f].SequenceEqual(original[f])));
                Check($"{name}: the frames differ from each other", original.Select(f => Convert.ToBase64String(f)).Distinct().Count() == Frames);

                var implicitVr = TransferSyntaxConverter.Convert(First(Request(modality)), DicomTransferSyntax.ImplicitVRLittleEndian);
                Check($"{name}: Implicit VR holds the same frames", FramesOf(Reopened(implicitVr).Dataset).Select((f, i) => f.SequenceEqual(original[i])).All(same => same));
            }

            var cine = First(Request(Named("us-multiframe"))).Dataset;
            Check("Cine loop: RGB, Frame Time and the frame rate", cine.GetString(DicomTag.PhotometricInterpretation) == "RGB" && cine.GetSingleValue<ushort>(DicomTag.SamplesPerPixel) == 3
                && cine.GetString(DicomTag.FrameTime) == "33" && cine.GetString(DicomTag.CineRate) == "30" && cine.GetString(DicomTag.RecommendedDisplayFrameRate) == "30"
                && cine.GetSingleValue<DicomTag>(DicomTag.FrameIncrementPointer) == DicomTag.FrameTime);

            // 65 x 65 x 3 x 5 bytes is an odd number, and Pixel Data must have even length.
            var odd = First(Request(Named("us-multiframe"), rows: 65, columns: 65));
            var oddBuffer = PixelBuffer(odd.Dataset);
            var oddBack = Reopened(odd);
            Check("An odd number of bytes is padded to an even one", oddBuffer.Size == 65 * 65 * 3 * Frames + 1 && oddBuffer.Data[oddBuffer.Size - 1] == 0, oddBuffer.Size.ToString());
            Check("... and the padded file reads back with the same frames", FramesOf(oddBack.Dataset).Select((f, i) => f.SequenceEqual(FramesOf(odd.Dataset)[i])).All(same => same));
        }

        // ---------------------------------------------------------------- the XA and RF objects

        private static void TestXrayObjects()
        {
            Console.WriteLine("\nMulti-frame: XA and RF cine, and the Enhanced XA and XRF IODs");

            // A cine run and a fluoro loop are the single-frame SOP class with the Multi-frame and Cine modules added.
            var cineRun = First(Request(Named("xa-cine"))).Dataset;
            var fluoro = First(Request(Named("rf-cine"))).Dataset;
            var single = First(Request(Named("xa"))).Dataset;

            Check("Cine run: the SOP class of the single-frame XA image, with Number of Frames",
                cineRun.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == single.GetSingleValue<DicomUID>(DicomTag.SOPClassUID)
                && cineRun.GetSingleValue<int>(DicomTag.NumberOfFrames) == Frames && !single.Contains(DicomTag.NumberOfFrames));
            Check("Cine run: Frame Time, Frame Increment Pointer and a whole-number frame rate, 15 per second",
                cineRun.GetString(DicomTag.FrameTime) == "66.666667" && cineRun.GetSingleValue<DicomTag>(DicomTag.FrameIncrementPointer) == DicomTag.FrameTime
                && cineRun.GetString(DicomTag.CineRate) == "15" && cineRun.GetString(DicomTag.RecommendedDisplayFrameRate) == "15"
                && cineRun.GetSingleValue<ushort>(DicomTag.PreferredPlaybackSequencing) == 0, cineRun.GetString(DicomTag.FrameTime));
            Check("Cine run: Positioner Motion, which the XA Positioner module requires above one frame",
                cineRun.GetString(DicomTag.PositionerMotion) == "STATIC" && !single.Contains(DicomTag.PositionerMotion));
            Check("Fluoro loop: the RF SOP class at 8 frames per second", fluoro.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == DicomUID.XRayRadiofluoroscopicImageStorage
                && fluoro.GetString(DicomTag.FrameTime) == "125" && fluoro.GetString(DicomTag.CineRate) == "8" && fluoro.GetSingleValue<int>(DicomTag.NumberOfFrames) == Frames);
            Check("Both keep what the single-frame image has: the X-Ray Image module and the exposure",
                cineRun.GetString(DicomTag.PixelIntensityRelationship) == single.GetString(DicomTag.PixelIntensityRelationship)
                && cineRun.GetString(DicomTag.RadiationSetting) == single.GetString(DicomTag.RadiationSetting) && fluoro.Contains(DicomTag.Exposure));

            foreach (var id in new[] { "enhanced-xa", "enhanced-rf" })
            {
                var image = Named(id);
                var name = image.DisplayName;
                var file = First(Request(image));
                var dataset = file.Dataset;
                var shared = dataset.GetSequence(DicomTag.SharedFunctionalGroupsSequence).Items[0];
                var perFrame = dataset.GetSequence(DicomTag.PerFrameFunctionalGroupsSequence);

                Check($"{name}: its own SOP class, the right modality and {Frames} frames in functional groups",
                    dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == image.SopClass && dataset.GetString(DicomTag.Modality) == image.Code
                    && dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == Frames && perFrame.Items.Count == Frames);
                Check($"{name}: Image Type is DERIVED, with NONE as value 4", dataset.GetValues<string>(DicomTag.ImageType).SequenceEqual(new[] { "DERIVED", "PRIMARY", id == "enhanced-xa" ? "ANGIO" : "FLUOROSCOPY", "NONE" }),
                    string.Join("\\", dataset.GetValues<string>(DicomTag.ImageType)));
                Check($"{name}: one plane, named, and the rest of the Enhanced XA/XRF Image module",
                    dataset.GetString(DicomTag.PlanesInAcquisition) == "SINGLE PLANE" && dataset.GetString(DicomTag.PlaneIdentification) == "MONOPLANE"
                    && dataset.GetString(DicomTag.ContentQualification) == "RESEARCH" && dataset.GetString(DicomTag.PresentationLUTShape) == "IDENTITY"
                    && dataset.GetString(DicomTag.LossyImageCompression) == "00" && dataset.Contains(DicomTag.AcquisitionDateTime)
                    && dataset.GetString(DicomTag.PhotometricInterpretation) == "MONOCHROME2");
                Check($"{name}: an Acquisition Context Sequence, which the IOD requires, with no items",
                    dataset.GetSequence(DicomTag.AcquisitionContextSequence).Items.Count == 0);

                var properties = shared.GetSequence(DicomTag.FramePixelDataPropertiesSequence).Items[0];
                Check($"{name}: X-Ray Frame Pixel Data Properties, with LIN so that no intensity LUT is needed",
                    properties.GetValues<string>(DicomTag.FrameType).SequenceEqual(dataset.GetValues<string>(DicomTag.ImageType))
                    && properties.GetString(DicomTag.PixelIntensityRelationship) == "LIN" && properties.GetSingleValue<short>(DicomTag.PixelIntensityRelationshipSign) == 1
                    && properties.GetString(DicomTag.GeometricalProperties) == "UNIFORM" && properties.GetString(DicomTag.ImageProcessingApplied) == "NONE");

                var anatomy = shared.GetSequence(DicomTag.FrameAnatomySequence).Items[0];
                var region = anatomy.GetSequence(DicomTag.AnatomicRegionSequence).Items[0];
                Check($"{name}: Frame Anatomy with a SNOMED CT code of CID 4042 and an unpaired laterality",
                    anatomy.GetString(DicomTag.FrameLaterality) == "U" && region.GetString(DicomTag.CodingSchemeDesignator) == "SCT"
                    && region.GetString(DicomTag.CodeValue) == (id == "enhanced-xa" ? "41801008" : "69695003"), region.GetString(DicomTag.CodeMeaning));
                Check($"{name}: Frame VOI LUT in the shared groups, where this IOD has no window of its own",
                    shared.GetSequence(DicomTag.FrameVOILUTSequence).Items[0].Contains(DicomTag.WindowCenter) && !dataset.Contains(DicomTag.WindowCenter));

                var eventUid = shared.GetSequence(DicomTag.IrradiationEventIdentificationSequence).Items[0].GetString(DicomTag.IrradiationEventUID);
                var again = First(Request(image)).Dataset.GetSequence(DicomTag.SharedFunctionalGroupsSequence).Items[0]
                    .GetSequence(DicomTag.IrradiationEventIdentificationSequence).Items[0].GetString(DicomTag.IrradiationEventUID);
                Check($"{name}: an Irradiation Event UID that follows from the UID root, the same one every time",
                    ExamValidator.IsValidUid(eventUid) && eventUid.StartsWith("2.25.", StringComparison.Ordinal) && eventUid == again, eventUid);

                Check($"{name}: the frames are drawn as they are written", PixelBuffer(dataset) != null && !PixelBuffer(dataset).IsMemory);
                Check($"{name}: each frame has a Frame Content item and nothing that belongs to a stack",
                    perFrame.Items.Select((item, i) => item.Count() == 1 && item.GetSequence(DicomTag.FrameContentSequence).Items[0].GetString(DicomTag.FrameLabel) == "Frame " + (i + 1)
                        && !item.Contains(DicomTag.PlanePositionSequence)).All(ok => ok));
                Check($"{name}: no Frame of Reference, slice geometry or Dimension Organization, none of which the IOD has",
                    !dataset.Contains(DicomTag.FrameOfReferenceUID) && !dataset.Contains(DicomTag.DimensionOrganizationSequence) && !dataset.Contains(DicomTag.DimensionIndexSequence)
                    && !shared.Contains(DicomTag.PixelMeasuresSequence) && !shared.Contains(DicomTag.PlaneOrientationSequence)
                    && !perFrame.Items[0].GetSequence(DicomTag.FrameContentSequence).Items[0].Contains(DicomTag.StackID));
            }

            // The same skeleton still carries the stack of an enhanced CT, which the two new IODs switch off.
            var enhancedCt = First(Request(Named("enhanced-ct"))).Dataset;
            var ctShared = enhancedCt.GetSequence(DicomTag.SharedFunctionalGroupsSequence).Items[0];
            Check("Enhanced CT keeps its slice geometry and dimensions", ctShared.Contains(DicomTag.PixelMeasuresSequence) && ctShared.Contains(DicomTag.PlaneOrientationSequence)
                && enhancedCt.Contains(DicomTag.DimensionOrganizationSequence) && enhancedCt.Contains(DicomTag.FrameOfReferenceUID)
                && enhancedCt.GetSequence(DicomTag.PerFrameFunctionalGroupsSequence).Items[0].GetSequence(DicomTag.FrameContentSequence).Items[0].Contains(DicomTag.StackID));
        }

        // ---------------------------------------------------------------- encoding one frame at a time

        private static void TestFrameByFrameEncoding()
        {
            Console.WriteLine("\nMulti-frame: frames encoded one at a time");
            var cases = new[]
            {
                Tuple.Create("us-multiframe", "jpeg-baseline"),
                Tuple.Create("us-multiframe", "jpeg-lossless"),
                Tuple.Create("us-multiframe", "rle"),
                Tuple.Create("enhanced-ct", "jpeg-lossless"),
                Tuple.Create("enhanced-ct", "jpeg-ls"),
                Tuple.Create("enhanced-mr", "jpeg-lossless"),
                Tuple.Create("tomosynthesis-large", "jpeg-lossless"),
                Tuple.Create("tomosynthesis-large", "jpeg2000"),
                Tuple.Create("xa-cine", "jpeg-lossless"),
                Tuple.Create("enhanced-xa", "rle"),
            };

            foreach (var item in cases)
            {
                var modality = Named(item.Item1);
                var choice = CompressionCatalog.Find(item.Item2);
                var label = $"{modality.ObjectName} in {item.Item2}";
                if (!CompressionCatalog.Probe(choice, modality.DefaultBitDepth, modality.SamplesPerPixel).Available)
                {
                    Console.WriteLine($"  SKIP  {label}: the encoder is not available on this computer");
                    continue;
                }

                var source = First(Request(modality));
                var original = FramesOf(source.Dataset);
                var whole = TransferSyntaxConverter.Convert(InMemoryCopy(First(Request(modality))), choice.Syntax);
                var oneAtATime = TransferSyntaxConverter.Convert(source, choice.Syntax);

                var fragments = oneAtATime.Dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) as DicomFragmentSequence;
                Check($"{label}: one fragment per frame, in OB, with an offset table", fragments != null && fragments.Fragments.Count == Frames && fragments.ValueRepresentation == DicomVR.OB
                    && fragments.OffsetTable.Count == Frames && fragments.Fragments.All(f => f.Size % 2 == 0), fragments?.Fragments.Count.ToString());
                Check($"{label}: byte for byte the file that encoding the whole image gives", Saved(oneAtATime).SequenceEqual(Saved(whole)), $"{Saved(oneAtATime).Length} and {Saved(whole).Length} bytes");

                var decoded = FramesOf(Reopened(oneAtATime).Clone(DicomTransferSyntax.ExplicitVRLittleEndian).Dataset);
                if (choice.Syntax.IsLossy)
                {
                    var worst = Enumerable.Range(0, Frames).Max(f => original[f].Zip(decoded[f], (a, b) => Math.Abs(a - b)).Average());
                    Check($"{label}: decodes to nearly the same frames", decoded.Count == Frames && worst < 6, worst.ToString("0.00"));
                    Check($"{label}: marked as lossy, with a ratio, and DERIVED", oneAtATime.Dataset.GetString(DicomTag.LossyImageCompression) == "01" && oneAtATime.Dataset.GetSingleValue<double>(DicomTag.LossyImageCompressionRatio) > 1
                        && oneAtATime.Dataset.GetValues<string>(DicomTag.ImageType)[0] == "DERIVED", oneAtATime.Dataset.GetString(DicomTag.LossyImageCompressionRatio));
                }
                else
                {
                    Check($"{label}: decodes to exactly the same frames", decoded.Count == Frames && Enumerable.Range(0, Frames).All(f => decoded[f].SequenceEqual(original[f])));
                }
            }

            var dataset = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "tomosynthesis-large", Rows = 64, Columns = 64, Frames = 3, TransferSyntax = DicomTransferSyntax.JPEGProcess14SV1 });
            var images = dataset.GetImages(CancellationToken.None).ToList();
            Check("The tomosynthesis dataset is generated in JPEG Lossless, with a hash for each frame", images.Count == 1 && images[0].File.Dataset.InternalTransferSyntax == DicomTransferSyntax.JPEGProcess14SV1
                && dataset.Manifest.Instances.Single().FrameSha256.Count == 3);
        }

        private static void TestProgressCancelAndMemory()
        {
            Console.WriteLine("\nMulti-frame: progress, cancelling and memory");
            var cine = Named("us-multiframe");

            var seen = new List<string>();
            TransferSyntaxConverter.Convert(First(Request(cine)), DicomTransferSyntax.JPEGProcess1, CancellationToken.None, (done, total) => seen.Add(done + "/" + total));
            Check("Each encoded frame is reported", seen.SequenceEqual(new[] { "1/5", "2/5", "3/5", "4/5", "5/5" }), string.Join(" ", seen));

            var none = new List<string>();
            TransferSyntaxConverter.Convert(First(Request(Named("ct"))), DicomTransferSyntax.JPEGProcess14SV1, CancellationToken.None, (done, total) => none.Add(done + "/" + total));
            TransferSyntaxConverter.Convert(First(Request(cine)), DicomTransferSyntax.ImplicitVRLittleEndian, CancellationToken.None, (done, total) => none.Add(done + "/" + total));
            Check("A single-frame image and an uncompressed one report nothing", none.Count == 0, string.Join(" ", none));

            using (var cts = new CancellationTokenSource())
            {
                var cancelled = false;
                var frames = 0;
                try
                {
                    TransferSyntaxConverter.Convert(First(Request(cine, frames: 50)), DicomTransferSyntax.JPEGProcess1, cts.Token, (done, total) => { frames = done; if (done == 3) cts.Cancel(); });
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                Check("Cancelling stops the encoding at the next frame", cancelled && frames == 3, frames.ToString());
            }

            // 300 frames of 256 x 256 RGB are 59 MB as drawn. Encoded, the test images are a small part of that.
            const int many = 300;
            var raw = 256L * 256 * 3 * many;
            GC.Collect();
            var before = GC.GetTotalMemory(true);
            var peak = 0L;
            var encoded = TransferSyntaxConverter.Convert(First(Request(cine, frames: many, rows: 256, columns: 256)), DicomTransferSyntax.JPEGProcess1, CancellationToken.None,
                (done, total) => { if (done % 100 == 0) peak = Math.Max(peak, GC.GetTotalMemory(true) - before); });
            var kept = GC.GetTotalMemory(true) - before;
            var compressed = ((DicomFragmentSequence)encoded.Dataset.GetDicomItem<DicomItem>(DicomTag.PixelData)).Fragments.Sum(f => f.Size);
            Check($"{many} frames: all are there", encoded.Dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == many);
            Check($"{many} frames: what is kept is the encoded frames, far less than the frames as drawn", kept < compressed + 4 * 1024 * 1024 && kept < raw / 4, $"kept {kept / 1024} KB, encoded {compressed / 1024} KB, drawn {raw / 1024} KB");
            Check($"{many} frames: what was in use on the way was never more than a part of the frames as drawn", peak < raw / 4, $"peak {peak / 1024} KB of {raw / 1024} KB");
            GC.KeepAlive(encoded);
        }

        // ---------------------------------------------------------------- private payload

        private const long PayloadBytes = 1024 * 1024 + 1;
        private const long PayloadWritten = PayloadBytes + 1;

        private static IByteBuffer PayloadBuffer(DicomDataset dataset) =>
            dataset.TryGetSequence(PrivatePayload.SequenceTag, out var sequence) && sequence.Items.Count == 1 ? sequence.Items[0].GetDicomItem<DicomElement>(PrivatePayload.BytesTag)?.Buffer : null;

        /// <summary>
        /// Whether these bytes are somewhere in the object outside Pixel Data, whatever a receiver made of the private tags around
        /// them: with Implicit VR it does not know their VR, and may keep the whole sequence as one value.
        /// </summary>
        private static bool Carries(DicomDataset dataset, byte[] payload)
        {
            foreach (var item in dataset.Where(i => i.Tag != DicomTag.PixelData))
            {
                if (item is DicomSequence sequence)
                {
                    if (sequence.Items.Any(child => Carries(child, payload))) return true;
                }
                else if (item is DicomElement element && element.Buffer.Size >= payload.Length && Contains(element.Buffer.Data, payload))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) continue;
                var j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static void TestPayload(string temp)
        {
            Console.WriteLine("\nPrivate payload");
            var ct = Named("ct");
            var plain = First(Request(ct)).Dataset;
            Check("None by default: no private group in the object", !plain.Any(i => i.Tag.Group == 0x0099) && PrivatePayload.BytesIn(plain) == 0);

            var file = First(Request(ct, payload: PayloadBytes));
            var dataset = file.Dataset;
            var buffer = PayloadBuffer(dataset);
            Check("A private sequence with one item: creator, label and the bytes", buffer != null && dataset.GetSequence(PrivatePayload.SequenceTag).Items[0].Count() == 3
                && dataset.GetString(new DicomTag(0x0099, 0x0010)) == PrivatePayload.PrivateCreator && dataset.GetSequence(PrivatePayload.SequenceTag).Items[0].GetString(new DicomTag(0x0099, 0x0010)) == PrivatePayload.PrivateCreator);
            Check("The size asked for, made even, in OB, and not held in memory", buffer != null && buffer.Size == PayloadWritten && !buffer.IsMemory && PrivatePayload.BytesIn(dataset) == PayloadWritten
                && dataset.GetSequence(PrivatePayload.SequenceTag).Items[0].GetDicomItem<DicomItem>(PrivatePayload.BytesTag).ValueRepresentation == DicomVR.OB, buffer?.Size.ToString());
            Check("It comes before Pixel Data, and the image is otherwise the same", dataset.Select(i => i.Tag).ToList().IndexOf(DicomTag.PixelData) == dataset.Count() - 1
                && FramesOf(dataset)[0].SequenceEqual(FramesOf(plain)[0]) && dataset.Count() == plain.Count() + 2);

            var bytes = buffer.Data;
            var part = new byte[1000];
            buffer.GetByteRange(12345, part.Length, part);
            Check("Any part can be read on its own, and reads the same again", part.SequenceEqual(bytes.Skip(12345).Take(1000)) && buffer.Data.SequenceEqual(bytes));
            Check("The bytes look random: every value occurs, and no long run of one", bytes.Distinct().Count() == 256 && bytes.Take(4096).GroupBy(b => b).Max(g => g.Count()) < 64);
            Check("The same object gets the same payload again", PayloadBuffer(First(Request(ct, payload: PayloadBytes)).Dataset).Data.SequenceEqual(bytes));

            var two = Request(ct, payload: 4096);
            two.ImagesPerSeries = 2;
            var pair = new ExamGenerator(two).GetImages(CancellationToken.None).Select(i => PayloadBuffer(i.File.Dataset).Data).ToList();
            Check("Two images carry different bytes", !pair[0].SequenceEqual(pair[1]));

            var path = Path.Combine(temp, "payload.dcm");
            file.Save(path);
            var reopened = DicomFile.Open(path, FileReadOption.ReadAll);
            Check("A saved file reads back with the same payload", PayloadBuffer(reopened.Dataset)?.Data.SequenceEqual(bytes) == true && FramesOf(reopened.Dataset)[0].SequenceEqual(FramesOf(plain)[0]));
            var growth = new FileInfo(path).Length - Saved(First(Request(ct))).Length;
            Check("The file grows by the payload and the few tags around it", growth > PayloadWritten && growth < PayloadWritten + 200, growth.ToString());

            var jpeg = TransferSyntaxConverter.Convert(First(Request(ct, payload: PayloadBytes)), DicomTransferSyntax.JPEGProcess14SV1);
            Check("Compressing the image leaves the payload as it is, still not in memory", PayloadBuffer(jpeg.Dataset)?.IsMemory == false && PayloadBuffer(jpeg.Dataset).Size == PayloadWritten);
            var cine = TransferSyntaxConverter.Convert(First(Request(Named("us-multiframe"), payload: PayloadBytes)), DicomTransferSyntax.JPEGProcess1);
            Check("... also when the frames are encoded one at a time", PayloadBuffer(cine.Dataset)?.IsMemory == false && PayloadBuffer(cine.Dataset).Size == PayloadWritten
                && PayloadBuffer(Reopened(cine).Dataset).Data.Length == PayloadWritten);
        }

        // ---------------------------------------------------------------- rules

        private static void TestRules()
        {
            Console.WriteLine("\nMulti-frame and payload: what is refused");
            var cine = Named("us-multiframe");
            Func<ExamRequest, string> validate = r => string.Join(" ", ExamValidator.Validate(r));
            Func<ExamRequest, DicomTransferSyntax, string> syntax = (r, s) => string.Join(" ", ExamValidator.SyntaxProblems(r, s));

            Check("10000 frames are allowed, 10001 are not", validate(Request(cine, frames: 10000)).Length == 0 && validate(Request(cine, frames: 10001)).Contains("at most 10000"), validate(Request(cine, frames: 10001)));
            var single = Request(Named("ct"));
            single.FramesPerImage = 2;
            Check("Frames on a single-frame modality are refused", validate(single).Contains("one frame"), validate(single));
            Check("A payload of 2000 MB is allowed, a larger one is not", validate(Request(cine, payload: PrivatePayload.MaxBytes)).Length == 0 && validate(Request(cine, payload: PrivatePayload.MaxBytes + 1)).Contains("at most 2000 MB"),
                validate(Request(cine, payload: PrivatePayload.MaxBytes + 1)));

            var canon = Request(cine, frames: 1667, rows: 960, columns: 1280);
            Check("1667 frames of 960 x 1280 uncompressed: refused, with the size and what to do", syntax(canon, DicomTransferSyntax.ExplicitVRLittleEndian).Contains("5.7 GB") && syntax(canon, DicomTransferSyntax.ExplicitVRLittleEndian).Contains("compressed transfer syntax"),
                syntax(canon, DicomTransferSyntax.ExplicitVRLittleEndian));
            Check("The same loop in JPEG Baseline: fine", syntax(canon, DicomTransferSyntax.JPEGProcess1).Length == 0, syntax(canon, DicomTransferSyntax.JPEGProcess1));
            Check("JPEG Baseline with 12 or 16 bits: refused, and it says 8 bits", syntax(Request(Named("ct"), bits: 12), DicomTransferSyntax.JPEGProcess1).Contains("at most 8 bits")
                && syntax(Request(Named("enhanced-ct")), DicomTransferSyntax.JPEGProcess1).Contains("at most 8 bits"), syntax(Request(Named("ct"), bits: 12), DicomTransferSyntax.JPEGProcess1));
            Check("JPEG Baseline with 8 bits, grayscale or colour: fine", syntax(Request(Named("us")), DicomTransferSyntax.JPEGProcess1).Length == 0 && syntax(Request(Named("cr"), bits: 8), DicomTransferSyntax.JPEGProcess1).Length == 0);
            Check("The uncompressed syntaxes and JPEG Lossless: fine for CT", new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1 }.All(s => syntax(Request(Named("ct")), s).Length == 0));
            Check("Generate & Send offers JPEG Baseline first in the JPEG family", TransferSyntaxOption.All.SkipWhile(o => o.Family != TransferSyntaxFamily.Jpeg).First() == TransferSyntaxOption.JpegBaseline);
        }

        // ---------------------------------------------------------------- size estimate

        private static void TestSizeEstimate()
        {
            Console.WriteLine("\nMulti-frame and payload: size estimate");
            var cine = Named("us-multiframe");
            var without = ExamSizeEstimator.UncompressedImageBytes(Request(cine)).Value;
            var with = ExamSizeEstimator.UncompressedImageBytes(Request(cine, payload: PayloadBytes)).Value;
            var actual = Saved(First(Request(cine, payload: PayloadBytes))).Length - Saved(First(Request(cine))).Length;
            Check("Uncompressed: frames times the frame size", without == ExamSizeEstimator.HeaderBytes + 64 * 64 * 3 * Frames, without.ToString());
            Check("The payload adds what it adds to the file, within a few bytes", Math.Abs(with - without - actual) <= 16, $"{with - without} estimated, {actual} in the file");

            var forty = Request(cine, frames: 40);
            var measured = ExamSizeEstimator.MeasureImageBytes(forty, DicomTransferSyntax.JPEGProcess1, CancellationToken.None);
            var encoded = Saved(TransferSyntaxConverter.Convert(First(forty), DicomTransferSyntax.JPEGProcess1)).Length;
            Check("JPEG Baseline, 40 frames: measured on a few frames, within 15 % of the encoded file", Math.Abs(measured - encoded) < encoded * 0.15, $"{measured} estimated, {encoded} encoded");

            var payload = Request(cine, frames: 40, payload: PayloadBytes);
            Check("... and the payload is added to that", ExamSizeEstimator.MeasureImageBytes(payload, DicomTransferSyntax.JPEGProcess1, CancellationToken.None) - measured == with - without);
            Check("Four frames or fewer are measured as they are", ExamSizeEstimator.MeasureImageBytes(Request(cine, frames: 4), DicomTransferSyntax.JPEGProcess1, CancellationToken.None)
                == Saved(TransferSyntaxConverter.Convert(First(Request(cine, frames: 4)), DicomTransferSyntax.JPEGProcess1)).Length);
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

        private static async Task TestSending()
        {
            Console.WriteLine("\nMulti-frame and payload: sending");
            var cine = Named("us-multiframe");

            Scp.Reset();
            Scp.AcceptedSyntaxes = Scp.AcceptedSyntaxes.Concat(new[] { DicomTransferSyntax.JPEGProcess1 }).ToArray();
            var request = Request(cine, payload: PayloadBytes);
            request.ImagesPerSeries = 2;
            var expected = new ExamGenerator(request).GetImages(CancellationToken.None).Select(i => PayloadBuffer(i.File.Dataset).Data).ToList();
            var progress = new List<string>();
            var run = new StoreRun(new ExamGenerator(request), SendOptions(DicomTransferSyntax.JPEGProcess1));
            run.FrameEncoded += (image, done, total) => { lock (progress) progress.Add(image + ":" + done + "/" + total); };
            var summary = await run.ExecuteAsync(CancellationToken.None);
            var received = Scp.Received.ToList();

            Check("Two cine loops in JPEG Baseline: both stored", summary.Success == 2 && summary.FatalError == null, summary + " / " + summary.FatalError);
            Check("They arrive in JPEG Baseline with all their frames", received.Count == 2 && received.All(r => r.Syntax == DicomTransferSyntax.JPEGProcess1 && r.Dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == Frames
                && r.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == DicomUID.UltrasoundMultiFrameImageStorage), string.Join(",", received.Select(r => r.Syntax.UID.Name)));
            Check("Each arrives with its own payload, byte for byte", received.Count == 2 && Enumerable.Range(0, 2).All(i => Carries(received[i].Dataset, expected[i])));
            Check("The encoding of each image is reported, frame by frame", progress.Count == 2 * Frames && progress.First() == "1:1/5" && progress.Last() == "2:5/5", string.Join(" ", progress));

            // A receiver that only takes Implicit VR sees private tags without their VR.
            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
            var ct = Request(Named("ct"), payload: PayloadBytes);
            var ctPayload = PayloadBuffer(First(ct).Dataset).Data;
            var fallback = await new StoreRun(new ExamGenerator(ct), SendOptions(DicomTransferSyntax.ExplicitVRLittleEndian, fallback: true)).ExecuteAsync(CancellationToken.None);
            var implicitVr = Scp.Received.ToList();
            Check("With fallback to Implicit VR the payload still arrives whole", fallback.Success == 1 && implicitVr.Count == 1 && implicitVr[0].Syntax == DicomTransferSyntax.ImplicitVRLittleEndian
                && Carries(implicitVr[0].Dataset, ctPayload), fallback + " / " + fallback.FatalError);

            Scp.Reset();
            var enhanced = await new StoreRun(new ExamGenerator(Request(Named("enhanced-ct"))), SendOptions(DicomTransferSyntax.ExplicitVRLittleEndian)).ExecuteAsync(CancellationToken.None);
            var tomo = await new StoreRun(new ExamGenerator(Request(Named("tomosynthesis-large"))), SendOptions(DicomTransferSyntax.JPEGProcess14SV1)).ExecuteAsync(CancellationToken.None);
            var both = Scp.Received.ToList();
            Check("Enhanced CT uncompressed and Breast Tomosynthesis in JPEG Lossless are stored", enhanced.Success == 1 && tomo.Success == 1 && both.Count == 2
                && both.All(r => r.Dataset.GetSingleValue<int>(DicomTag.NumberOfFrames) == Frames) && both[1].Syntax == DicomTransferSyntax.JPEGProcess14SV1, enhanced + " / " + tomo);
            Scp.Reset();
        }

        // ---------------------------------------------------------------- OT, DOC and SR: Secondary Capture and objects without pixels

        private static async Task TestObjectGroups()
        {
            Console.WriteLine("\nOT, DOC and SR: Secondary Capture, Encapsulated PDF, Basic Text SR and Enhanced SR in Generate & Send");
            var groups = new[] { "OT", "DOC", "SR" }.Select(ModalityCatalog.Find).ToList();
            Check("OT, DOC and SR come after the ten in the Modality list, named by their codes, and are not imaging modalities",
                groups.All(g => g != null && g.DisplayName == g.Code) && ModalityCatalog.Choices.Count == 13 && ModalityCatalog.Choices.Skip(10).SequenceEqual(groups)
                && ModalityCatalog.All.Count == 10 && !groups.Any(ModalityCatalog.All.Contains), string.Join(",", ModalityCatalog.Choices));
            Func<int, string> listOf = i => string.Join(" | ", ModalityCatalog.ObjectsOf(groups[i]).Select(o => o.Name + " (" + o.DatasetProfileId + ")"));
            Check("OT is Secondary Capture, DOC the PDF, SR the two reports", listOf(0) == "Secondary Capture (secondary-capture)" && listOf(1) == "Encapsulated PDF (encapsulated-pdf)"
                && listOf(2) == "Basic Text SR (basic-text-sr) | Enhanced SR (enhanced-sr)", listOf(0) + " / " + listOf(1) + " / " + listOf(2));
            Check("Only OT has pixels, as an entry and in its object", groups[0].HasPixelData && !groups[1].HasPixelData && !groups[2].HasPixelData);
            var objects = groups.SelectMany(ModalityCatalog.ObjectsOf).ToList();
            Check("Their objects in order: Secondary Capture first, then the PDF and the two reports",
                string.Join(" | ", objects.Select(o => o.Name + " (" + o.DatasetProfileId + ")")) == "Secondary Capture (secondary-capture) | Encapsulated PDF (encapsulated-pdf) | Basic Text SR (basic-text-sr) | Enhanced SR (enhanced-sr)",
                string.Join(" | ", objects.Select(o => o.Name)));
            Check("Their SOP classes", objects.Select(o => o.Image.SopClass).SequenceEqual(new[] { DicomUID.SecondaryCaptureImageStorage, DicomUID.EncapsulatedPDFStorage, DicomUID.BasicTextSRStorage, DicomUID.EnhancedSRStorage }));
            Check("Only Secondary Capture has pixels, and none is multi-frame", objects[0].HasPixelData && objects.Skip(1).All(o => !o.HasPixelData) && objects.All(o => !o.IsMultiFrame));
            Check("Each is found by its dataset id, and each id is a dataset profile", objects.All(o => ReferenceEquals(ModalityCatalog.FindObject(o.DatasetProfileId), o) && DatasetProfileCatalog.Find(o.DatasetProfileId) != null));
            Check("OT starts at the defaults of Secondary Capture", groups[0].DefaultRows == 512 && groups[0].DefaultBitDepth == 8 && groups[0].SamplesPerPixel == 3, null);
            Check("SR starts at the Report exam type of Basic Text SR", groups[2].Presets.Single().Name == "Report", null);

            foreach (var item in objects)
            {
                var request = Request(item.Image);
                request.Identity.PatientName = "DCST4^TEST^12345678";
                request.ImagesPerSeries = 2;
                var files = new ExamGenerator(request).GetImages(CancellationToken.None).Select(i => Reopened(i.File).Dataset).ToList();
                var name = item.Name;
                Check($"{name}: two objects of its SOP class, with the patient and study",
                    files.Count == 2 && files.All(d => d.GetSingleValue<DicomUID>(DicomTag.SOPClassUID) == item.Image.SopClass && d.GetString(DicomTag.PatientName) == "DCST4^TEST^12345678"
                        && d.GetString(DicomTag.StudyInstanceUID) == request.Identity.StudyInstanceUid && d.GetString(DicomTag.Manufacturer) == "DCST4"),
                    files.Count.ToString());
                Check($"{name}: each has its own SOP Instance UID", files.Select(d => d.GetString(DicomTag.SOPInstanceUID)).Distinct().Count() == 2);
                Check($"{name}: pixels only where it is an image", files.All(d => d.Contains(DicomTag.PixelData) == item.HasPixelData));
                Check($"{name}: Explicit and Implicit VR can be chosen", ExamValidator.Validate(request).Count == 0
                    && ExamValidator.SyntaxProblems(request, DicomTransferSyntax.ExplicitVRLittleEndian).Count == 0 && ExamValidator.SyntaxProblems(request, DicomTransferSyntax.ImplicitVRLittleEndian).Count == 0,
                    string.Join(" ", ExamValidator.Validate(request)));
            }

            var pdf = Request(objects[1].Image);
            var pdfFile = First(pdf).Dataset;
            var document = pdfFile.GetDicomItem<DicomElement>(DicomTag.EncapsulatedDocument).Buffer.Data;
            Check("The PDF holds a PDF", System.Text.Encoding.ASCII.GetString(document, 0, 5) == "%PDF-" && pdfFile.GetString(DicomTag.MIMETypeOfEncapsulatedDocument) == "application/pdf");
            Check("A PDF cannot be compressed: the syntax is refused by name", ExamValidator.SyntaxProblems(pdf, DicomTransferSyntax.JPEGProcess14SV1).Any(p => p.Contains("no pixel data")),
                string.Join(" ", ExamValidator.SyntaxProblems(pdf, DicomTransferSyntax.JPEGProcess14SV1)));
            pdf.PrivatePayloadBytes = 1024;
            Check("A PDF gets no private payload", ExamValidator.Validate(pdf).Any(p => p.Contains("not an image")), string.Join(" ", ExamValidator.Validate(pdf)));
            pdf.PrivatePayloadBytes = 0;
            pdf.Rows = 1;
            pdf.BitsStored = 16;
            Check("The matrix and bits on the Exam tab do not apply to a PDF", ExamValidator.Validate(pdf).Count == 0, string.Join(" ", ExamValidator.Validate(pdf)));
            Check("The size of a PDF is its header and the document", ExamSizeEstimator.UncompressedImageBytes(pdf) == ExamSizeEstimator.HeaderBytes + new Core.Generation.Objects.EncapsulatedPdfProfile().EstimatedContentBytes
                && ExamSizeEstimator.UncompressedImageBytes(pdf) < 4000, ExamSizeEstimator.UncompressedImageBytes(pdf)?.ToString());

            var enhancedSr = First(Request(objects[3].Image)).Dataset;
            var findings = enhancedSr.GetSequence(DicomTag.ContentSequence).Items[0].GetSequence(DicomTag.ContentSequence).Items;
            Check("Enhanced SR: the text finding and a measurement in mm", findings.Count == 2 && findings[0].GetString(DicomTag.ValueType) == "TEXT" && findings[1].GetString(DicomTag.ValueType) == "NUM"
                && findings[1].GetSequence(DicomTag.MeasuredValueSequence).Items[0].GetSequence(DicomTag.MeasurementUnitsCodeSequence).Items[0].GetString(DicomTag.CodeValue) == "mm", findings.Count.ToString());
            var basicSr = First(Request(objects[2].Image)).Dataset;
            Check("Basic Text SR: the text finding only", basicSr.GetSequence(DicomTag.ContentSequence).Items[0].GetSequence(DicomTag.ContentSequence).Items.Count == 1);

            Scp.Reset();
            var stored = new List<RunSummary>();
            foreach (var item in objects) stored.Add(await new StoreRun(new ExamGenerator(Request(item.Image)), SendOptions(DicomTransferSyntax.ExplicitVRLittleEndian)).ExecuteAsync(CancellationToken.None));
            var received = Scp.Received.ToList();
            Check("All four are sent and stored", stored.All(r => r.Success == 1 && r.FatalError == null) && received.Count == 4
                && received.Select(r => r.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID)).SequenceEqual(objects.Select(o => o.Image.SopClass)),
                string.Join(", ", stored.Select(r => r + " " + r.FatalError)));
            Scp.Reset();
        }
    }
}
