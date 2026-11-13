using DCST4.Core.Generation;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>Phase 2: deterministic test data, dataset profiles, variants and the manifest.</summary>
    internal static class TestDataTests
    {
        private const int SmallMatrix = 64;
        private static readonly Regex MarkedName = new Regex(@"^DCST4\^TEST\^\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex MarkedReferrer = new Regex(@"^DCST4\^REFERRING\^\d{8}$", RegexOptions.CultureInvariant);

        private static TestDatasetRequest Small(string profile, int seed = 1)
        {
            var image = DatasetProfileCatalog.Find(profile).Image;
            return new TestDatasetRequest
            {
                ProfileId = profile,
                Seed = seed,
                Rows = image != null ? SmallMatrix : 0,
                Columns = image != null ? SmallMatrix : 0,
                Frames = image != null && image.IsMultiFrame ? 3 : 0,
            };
        }

        private sealed class Generated
        {
            public List<SourceImage> Images;
            public DatasetManifest Manifest;
        }

        private static Generated Generate(TestDatasetRequest request)
        {
            var generator = new TestDatasetGenerator(request);
            var images = generator.GetImages(CancellationToken.None).ToList();
            return new Generated { Images = images, Manifest = generator.Manifest };
        }

        private static DicomDataset Target(Generated generated)
        {
            var index = generated.Manifest.Instances.FindIndex(i => i.Role == ManifestInstance.RoleTarget);
            return generated.Images[index].File.Dataset;
        }

        private static string Text(DicomDataset dataset, DicomTag tag) => dataset.GetSingleValueOrDefault(tag, string.Empty);

        private static string HashOf(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return ManifestHashes.Hex(sha.ComputeHash(stream));
            }
        }

        public static async Task Run(int port, string temp, Action<string, bool, string> check)
        {
            var folder = Path.Combine(temp, "testdata");
            Directory.CreateDirectory(folder);

            TestUids(check);
            TestValidation(check);
            TestDeterminism(check);
            TestStructure(folder, check);
            TestNaming(check);
            TestCharacterSets(folder, check);
            TestEdgeCases(folder, check);
            TestCompression(folder, check);
            TestLargeObject(folder, check);
            await TestPipeline(port, folder, check);
            await TestCli(folder, check);
            TestBoundaries(check);
        }

        private sealed class CaptureNaming : ITestCase
        {
            public TestPatientNaming Seen { get; private set; } = TestPatientNaming.MarkedTestName;
            public string Id => "capture-naming";
            public string Name => "Capture naming";
            public TestCategory Category => TestCategory.Capability;
            public string Description => "Records the patient naming it was given.";
            public string Group => TestGroups.Association;
            public bool StoresObjects => false;
            public StorageEstimate Storage => null;

            public Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
            {
                Seen = context.PatientNaming;
                return Task.FromResult(new TestResult { Result = TestResultClass.PASS, Explanation = "captured" });
            }
        }

        // ---------------------------------------------------------------- UIDs

        private static void TestUids(Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: UIDs");
            var source = new DeterministicUidSource(null, "seed=1");
            var uid = source.Uid("sop/0/0");
            check("No root: 2.25 and a decimal value", uid.StartsWith("2.25.") && ExamValidator.IsValidUid(uid), uid);
            check("The same input gives the same UID", uid == new DeterministicUidSource(null, "seed=1").Uid("sop/0/0"), null);
            check("Another counter gives another UID", uid != source.Uid("sop/0/1"), null);
            check("Another seed gives another UID", uid != new DeterministicUidSource(null, "seed=2").Uid("sop/0/0"), null);

            var root40 = "1.2.826.0.1.3680043.8.498.1234567890.1234";
            root40 = root40.Substring(0, 40).TrimEnd('.');
            var rooted = Enumerable.Range(0, 200).Select(i => new DeterministicUidSource(root40, "seed=1").Uid("sop/" + i)).ToList();
            check("A root of 40 characters stays within 64", rooted.All(u => u.Length <= 64 && ExamValidator.IsValidUid(u)), rooted[0]);
            check("It starts with the root", rooted.All(u => u.StartsWith(root40 + ".")), rooted[0]);
            check("All are different", rooted.Distinct().Count() == 200, null);
            check("Within adds to the context", new DeterministicUidSource(null, "a").Within("b").Uid("c") != new DeterministicUidSource(null, "a").Uid("c"), null);

            var bytes = BitConverter.GetBytes(0L);
            check("A UUID-derived UID has at most 39 digits", uid.Length <= "2.25.".Length + 39, uid);
            check("Generated source: a new UID each time", GeneratedUidSource.Instance.Uid("x") != GeneratedUidSource.Instance.Uid("x"), bytes.Length.ToString());

            var ct = ModalityCatalog.Find("CT");
            var auto = new ExamRequest { Modality = ct, Preset = ct.Presets[0], Identity = new IdentityGenerator().Generate(null, ct, ct.Presets[0]), Rows = 64, Columns = 64, BitsStored = 16 };
            var first = new ExamGenerator(auto).GetImages(CancellationToken.None).Single().SopInstanceUid;
            var second = new ExamGenerator(auto).GetImages(CancellationToken.None).Single().SopInstanceUid;
            check("Generate & Send still draws new UIDs", first != second, first);
            check("Generate & Send: the list of modalities is unchanged", ModalityCatalog.All.Select(m => m.Code).SequenceEqual(new[] { "CT", "MR", "CR", "DX", "US", "XA", "RF", "MG", "PT", "NM" }), null);
        }

        private static void TestValidation(Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: requests");
            check("Default request is valid", new TestDatasetRequest { ProfileId = "ct" }.Validate().Count == 0, null);
            check("Large profile is off by default", !DatasetProfileCatalog.Default.Any(p => p.Id == "tomosynthesis-large"), null);
            check("Large profile can be asked for", DatasetProfileCatalog.Find("tomosynthesis-large") != null, null);
            check("Unknown profile is refused", new TestDatasetRequest { ProfileId = "nope" }.Validate().Single().Contains("nope"), null);

            Action<string, TestDatasetRequest, string> refused = (name, request, expected) =>
            {
                var problems = request.Validate();
                check("Refused: " + name, problems.Any(p => p.Contains(expected)), string.Join("; ", problems));
            };

            refused("frames on a single-frame image", new TestDatasetRequest { ProfileId = "ct", Frames = 2 }, "one frame");
            refused("a bit depth the IOD does not allow", new TestDatasetRequest { ProfileId = "ct", BitsStored = 8 }, "does not allow");
            refused("a matrix too small", new TestDatasetRequest { ProfileId = "ct", Rows = 8 }, "Rows");
            refused("a bad UID root", new TestDatasetRequest { ProfileId = "ct", UidRoot = "1.02" }, "UID root");
            refused("a root over 40 characters", new TestDatasetRequest { ProfileId = "ct", UidRoot = new string('1', 41) }, "UID root");
            refused("a binary matrix that is not whole bytes", new TestDatasetRequest { ProfileId = "segmentation", Rows = 65, Columns = 64 }, "multiples of 8");
            refused("compressing a report", new TestDatasetRequest { ProfileId = "basic-text-sr", TransferSyntax = DicomTransferSyntax.JPEGProcess14SV1 }, "no pixel data");
            refused("compressing a segmentation", new TestDatasetRequest { ProfileId = "segmentation", Rows = 64, Columns = 64, TransferSyntax = DicomTransferSyntax.RLELossless }, "uncompressed only");
            check("The large object can be compressed, since its frames are encoded one at a time", new TestDatasetRequest { ProfileId = "tomosynthesis-large", TransferSyntax = DicomTransferSyntax.RLELossless }.Validate().Count == 0, null);
            refused("more uncompressed pixels than an image holds", new TestDatasetRequest { ProfileId = "us-multiframe", Rows = 960, Columns = 1280, Frames = 1667 }, "less than 4 GB");
            check("The same cine loop is accepted in JPEG Baseline", new TestDatasetRequest { ProfileId = "us-multiframe", Rows = 960, Columns = 1280, Frames = 1667, TransferSyntax = DicomTransferSyntax.JPEGProcess1 }.Validate().Count == 0, null);
            refused("a payload over the limit", new TestDatasetRequest { ProfileId = "ct", PrivatePayloadBytes = PrivatePayload.MaxBytes + 1 }, "private payload");
            refused("a payload on a report", new TestDatasetRequest { ProfileId = "basic-text-sr", PrivatePayloadBytes = 1024 }, "not an image");
            refused("too many instances", new TestDatasetRequest { ProfileId = "ct", Patients = 1000, StudiesPerPatient = 1000 }, "at most");
            refused("no patients", new TestDatasetRequest { ProfileId = "ct", Patients = 0 }, "Patients");
        }

        // ---------------------------------------------------------------- determinism

        private static void TestDeterminism(Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: same seed, same result");
            foreach (var profile in DatasetProfileCatalog.All)
            {
                var first = Generate(Small(profile.Id));
                var second = Generate(Small(profile.Id));
                var other = Generate(Small(profile.Id, seed: 2));

                check($"{profile.Id}: the same seed gives an identical manifest", first.Manifest.ToJson() == second.Manifest.ToJson(), null);
                check($"{profile.Id}: instances are in the manifest", first.Manifest.Instances.Count == first.Images.Count && first.Images.Count > 0, null);
                check($"{profile.Id}: another seed gives other files", !first.Manifest.Instances.Select(i => i.FileSha256).Intersect(other.Manifest.Instances.Select(i => i.FileSha256)).Any(), null);
                check($"{profile.Id}: another seed gives other UIDs", first.Manifest.Instances[0].SopInstanceUid != other.Manifest.Instances[0].SopInstanceUid, null);
            }

            var withSyntax = Generate(new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64, TransferSyntax = DicomTransferSyntax.ImplicitVRLittleEndian });
            var explicitSyntax = Generate(new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64 });
            check("Variants never share a SOP Instance UID", withSyntax.Manifest.Instances[0].SopInstanceUid != explicitSyntax.Manifest.Instances[0].SopInstanceUid, null);

            var counted = Generate(new TestDatasetRequest { ProfileId = "ct", Patients = 2, StudiesPerPatient = 2, SeriesPerStudy = 2, InstancesPerSeries = 2, Rows = 64, Columns = 64 });
            var dates = counted.Images.Select(i => Text(i.File.Dataset, DicomTag.StudyDate)).Distinct().ToList();
            check("Patients, studies, series and instances are counted", counted.Images.Count == 16, counted.Images.Count.ToString());
            check("Two patients", counted.Images.Select(i => Text(i.File.Dataset, DicomTag.PatientID)).Distinct().Count() == 2, null);
            check("Four studies", counted.Images.Select(i => Text(i.File.Dataset, DicomTag.StudyInstanceUID)).Distinct().Count() == 4, null);
            check("Eight series", counted.Images.Select(i => Text(i.File.Dataset, DicomTag.SeriesInstanceUID)).Distinct().Count() == 8, null);
            check("All SOP Instance UIDs are unique", counted.Images.Select(i => i.SopInstanceUid).Distinct().Count() == 16, null);
            check("Fixed dates", dates.All(d => d.StartsWith("2026")), string.Join(",", dates));
            check("Series ordinals of a run do not repeat", counted.Images.Select(i => i.SeriesNumber + "/" + i.InstanceNumber).Distinct().Count() == 16, null);
            check("The DICOM Series Number restarts per study", counted.Images.Select(i => Text(i.File.Dataset, DicomTag.SeriesNumber)).Distinct().Count() == 2, null);

            var multi = Generate(Small("enhanced-ct"));
            var frames = multi.Manifest.Instances[0].FrameSha256;
            var longer = new TestDatasetRequest { ProfileId = "enhanced-ct", Rows = 64, Columns = 64, Frames = 6 };
            var longFrames = Generate(longer).Manifest.Instances[0].FrameSha256;
            check("Each frame of a multi-frame image is different", longFrames.Distinct().Count() == 6 && frames.Distinct().Count() == 3, string.Join(",", longFrames));

            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try { new TestDatasetGenerator(Small("ct")).GetImages(cancelled.Token).ToList(); }
            catch (OperationCanceledException) { threw = true; }
            check("Generation stops on cancel", threw, null);
        }

        // ---------------------------------------------------------------- structure

        private static readonly DicomTag[] RequiredEverywhere =
        {
            DicomTag.SpecificCharacterSet, DicomTag.SOPClassUID, DicomTag.SOPInstanceUID, DicomTag.PatientName, DicomTag.PatientID,
            DicomTag.PatientBirthDate, DicomTag.PatientSex, DicomTag.StudyInstanceUID, DicomTag.StudyDate, DicomTag.StudyTime,
            DicomTag.ReferringPhysicianName, DicomTag.StudyID, DicomTag.AccessionNumber, DicomTag.Modality, DicomTag.SeriesInstanceUID,
            DicomTag.SeriesNumber, DicomTag.Manufacturer, DicomTag.InstanceNumber, DicomTag.ContentDate, DicomTag.ContentTime,
        };

        private static readonly DicomTag[] ImageRequired =
        {
            DicomTag.ImageType, DicomTag.SamplesPerPixel, DicomTag.PhotometricInterpretation, DicomTag.Rows, DicomTag.Columns,
            DicomTag.BitsAllocated, DicomTag.BitsStored, DicomTag.HighBit, DicomTag.PixelRepresentation, DicomTag.PixelData,
            DicomTag.BurnedInAnnotation, DicomTag.ImageComments, DicomTag.LossyImageCompression,
        };

        private static readonly DicomTag[] EnhancedRequired =
        {
            DicomTag.NumberOfFrames, DicomTag.SharedFunctionalGroupsSequence, DicomTag.PerFrameFunctionalGroupsSequence,
            DicomTag.DimensionOrganizationSequence, DicomTag.DimensionIndexSequence, DicomTag.AcquisitionDateTime, DicomTag.ContentQualification,
            DicomTag.PresentationLUTShape, DicomTag.PixelPresentation, DicomTag.VolumetricProperties, DicomTag.VolumeBasedCalculationTechnique,
            DicomTag.FrameOfReferenceUID, DicomTag.PositionReferenceIndicator,
        };

        private static readonly DicomTag[] EnhancedXrayRequired =
        {
            DicomTag.NumberOfFrames, DicomTag.SharedFunctionalGroupsSequence, DicomTag.PerFrameFunctionalGroupsSequence,
            DicomTag.AcquisitionDateTime, DicomTag.ContentQualification, DicomTag.PresentationLUTShape, DicomTag.PlanesInAcquisition,
            DicomTag.PlaneIdentification, DicomTag.AcquisitionContextSequence,
        };

        private static readonly Dictionary<string, DicomTag[]> Required = new Dictionary<string, DicomTag[]>
        {
            { "us-multiframe", new[] { DicomTag.NumberOfFrames, DicomTag.FrameTime, DicomTag.FrameIncrementPointer, DicomTag.PlanarConfiguration, DicomTag.TransducerData, DicomTag.SequenceOfUltrasoundRegions } },
            { "xa-cine", new[] { DicomTag.NumberOfFrames, DicomTag.FrameTime, DicomTag.FrameIncrementPointer, DicomTag.CineRate, DicomTag.PositionerMotion, DicomTag.RadiationSetting, DicomTag.ImagerPixelSpacing } },
            { "rf-cine", new[] { DicomTag.NumberOfFrames, DicomTag.FrameTime, DicomTag.FrameIncrementPointer, DicomTag.CineRate, DicomTag.RadiationSetting, DicomTag.ImagerPixelSpacing } },
            { "secondary-capture", new[] { DicomTag.ConversionType, DicomTag.PatientOrientation, DicomTag.PlanarConfiguration } },
            { "enhanced-ct", EnhancedRequired },
            { "enhanced-mr", EnhancedRequired.Concat(new[] { DicomTag.ComplexImageComponent, DicomTag.AcquisitionContrast }).ToArray() },
            { "tomosynthesis-large", EnhancedRequired.Concat(new[] { DicomTag.ImageLaterality, DicomTag.PresentationIntentType, DicomTag.AcquisitionContextSequence }).ToArray() },
            // The Enhanced XA and XRF IODs have no Frame of Reference, no dimensions and no Pixel Presentation.
            { "enhanced-xa", EnhancedXrayRequired },
            { "enhanced-rf", EnhancedXrayRequired },
            { "enhanced-us-volume", new[] { DicomTag.NumberOfFrames, DicomTag.SharedFunctionalGroupsSequence, DicomTag.PerFrameFunctionalGroupsSequence, DicomTag.DimensionOrganizationSequence, DicomTag.DimensionIndexSequence, DicomTag.DimensionOrganizationType, DicomTag.FrameOfReferenceUID, DicomTag.VolumeFrameOfReferenceUID, DicomTag.UltrasoundAcquisitionGeometry, DicomTag.ApexPosition, DicomTag.VolumeToTransducerMappingMatrix, DicomTag.SynchronizationFrameOfReferenceUID, DicomTag.AcquisitionDuration, DicomTag.TransducerScanPatternCodeSequence, DicomTag.ViewCodeSequence, DicomTag.AnatomicRegionSequence, DicomTag.RescaleSlope, DicomTag.AcquisitionContextSequence } },
            { "segmentation", new[] { DicomTag.NumberOfFrames, DicomTag.SharedFunctionalGroupsSequence, DicomTag.PerFrameFunctionalGroupsSequence, DicomTag.DimensionOrganizationSequence, DicomTag.DimensionIndexSequence, DicomTag.SegmentationType, DicomTag.ContentLabel, DicomTag.ContentDescription, DicomTag.ContentCreatorName, DicomTag.SegmentSequence, DicomTag.ReferencedSeriesSequence, DicomTag.FrameOfReferenceUID } },
            { "enhanced-sr", new[] { DicomTag.CompletionFlag, DicomTag.VerificationFlag, DicomTag.ValueType, DicomTag.ConceptNameCodeSequence, DicomTag.ContinuityOfContent, DicomTag.ContentSequence, DicomTag.PerformedProcedureCodeSequence, DicomTag.ReferencedPerformedProcedureStepSequence } },
            { "basic-text-sr", new[] { DicomTag.CompletionFlag, DicomTag.VerificationFlag, DicomTag.ValueType, DicomTag.ConceptNameCodeSequence, DicomTag.ContinuityOfContent, DicomTag.ContentSequence, DicomTag.PerformedProcedureCodeSequence, DicomTag.ReferencedPerformedProcedureStepSequence } },
            { "key-object-selection", new[] { DicomTag.ValueType, DicomTag.ConceptNameCodeSequence, DicomTag.ContinuityOfContent, DicomTag.ContentTemplateSequence, DicomTag.ContentSequence, DicomTag.CurrentRequestedProcedureEvidenceSequence, DicomTag.ReferencedPerformedProcedureStepSequence } },
            { "grayscale-presentation-state", new[] { DicomTag.PresentationCreationDate, DicomTag.PresentationCreationTime, DicomTag.ContentLabel, DicomTag.ContentDescription, DicomTag.ContentCreatorName, DicomTag.ReferencedSeriesSequence, DicomTag.DisplayedAreaSelectionSequence, DicomTag.GraphicLayerSequence, DicomTag.GraphicAnnotationSequence, DicomTag.PresentationLUTShape } },
            { "encapsulated-pdf", new[] { DicomTag.ConversionType, DicomTag.BurnedInAnnotation, DicomTag.AcquisitionDateTime, DicomTag.ConceptNameCodeSequence, DicomTag.DocumentTitle, DicomTag.MIMETypeOfEncapsulatedDocument, DicomTag.EncapsulatedDocument, DicomTag.EncapsulatedDocumentLength } },
            { "rt-structure-set", new[] { DicomTag.StructureSetLabel, DicomTag.StructureSetDate, DicomTag.StructureSetTime, DicomTag.ReferencedFrameOfReferenceSequence, DicomTag.StructureSetROISequence, DicomTag.ROIContourSequence, DicomTag.RTROIObservationsSequence, DicomTag.OperatorsName } },
        };

        private static void TestStructure(string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: every profile opens and is complete");
            var target = Path.Combine(folder, "structure");
            Directory.CreateDirectory(target);

            foreach (var profile in DatasetProfileCatalog.All)
            {
                var request = Small(profile.Id);
                var generated = Generate(request);
                var path = Path.Combine(target, profile.Id + ".dcm");

                var targetIndex = generated.Manifest.Instances.FindIndex(i => i.Role == ManifestInstance.RoleTarget);
                generated.Images[targetIndex].File.Save(path);
                var read = DicomFile.Open(path);
                var dataset = read.Dataset;

                var uids = new[] { DicomTag.SOPClassUID, DicomTag.SOPInstanceUID, DicomTag.StudyInstanceUID, DicomTag.SeriesInstanceUID }.Select(t => Text(dataset, t)).ToList();
                check($"{profile.Id}: opens, with valid UIDs", uids.All(ExamValidator.IsValidUid), string.Join(" ", uids));
                check($"{profile.Id}: SOP class in the file meta header", read.FileMetaInfo.MediaStorageSOPClassUID?.UID == uids[0], null);
                check($"{profile.Id}: the manifest's file hash is this file", HashOf(path) == generated.Manifest.Instances[targetIndex].FileSha256, null);
                check($"{profile.Id}: manufacturer marks it as a test object", Text(dataset, DicomTag.Manufacturer) == "DCST4", null);
                check($"{profile.Id}: file meta header does not name this computer", read.FileMetaInfo.SourceApplicationEntityTitle == "DCST4", read.FileMetaInfo.SourceApplicationEntityTitle);

                var requiredTags = RequiredEverywhere.Where(t => profile.Id != "rt-structure-set" && profile.Id != "grayscale-presentation-state" || (t != DicomTag.ContentDate && t != DicomTag.ContentTime)).Concat(profile.IsImage ? ImageRequired : new DicomTag[0]);
                if (Required.TryGetValue(profile.Id, out var extra)) requiredTags = requiredTags.Concat(extra);
                var missing = requiredTags.Where(t => !dataset.Contains(t)).Select(t => t.ToString()).ToList();
                check($"{profile.Id}: required attributes present", missing.Count == 0, string.Join(" ", missing));

                if (profile.IsImage)
                {
                    var image = profile.Image;
                    var frames = request.Frames > 0 ? request.Frames : 1;
                    var samples = dataset.GetSingleValue<ushort>(DicomTag.SamplesPerPixel);
                    var bitsAllocated = dataset.GetSingleValue<ushort>(DicomTag.BitsAllocated);
                    var expected = SmallMatrix * SmallMatrix * frames * samples * bitsAllocated / 8;
                    var length = dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Size;
                    check($"{profile.Id}: pixel length is Rows x Columns x Frames x Samples x Bits / 8", length == expected + (expected % 2), $"{length}, expected {expected}");
                    check($"{profile.Id}: test image marking in Image Comments", Text(dataset, DicomTag.ImageComments) == ProductUrl, null);
                    check($"{profile.Id}: pixels decode as the manifest says", ManifestHashes.FrameHashes(dataset).SequenceEqual(generated.Manifest.Instances[targetIndex].FrameSha256), null);
                    if (image.UsesFunctionalGroups) CheckFunctionalGroups(profile, dataset, frames, check);
                }
                else
                {
                    check($"{profile.Id}: no Pixel Data", !dataset.Contains(DicomTag.PixelData), null);
                    check($"{profile.Id}: has no frames in the manifest", generated.Manifest.Instances[targetIndex].FrameSha256.Count == 0, null);
                }

                var sourceCount = generated.Manifest.Instances.Count(i => i.Role == ManifestInstance.RoleSourceImage);
                check($"{profile.Id}: a source image only where the object refers to one", sourceCount == (profile.NeedsSourceImage ? 1 : 0), sourceCount.ToString());
                if (profile.NeedsSourceImage)
                {
                    var source = generated.Images[generated.Manifest.Instances.FindIndex(i => i.Role == ManifestInstance.RoleSourceImage)].File.Dataset;
                    check($"{profile.Id}: the source image is in the same study", Text(source, DicomTag.StudyInstanceUID) == Text(dataset, DicomTag.StudyInstanceUID), null);
                    check($"{profile.Id}: the source image is first in the study", Text(source, DicomTag.SeriesNumber) == "1" && Text(dataset, DicomTag.SeriesNumber) == "2", Text(dataset, DicomTag.SeriesNumber));
                }
            }

            var sr = Target(Generate(Small("key-object-selection")));
            var imageItem = sr.GetSequence(DicomTag.ContentSequence).Items[1];
            check("KOS points at the source image", imageItem.GetSequence(DicomTag.ReferencedSOPSequence).Items[0].Contains(DicomTag.ReferencedSOPInstanceUID), null);

            var pdf = Target(Generate(Small("encapsulated-pdf")));
            var document = pdf.GetDicomItem<DicomElement>(DicomTag.EncapsulatedDocument).Buffer.Data;
            check("PDF begins and ends as a PDF", Encoding.ASCII.GetString(document, 0, 5) == "%PDF-" && Encoding.ASCII.GetString(document).TrimEnd().EndsWith("%%EOF"), null);
            check("PDF is marked as a test document", Encoding.ASCII.GetString(document).Contains("TEST DOCUMENT"), null);
            check("PDF length is even and recorded", document.Length % 2 == 0 && pdf.GetSingleValue<uint>(DicomTag.EncapsulatedDocumentLength) == document.Length, null);
        }

        private const string ProductUrl = "https://github.com/droidgren/DCST4";

        private static void CheckFunctionalGroups(DatasetProfile profile, DicomDataset dataset, int frames, Action<string, bool, string> check)
        {
            var perFrame = dataset.GetSequence(DicomTag.PerFrameFunctionalGroupsSequence).Items;
            var shared = dataset.GetSequence(DicomTag.SharedFunctionalGroupsSequence).Items[0];
            check($"{profile.Id}: one item per frame", perFrame.Count == frames, perFrame.Count.ToString());
            check($"{profile.Id}: frame content per frame", perFrame.All(i => i.Contains(DicomTag.FrameContentSequence)), null);
            // The Enhanced XA and XRF IODs have none of the slice geometry macros, so they are the exception here. The Enhanced
            // US Volume places its frames in the volume's coordinates, with the (Volume) plane macros in place of the patient ones.
            var volume = profile.Image is EnhancedUsVolumeProfile;
            var stack = !(profile.Image is EnhancedXrayProfile) && !volume;
            check($"{profile.Id}: frame position per frame, where the IOD has it", perFrame.All(i => i.Contains(DicomTag.PlanePositionSequence) == stack && i.Contains(DicomTag.PlanePositionVolumeSequence) == volume), null);
            check($"{profile.Id}: pixel measures and orientation shared, where the IOD has them",
                shared.Contains(DicomTag.PixelMeasuresSequence) == (stack || volume) && shared.Contains(DicomTag.PlaneOrientationSequence) == stack && shared.Contains(DicomTag.PlaneOrientationVolumeSequence) == volume, null);
            // Enhanced US Image has Rescale Slope and Intercept as Type 1 at the top level.
            check($"{profile.Id}: no window at the top level", !dataset.Contains(DicomTag.WindowCenter) && dataset.Contains(DicomTag.RescaleIntercept) == volume, null);
            check($"{profile.Id}: no acquisition date without a time zone rule", !dataset.Contains(DicomTag.AcquisitionDate), null);

            if (profile.Image.IsBinaryMask)
            {
                check($"{profile.Id}: frames name the source image", perFrame.All(i => i.Contains(DicomTag.DerivationImageSequence) && i.Contains(DicomTag.SegmentIdentificationSequence)), null);
                return;
            }

            check($"{profile.Id}: window in the shared groups", shared.Contains(DicomTag.FrameVOILUTSequence), null);
            // The Enhanced US Volume IOD has its anatomy at the top level, and no Frame Anatomy macro.
            check($"{profile.Id}: frame anatomy in the shared groups, or at the top level for US Volume", volume ? dataset.Contains(DicomTag.AnatomicRegionSequence) && !shared.Contains(DicomTag.FrameAnatomySequence) : shared.Contains(DicomTag.FrameAnatomySequence), null);
        }

        // ---------------------------------------------------------------- naming and character sets

        private static void TestNaming(Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: patient naming");
            var registry = TestPersonnummerRegistry.Default;

            var swedish = Generate(new TestDatasetRequest { ProfileId = "ct", Patients = 20, Rows = 64, Columns = 64, PatientNaming = TestPatientNaming.RegistrySwedishName });
            var marked = Generate(new TestDatasetRequest { ProfileId = "ct", Patients = 20, Rows = 64, Columns = 64, PatientNaming = TestPatientNaming.MarkedTestName });

            check("Swedish name: Patient IDs are in the registry", swedish.Images.All(i => registry.Contains(Text(i.File.Dataset, DicomTag.PatientID))), null);
            check("Marked name: Patient IDs are in the registry", marked.Images.All(i => registry.Contains(Text(i.File.Dataset, DicomTag.PatientID))), null);
            check("Swedish name: not marked", swedish.Images.All(i => !Text(i.File.Dataset, DicomTag.PatientName).StartsWith("DCST4")), null);
            check("Marked name: DCST4^TEST^<number>", marked.Images.All(i => MarkedName.IsMatch(Text(i.File.Dataset, DicomTag.PatientName))), Text(marked.Images[0].File.Dataset, DicomTag.PatientName));
            check("Marked name: the number varies", marked.Images.Select(i => Text(i.File.Dataset, DicomTag.PatientName)).Distinct().Count() > 15, null);
            check("Marked name: Referring Physician DCST4^REFERRING^<number>", marked.Images.All(i => MarkedReferrer.IsMatch(Text(i.File.Dataset, DicomTag.ReferringPhysicianName))), Text(marked.Images[0].File.Dataset, DicomTag.ReferringPhysicianName));
            check("Swedish name: the referrer is not marked", swedish.Images.All(i => !Text(i.File.Dataset, DicomTag.ReferringPhysicianName).StartsWith("DCST4")), Text(swedish.Images[0].File.Dataset, DicomTag.ReferringPhysicianName));

            var again = Generate(new TestDatasetRequest { ProfileId = "ct", Patients = 20, Rows = 64, Columns = 64, PatientNaming = TestPatientNaming.MarkedTestName });
            check("Marked name: the number comes from the seed", again.Images.Select(i => Text(i.File.Dataset, DicomTag.PatientName)).SequenceEqual(marked.Images.Select(i => Text(i.File.Dataset, DicomTag.PatientName))), null);

            var older = Generate(new TestDatasetRequest { ProfileId = "ct", Patients = 5, Rows = 64, Columns = 64, ReferenceDate = new DateTime(2030, 6, 1) });
            var fixedDate = Generate(new TestDatasetRequest { ProfileId = "ct", Patients = 5, Rows = 64, Columns = 64 });
            check("The reference date decides who can be picked", older.Manifest.Request.ReferenceDate != fixedDate.Manifest.Request.ReferenceDate, null);
            check("Manifest records the personnummer list", fixedDate.Manifest.RegistrySource == registry.SourceDescription && !string.IsNullOrEmpty(registry.SourceDescription), fixedDate.Manifest.RegistrySource);
            check("Manifest records the tool versions", !string.IsNullOrEmpty(fixedDate.Manifest.Tools.Dcst4Version) && !string.IsNullOrEmpty(fixedDate.Manifest.Tools.FoDicomVersion) && !string.IsNullOrEmpty(fixedDate.Manifest.Tools.OsVersion), null);

            string parsed;
            check("Naming parses, ignoring case", TestPatientNames.TryParse("markedtestname", out var naming) && naming == TestPatientNaming.MarkedTestName, null);
            check("Naming: anything else is refused and means the default", !TestPatientNames.TryParse("nonsense", out naming) && naming == TestPatientNaming.MarkedTestName, null);
            check("Naming: a blank setting means the default", !TestPatientNames.TryParse("", out naming) && naming == TestPatientNaming.MarkedTestName, null);
            parsed = TestPatientNaming.MarkedTestName.ToString();
            check("Naming: the default is the marked test name", new TestDatasetRequest().PatientNaming.ToString() == parsed && TestPatientNames.Default == TestPatientNaming.MarkedTestName, null);
        }

        private static byte[] RawValueFromFile(string path, DicomTag tag)
        {
            var bytes = File.ReadAllBytes(path);
            var pattern = new[] { (byte)tag.Group, (byte)(tag.Group >> 8), (byte)tag.Element, (byte)(tag.Element >> 8) };
            for (var i = 132; i < bytes.Length - 8; i++)
            {
                if (bytes[i] == pattern[0] && bytes[i + 1] == pattern[1] && bytes[i + 2] == pattern[2] && bytes[i + 3] == pattern[3])
                {
                    var length = BitConverter.ToUInt16(bytes, i + 6);
                    return bytes.Skip(i + 8).Take(length).ToArray();
                }
            }
            return new byte[0];
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (!needle.Where((b, j) => haystack[i + j] != b).Any()) return true;
            }
            return false;
        }

        private static void TestCharacterSets(string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: character set variants");
            const string letters = "åäöÅÄÖ";
            var latin1 = Encoding.GetEncoding("ISO-8859-1").GetBytes(letters);
            var utf8 = Encoding.UTF8.GetBytes(letters);
            var target = Path.Combine(folder, "charsets");
            Directory.CreateDirectory(target);

            foreach (var naming in new[] { TestPatientNaming.RegistrySwedishName, TestPatientNaming.MarkedTestName })
            {
                foreach (var variant in new[] { CharacterSetVariant.Latin1Swedish, CharacterSetVariant.Utf8Swedish, CharacterSetVariant.EmptyAscii })
                {
                    var request = new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64, CharacterSet = variant, PatientNaming = naming };
                    var generated = Generate(request);
                    var image = generated.Images[0];
                    var path = Path.Combine(target, naming + "_" + variant + ".dcm");
                    image.File.Save(path);
                    var read = DicomFile.Open(path).Dataset;
                    var name = Text(read, DicomTag.PatientName);
                    var label = $"{naming} {variant}";

                    check($"{label}: Patient ID is in the registry", TestPersonnummerRegistry.Default.Contains(Text(read, DicomTag.PatientID)), null);
                    if (naming == TestPatientNaming.MarkedTestName) check($"{label}: keeps the marker", new Regex(@"^DCST4\^TEST\^\d+").IsMatch(name), name);

                    if (variant == CharacterSetVariant.EmptyAscii)
                    {
                        var ascii = RawValueFromFile(path, DicomTag.PatientName);
                        check($"{label}: name is Basic ASCII in the file", ascii.Length > 0 && ascii.All(b => b < 0x80), name);
                        check($"{label}: Specific Character Set is empty", Text(read, DicomTag.SpecificCharacterSet).Length == 0, null);
                        check($"{label}: descriptions are ASCII", Text(read, DicomTag.StudyDescription).All(c => c < 0x80) && Text(read, DicomTag.SeriesDescription).All(c => c < 0x80), null);
                        check($"{label}: the letters are spelled without diacritics", name.Contains("aaoAAO"), name);
                    }
                    else
                    {
                        var expected = variant == CharacterSetVariant.Utf8Swedish ? utf8 : latin1;
                        var tag = variant == CharacterSetVariant.Utf8Swedish ? DicomCharacterSets.Utf8 : DicomCharacterSets.Latin1;
                        check($"{label}: Specific Character Set", Text(read, DicomTag.SpecificCharacterSet) == tag, null);
                        check($"{label}: name holds all six letters, read back", name.Contains(letters), name);
                        check($"{label}: name is encoded right in the file", Contains(RawValueFromFile(path, DicomTag.PatientName), expected), null);
                        check($"{label}: Study Description holds the letters", Text(read, DicomTag.StudyDescription).Contains(letters) && Contains(RawValueFromFile(path, DicomTag.StudyDescription), expected), Text(read, DicomTag.StudyDescription));
                        check($"{label}: Series Description holds the letters", Text(read, DicomTag.SeriesDescription).Contains(letters), Text(read, DicomTag.SeriesDescription));
                    }
                }
            }

            var report = Target(Generate(new TestDatasetRequest { ProfileId = "basic-text-sr", CharacterSet = CharacterSetVariant.Utf8Swedish }));
            var sequence = report.GetSequence(DicomTag.ContentSequence).Items[0].GetSequence(DicomTag.ContentSequence).Items[0];
            check("A report's text holds the letters too", Text(sequence, DicomTag.TextValue).Contains(letters), Text(sequence, DicomTag.TextValue));

            var plain = Generate(new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64 });
            var utf = Generate(new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64, CharacterSet = CharacterSetVariant.Utf8Swedish });
            check("A variant has its own UIDs", plain.Images[0].SopInstanceUid != utf.Images[0].SopInstanceUid, null);
        }

        private static void TestEdgeCases(string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: edge cases");
            var target = Path.Combine(folder, "edge");
            Directory.CreateDirectory(target);

            foreach (var syntax in new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian })
            {
                var request = new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64, TransferSyntax = syntax, EdgeCases = EdgeCases.PrivateTags | EdgeCases.LongStrings | EdgeCases.NestedSequences };
                var path = Path.Combine(target, syntax.UID.UID + ".dcm");
                var generated = Generate(request);
                generated.Images[0].File.Save(path);
                var dataset = DicomFile.Open(path).Dataset;
                var label = syntax == DicomTransferSyntax.ExplicitVRLittleEndian ? "Explicit" : "Implicit";

                check($"{label}: private creator is written", Text(dataset, new DicomTag(0x0099, 0x0010)) == "DCST4 TEST", Text(dataset, new DicomTag(0x0099, 0x0010)));
                // Implicit VR has no VR on the wire, and a private tag is not in the dictionary, so it reads back as UN bytes.
                var privateText = Encoding.ASCII.GetString(dataset.GetDicomItem<DicomElement>(new DicomTag(0x0099, 0x1001, "DCST4 TEST")).Buffer.Data).TrimEnd(' ', '\0');
                check($"{label}: private text", privateText == "DCST4 private text", privateText);
                check($"{label}: normalized hash survives writing and reading the file", NormalizedAttributes.Hash(dataset) == generated.Manifest.Instances[0].NormalizedAttributesSha256, null);
                check($"{label}: long study description at 64", Text(dataset, DicomTag.StudyDescription).Length == 64, Text(dataset, DicomTag.StudyDescription).Length.ToString());
                check($"{label}: long institution at 64", Text(dataset, DicomTag.InstitutionName).Length == 64, null);
                check($"{label}: long accession number at 16", Text(dataset, DicomTag.AccessionNumber).Length == 16, null);
                check($"{label}: patient comments at 10240", Text(dataset, DicomTag.PatientComments).Length == 10240, null);
                check($"{label}: derivation description at 1024", Text(dataset, DicomTag.DerivationDescription).Length == 1024, null);
                check($"{label}: person name at the 64 of a component group", Text(dataset, DicomTag.ReferringPhysicianName).Length == 64, null);
                check($"{label}: Image Comments keeps the project's address first", Text(dataset, DicomTag.ImageComments).StartsWith(ProductUrl), null);
                check($"{label}: Patient ID untouched", TestPersonnummerRegistry.Default.Contains(Text(dataset, DicomTag.PatientID)), null);

                var depth1 = dataset.GetSequence(DicomTag.RequestAttributesSequence).Items[0];
                var depth2 = depth1.GetSequence(DicomTag.ScheduledProtocolCodeSequence).Items[0];
                var depth3 = depth2.GetSequence(DicomTag.ProtocolContextSequence).Items[0];
                check($"{label}: standard sequences nested three levels", Text(depth3, DicomTag.TextValue) == "DCST4-3", Text(depth3, DicomTag.TextValue));
                // Found by DCMTK's drtdump: items of Protocol Context Sequence are content items.
                check($"{label}: the nested content item is complete", Text(depth3, DicomTag.ValueType) == "TEXT" && depth3.Contains(DicomTag.ConceptNameCodeSequence), null);
            }

            var withPrivate = Generate(new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64, EdgeCases = EdgeCases.PrivateTags }).Images[0].File.Dataset;
            var level1 = withPrivate.GetSequence(new DicomTag(0x0099, 0x1010, "DCST4 TEST")).Items[0];
            var level2 = level1.GetSequence(new DicomTag(0x0099, 0x1011, "DCST4 TEST")).Items[0];
            var level3 = level2.GetSequence(new DicomTag(0x0099, 0x1012, "DCST4 TEST")).Items[0];
            check("Private sequences nest three levels", Text(level3, new DicomTag(0x0099, 0x1013, "DCST4 TEST")) == "Innermost level", null);
            check("Each private item names its creator", new[] { level1, level2, level3 }.All(i => Text(i, new DicomTag(0x0099, 0x0010)) == "DCST4 TEST"), null);

            var edgeTarget = new TestDatasetRequest { ProfileId = "key-object-selection", EdgeCases = EdgeCases.PrivateTags };
            var edge = Generate(edgeTarget);
            var sourceDataset = edge.Images[0].File.Dataset;
            check("Edge cases leave the source image alone", !sourceDataset.Contains(new DicomTag(0x0099, 0x0010)), null);

            // The hash must not move for a change that means nothing.
            var padded = Generate(new TestDatasetRequest { ProfileId = "ct", Rows = 64, Columns = 64 }).Images[0].File;
            var normal = NormalizedAttributes.Hash(padded.Dataset);
            var implicitCopy = padded.Clone(DicomTransferSyntax.ImplicitVRLittleEndian);
            check("Normalized hash does not change with the transfer syntax", NormalizedAttributes.Hash(implicitCopy.Dataset) == normal, null);

            var utf8 = padded.Dataset.Clone();
            utf8.AddOrUpdate(DicomTag.SpecificCharacterSet, DicomCharacterSets.Utf8);
            check("Normalized hash does not change with the character set", NormalizedAttributes.Hash(utf8) == normal, null);

            var changed = padded.Dataset.Clone();
            changed.AddOrUpdate(DicomTag.StudyDescription, "Something else");
            check("Normalized hash changes with the content", NormalizedAttributes.Hash(changed) != normal, null);
        }

        // ---------------------------------------------------------------- compression

        private static void TestCompression(string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: compressed variants");
            var results = CompressionCatalog.ProbeAll();

            Func<string, int, int, CodecProbeResult> find = (id, bits, samples) => results.Single(r => r.SyntaxId == id && r.BitsStored == bits && r.SamplesPerPixel == samples);
            check("JPEG Baseline: 8 bits can be produced", find("jpeg-baseline", 8, 1).Available, find("jpeg-baseline", 8, 1).Reason);
            check("JPEG Baseline: 12 bits is reported as unavailable", !find("jpeg-baseline", 12, 1).Available, null);
            check("JPEG Baseline: 16 bits is reported as unavailable", !find("jpeg-baseline", 16, 1).Available, null);
            check("JPEG Extended: 16 bits is reported as unavailable", !find("jpeg-extended", 16, 1).Available, null);
            foreach (var bits in new[] { 8, 12, 16 }) check($"JPEG Lossless SV1: {bits} bits can be produced", find("jpeg-lossless", bits, 1).Available, find("jpeg-lossless", bits, 1).Reason);

            foreach (var result in results.Where(r => !r.Available)) Console.WriteLine($"  INFO  unavailable: {result.SyntaxId} {result.BitsStored} bits, {result.SamplesPerPixel} samples: {result.Reason}");

            var unavailable = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "cr", Rows = 64, Columns = 64, BitsStored = 12, TransferSyntax = DicomTransferSyntax.JPEGProcess1 });
            check("Unavailable: nothing expected", unavailable.IsUnavailable && unavailable.ExpectedCount == 0, null);
            check("Unavailable: nothing generated", !unavailable.GetImages(CancellationToken.None).Any(), null);
            check("Unavailable: the manifest says why", unavailable.Manifest.Unavailable.Single().Reason.Contains("8 bits") && unavailable.Manifest.Unavailable[0].TransferSyntaxUid == DicomTransferSyntax.JPEGProcess1.UID.UID, unavailable.Manifest.Unavailable[0].Reason);

            foreach (var choice in CompressionCatalog.Compressed)
            {
                // Video holds a colour cine loop only.
                if (choice.IsVideo)
                {
                    CheckVariant(check, folder, choice, "us-multiframe", 8, 3);
                    continue;
                }
                foreach (var bits in new[] { 8, 12, 16 }) CheckVariant(check, folder, choice, "cr", bits, 1);
                CheckVariant(check, folder, choice, "secondary-capture", 8, 3);
                CheckVariant(check, folder, choice, "enhanced-ct", 16, 1);
            }

            var original = Generate(new TestDatasetRequest { ProfileId = "cr", Rows = 64, Columns = 64, BitsStored = 8 }).Images[0].File.Dataset;
            var lossyImageType = Generate(new TestDatasetRequest { ProfileId = "cr", Rows = 64, Columns = 64, BitsStored = 8, TransferSyntax = DicomTransferSyntax.JPEGProcess1 }).Images[0].File.Dataset;
            check("Uncompressed: Lossy Image Compression is 00", Text(original, DicomTag.LossyImageCompression) == "00", null);
            check("Uncompressed: no lossy method", !original.Contains(DicomTag.LossyImageCompressionMethod), null);
            check("Lossy image: Image Type says DERIVED", lossyImageType.GetValues<string>(DicomTag.ImageType)[0] == "DERIVED", null);
        }

        private static void CheckVariant(Action<string, bool, string> check, string folder, SyntaxChoice choice, string profileId, int bits, int samples)
        {
            var label = $"{choice.Id} {profileId} {bits} bits";
            var request = new TestDatasetRequest
            {
                ProfileId = profileId,
                Rows = SmallMatrix,
                Columns = SmallMatrix,
                BitsStored = bits,
                Frames = profileId == "enhanced-ct" ? 2 : profileId == "us-multiframe" ? 3 : 0,
                TransferSyntax = choice.Syntax,
            };

            var generator = new TestDatasetGenerator(request);
            if (generator.IsUnavailable)
            {
                Console.WriteLine($"  INFO  {label}: {generator.Manifest.Unavailable[0].Reason}");
                return;
            }

            var images = generator.GetImages(CancellationToken.None).ToList();
            var entry = generator.Manifest.Instances[0];
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".dcm");
            images[0].File.Save(path);
            var read = DicomFile.Open(path);
            try
            {
                check($"{label}: file is in that syntax", read.Dataset.InternalTransferSyntax == choice.Syntax, read.Dataset.InternalTransferSyntax.UID.UID);
                check($"{label}: the manifest's file hash is this file", HashOf(path) == entry.FileSha256, null);

                var pixelData = DicomPixelData.Create(read.Dataset);
                check($"{label}: every frame is there", pixelData.NumberOfFrames == entry.FrameSha256.Count, pixelData.NumberOfFrames.ToString());

                // Found by DCMTK: the HTJ2K encoder can leave a fragment with odd length.
                var fragments = read.Dataset.GetDicomItem<DicomFragmentSequence>(DicomTag.PixelData);
                check($"{label}: every fragment has even length", fragments.Fragments.All(f => f.Size % 2 == 0), string.Join(",", fragments.Fragments.Select(f => f.Size)));
                if (fragments.OffsetTable.Count == fragments.Fragments.Count && fragments.Fragments.Count > 1)
                {
                    check($"{label}: the offset table points at each fragment", fragments.OffsetTable[1] == fragments.Fragments[0].Size + 8, fragments.OffsetTable[1].ToString());
                }

                if (choice.Syntax.IsLossy)
                {
                    var ratio = read.Dataset.GetSingleValueOrDefault(DicomTag.LossyImageCompressionRatio, string.Empty);
                    var method = choice.IsVideo ? DCST4.Core.Codecs.OwnCodecs.H264CompressionMethod : choice.Syntax.LossyCompressionMethod;
                    check($"{label}: marked lossy, with method and ratio", Text(read.Dataset, DicomTag.LossyImageCompression) == "01" && Text(read.Dataset, DicomTag.LossyImageCompressionMethod) == method && ratio.Length > 0 && ratio.Length <= 16, ratio);
                    return;
                }

                check($"{label}: Lossy Image Compression stays 00", Text(read.Dataset, DicomTag.LossyImageCompression) == "00", Text(read.Dataset, DicomTag.LossyImageCompression));

                // fo-dicom cannot decode JPEG XL, so the harness's own decoder for what DCST4 writes does.
                var bitsAllocated = read.Dataset.GetSingleValue<int>(DicomTag.BitsAllocated);
                var hashes = DCST4.Core.Codecs.OwnCodecs.IsJpegXl(choice.Syntax)
                    ? fragments.Fragments.Select(f => ManifestHashes.Sha256Hex(JpegXlNative(JpegXlSubsetDecoder.Decode(f.Data), bitsAllocated))).ToList()
                    : ManifestHashes.FrameHashes(read.Clone(DicomTransferSyntax.ExplicitVRLittleEndian).Dataset);
                check($"{label}: decodes to the manifest's frame hashes", hashes.SequenceEqual(entry.FrameSha256), null);

                // Not only PureCodecs' own decoder: this one was written from the standard.
                if (choice.Syntax == DicomTransferSyntax.JPEGProcess14SV1 && samples == 1)
                {
                    var values = new LosslessJpegDecoder(pixelData.GetFrame(0).Data).Decode();
                    check($"{label}: the independent decoder agrees", ManifestHashes.Sha256Hex(ToBytes(values, bits)) == entry.FrameSha256[0], null);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>A decoded JPEG XL image as the bytes of a native frame: samples interleaved, little endian.</summary>
        internal static byte[] JpegXlNative(JpegXlSubsetDecoder.Image image, int bitsAllocated)
        {
            var pixels = image.Width * image.Height;
            var channels = image.Channels.Length;
            var bytes = new byte[pixels * channels * (bitsAllocated / 8)];
            for (var p = 0; p < pixels; p++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var value = image.Channels[c][p];
                    var i = p * channels + c;
                    if (bitsAllocated == 8) bytes[i] = (byte)value;
                    else { bytes[2 * i] = (byte)value; bytes[2 * i + 1] = (byte)(value >> 8); }
                }
            }
            return bytes;
        }

        private static byte[] ToBytes(int[] values, int bits)
        {
            if (bits <= 8) return values.Select(v => (byte)v).ToArray();
            var bytes = new byte[values.Length * 2];
            for (var i = 0; i < values.Length; i++)
            {
                bytes[2 * i] = (byte)values[i];
                bytes[2 * i + 1] = (byte)(values[i] >> 8);
            }
            return bytes;
        }

        // ---------------------------------------------------------------- large object

        private static void TestLargeObject(string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: large object");
            const int rows = 512;
            const int columns = 512;
            const int frames = 200;
            const long objectBytes = (long)rows * columns * frames * 2;
            const long allowedGrowth = 60 * 1024 * 1024;

            var request = new TestDatasetRequest { ProfileId = "tomosynthesis-large", Rows = rows, Columns = columns, Frames = frames };

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var before = GC.GetTotalMemory(true);
            long peak = before;
            var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(false));
                    try { await Task.Delay(5, sampling.Token); } catch (OperationCanceledException) { }
                }
            });

            var path = Path.Combine(folder, "large.dcm");
            var clock = Stopwatch.StartNew();
            var generator = new TestDatasetGenerator(request);
            var image = generator.GetImages(CancellationToken.None).Single();
            image.File.Save(path);
            clock.Stop();
            sampling.Cancel();
            sampler.GetAwaiter().GetResult();

            Console.WriteLine($"  INFO  {objectBytes / 1048576} MB object written in {clock.Elapsed.TotalSeconds:0.0} s, managed heap grew by {(peak - before) / 1048576} MB");
            check("Large object: heap growth stays far below the object's size", peak - before < allowedGrowth, $"{(peak - before) / 1048576} MB of a {objectBytes / 1048576} MB object");

            var file = new FileInfo(path);
            check("Large object: the whole file is written", file.Length > objectBytes, file.Length.ToString());
            check("Large object: the manifest's hash is this file", HashOf(path) == generator.Manifest.Instances[0].FileSha256, null);

            var read = DicomFile.Open(path);
            var length = read.Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Size;
            check("Large object: pixel length", length == objectBytes, length.ToString());
            var pixelData = DicomPixelData.Create(read.Dataset);
            check("Large object: frames can be read back", pixelData.NumberOfFrames == frames && ManifestHashes.Sha256Hex(pixelData.GetFrame(frames / 2).Data) == generator.Manifest.Instances[0].FrameSha256[frames / 2], null);
        }

        // ---------------------------------------------------------------- pipeline

        private static StoreRunOptions SendOptions(int port, TestDatasetGenerator generator) => new StoreRunOptions
        {
            Send = true,
            TransferSyntax = generator.TransferSyntax,
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 30 },
        };

        private static async Task TestPipeline(int port, string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: through the pipeline");
            var expectedRecord = new Dictionary<string, string>
            {
                { "basic-text-sr", "SR DOCUMENT" },
                { "key-object-selection", "KEY OBJECT DOC" },
                { "grayscale-presentation-state", "PRESENTATION" },
                { "encapsulated-pdf", "ENCAP DOC" },
                { "rt-structure-set", "RT STRUCTURE SET" },
                { "segmentation", "IMAGE" },
            };

            foreach (var pair in expectedRecord)
            {
                Scp.Reset();
                var generator = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = pair.Key, Rows = pair.Key == "segmentation" ? 64 : 0, Columns = pair.Key == "segmentation" ? 64 : 0 });
                var summary = await new StoreRun(generator, SendOptions(port, generator)).ExecuteAsync(CancellationToken.None);
                check($"{pair.Key}: sent to the SCP", summary.FatalError == null && summary.Success == generator.ExpectedCount && summary.Failure == 0, summary.FatalError ?? summary.ToString());
                var received = Scp.Received.ToArray();
                check($"{pair.Key}: the SCP received the object itself", received.Any(r => r.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID) == generator.Manifest.Instances.Last().SopInstanceUid), null);

                var target = Path.Combine(folder, "fileset_" + pair.Key);
                var saving = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = pair.Key, Rows = pair.Key == "segmentation" ? 64 : 0, Columns = pair.Key == "segmentation" ? 64 : 0 });
                var saved = await new StoreRun(saving, new StoreRunOptions { Send = false, Save = true, SaveFolder = target, TransferSyntax = saving.TransferSyntax }).ExecuteAsync(CancellationToken.None);
                check($"{pair.Key}: saved with a DICOMDIR", saved.FatalError == null && saved.Saved == saving.ExpectedCount && saved.DicomDirPath != null, saved.FatalError ?? saved.DicomDirError);

                var records = DicomRecordTypes(saved.DicomDirPath);
                check($"{pair.Key}: DICOMDIR has the right record type", records.Contains(pair.Value), string.Join(",", records));

                var savedHashes = Directory.GetFiles(Path.Combine(target, "DICOM"), "*", SearchOption.AllDirectories).Select(HashOf).OrderBy(h => h, StringComparer.Ordinal);
                check($"{pair.Key}: saved files match the manifest's hashes", savedHashes.SequenceEqual(saving.Manifest.Instances.Select(i => i.FileSha256).OrderBy(h => h, StringComparer.Ordinal)), null);
            }

            var acrossPatients = Path.Combine(folder, "fileset_patients");
            var many = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "ct", Patients = 2, StudiesPerPatient = 2, Rows = 64, Columns = 64 });
            var manyRun = await new StoreRun(many, new StoreRunOptions { Send = false, Save = true, SaveFolder = acrossPatients, TransferSyntax = many.TransferSyntax }).ExecuteAsync(CancellationToken.None);
            check("Several patients and studies: no file collisions", manyRun.SaveFailed == 0 && manyRun.Saved == 4, manyRun.ToString());
            check("Several patients: DICOMDIR holds them all", DicomDirectory.Open(manyRun.DicomDirPath).RootDirectoryRecordCollection.Count() == 2, null);

            Scp.Reset();
            var large = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "tomosynthesis-large", Rows = 128, Columns = 128, Frames = 8 });
            var largeRun = await new StoreRun(large, SendOptions(port, large)).ExecuteAsync(CancellationToken.None);
            check("A frame-on-demand object can be sent", largeRun.Success == 1 && largeRun.FatalError == null, largeRun.FatalError ?? largeRun.ToString());
            var arrived = Scp.Received.Single();
            check("The SCP received it with the manifest's pixels", ManifestHashes.FrameHashes(arrived.Dataset).SequenceEqual(large.Manifest.Instances[0].FrameSha256), null);

            Scp.Reset();
            var colour = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "us-multiframe", Rows = 64, Columns = 64, Frames = 2 });
            var colourRun = await new StoreRun(colour, SendOptions(port, colour)).ExecuteAsync(CancellationToken.None);
            var frame = Scp.Received.Single().Frame;
            var red = Enumerable.Range(0, frame.Length / 3).Any(i => frame[3 * i] > 200 && frame[3 * i + 1] < 60 && frame[3 * i + 2] < 60);
            var blue = Enumerable.Range(0, frame.Length / 3).Any(i => frame[3 * i] < 60 && frame[3 * i + 1] < 60 && frame[3 * i + 2] > 200);
            check("RGB image: sent, with red and blue where they should be", colourRun.Success == 1 && red && blue, null);

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.JPEG2000Lossless, DicomTransferSyntax.ExplicitVRLittleEndian };
            var j2k = new TestDatasetGenerator(new TestDatasetRequest { ProfileId = "cr", Rows = 64, Columns = 64, BitsStored = 12, TransferSyntax = DicomTransferSyntax.JPEG2000Lossless });
            if (!j2k.IsUnavailable)
            {
                var j2kRun = await new StoreRun(j2k, SendOptions(port, j2k)).ExecuteAsync(CancellationToken.None);
                var j2kHash = ManifestHashes.FrameHashes(Scp.Received.Single().Dataset.Clone(DicomTransferSyntax.ExplicitVRLittleEndian));
                check("A compressed variant is sent and arrives with the right pixels", j2kRun.Success == 1 && j2kHash.SequenceEqual(j2k.Manifest.Instances[0].FrameSha256), j2kRun.FatalError);
            }
            Scp.Reset();
        }

        private static List<string> DicomRecordTypes(string path)
        {
            var directory = DicomDirectory.Open(path);
            var types = new List<string>();
            foreach (var patient in directory.RootDirectoryRecordCollection)
            {
                foreach (var study in patient.LowerLevelDirectoryRecordCollection)
                {
                    foreach (var series in study.LowerLevelDirectoryRecordCollection)
                    {
                        types.AddRange(series.LowerLevelDirectoryRecordCollection.Select(r => r.GetSingleValue<string>(DicomTag.DirectoryRecordType)));
                    }
                }
            }
            return types;
        }

        // ---------------------------------------------------------------- command line

        private static async Task TestCli(string folder, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: command line");
            var logs = Path.Combine(folder, "logs");
            var target = Path.Combine(folder, "cli");

            var run = await TestSuiteTests.RunCli("generate", "--dataset", "ct", "--out", target, "--log-dir", logs, "--seed", "7", "--rows", "64", "--columns", "64", "--patient-naming", "MarkedTestName");
            check("generate: exit code 0", run.ExitCode == 0, run.Output);

            var manifest = DatasetManifest.Load(Path.Combine(target, "manifest.json"));
            var inProcess = Generate(new TestDatasetRequest { ProfileId = "ct", Seed = 7, Rows = 64, Columns = 64, PatientNaming = TestPatientNaming.MarkedTestName });
            check("generate: another process gives the same manifest as this one", manifest.ToJson() == inProcess.Manifest.ToJson(), null);
            check("generate: DICOMDIR is written", File.Exists(Path.Combine(target, "DICOMDIR")), null);

            var saved = Directory.GetFiles(Path.Combine(target, "DICOM"), "*", SearchOption.AllDirectories).Single();
            check("generate: marked name from the option", MarkedName.IsMatch(Text(DicomFile.Open(saved).Dataset, DicomTag.PatientName)), null);

            var again = await TestSuiteTests.RunCli("generate", "--dataset", "ct", "--out", target, "--log-dir", logs);
            check("generate: an earlier manifest is not overwritten", again.ExitCode == 2 && again.Output.Contains("already exists"), again.Output);

            var unknown = await TestSuiteTests.RunCli("generate", "--dataset", "nope", "--out", Path.Combine(folder, "cli2"), "--log-dir", logs);
            check("generate: unknown dataset gives exit code 2", unknown.ExitCode == 2 && unknown.Output.Contains("nope"), unknown.Output);

            var badNaming = await TestSuiteTests.RunCli("generate", "--dataset", "ct", "--out", Path.Combine(folder, "cli3"), "--patient-naming", "Bad");
            check("generate: unknown naming gives exit code 2", badNaming.ExitCode == 2 && badNaming.Output.Contains("Bad"), badNaming.Output);

            var codec = await TestSuiteTests.RunCli("generate", "--dataset", "cr", "--out", Path.Combine(folder, "cli4"), "--log-dir", logs, "--bits", "12", "--rows", "64", "--columns", "64", "--transfer-syntax", "jpeg-baseline");
            check("generate: an unavailable codec is reported and is not an error", codec.ExitCode == 0 && codec.Output.Contains("Unavailable") && File.Exists(Path.Combine(folder, "cli4", "manifest.json")), codec.Output);

            var list = await TestSuiteTests.RunCli("list-datasets");
            check("list-datasets: every profile is listed", list.ExitCode == 0 && DatasetProfileCatalog.All.All(p => list.Output.Contains(p.Id)), list.Output);

            var codecs = await TestSuiteTests.RunCli("list-codecs", "--log-dir", logs);
            check("list-codecs: every syntax is listed", codecs.ExitCode == 0 && CompressionCatalog.Compressed.All(c => codecs.Output.Contains(c.Id)), codecs.Output);

            var help = await TestSuiteTests.RunCli("--help");
            check("Usage names the new commands", help.Output.Contains("generate") && help.Output.Contains("--patient-naming"), help.Output);
        }

        // ---------------------------------------------------------------- boundaries

        private static void TestBoundaries(Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest data: boundaries");
            var repository = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\..\.."));
            var generationFiles = new[] { @"src\DCST4.Core\Generation", @"src\DCST4.Core\TestData" }
                .SelectMany(d => Directory.GetFiles(Path.Combine(repository, d), "*.cs", SearchOption.AllDirectories))
                .ToList();
            var offenders = generationFiles.Where(f => File.ReadAllText(f).Contains("DCST4.Core.Network")).ToList();

            check("The code was found to be checked", generationFiles.Count > 10, generationFiles.Count.ToString());
            check("Generation and test data do not depend on Network", offenders.Count == 0, string.Join(" ", offenders));
            check("Core has no WPF reference", !File.ReadAllText(Path.Combine(repository, @"src\DCST4.Core\DCST4.Core.csproj")).Contains("UseWPF"), null);

            var captured = new CaptureNaming();
            new TestRunner().RunAsync(new TargetProfile { Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = 1, CallingAe = "A", CalledAe = "B" } }, new ITestCase[] { captured }, CancellationToken.None, TestPatientNaming.RegistrySwedishName).GetAwaiter().GetResult();
            check("A test run passes a naming other than the default on to its tests", captured.Seen == TestPatientNaming.RegistrySwedishName, captured.Seen.ToString());

            var noPixels = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian)
            {
                { DicomTag.SOPClassUID, DicomUID.BasicTextSRStorage },
                { DicomTag.SOPInstanceUID, "1.2.3" },
            };
            var kept = TransferSyntaxConverter.Convert(new DicomFile(noPixels), DicomTransferSyntax.JPEGProcess14SV1);
            check("A JPEG run leaves an object without pixels uncompressed", kept.Dataset.InternalTransferSyntax == DicomTransferSyntax.ExplicitVRLittleEndian, kept.Dataset.InternalTransferSyntax.UID.UID);
        }
    }
}
