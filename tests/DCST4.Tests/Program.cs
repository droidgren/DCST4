using DCST4.Core;
using DCST4.Core.Generation;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>
    /// Console test harness in the same style as DMWLT4's: runs real code against a mock SCP and prints PASS or FAIL.
    /// Exit code 0 = all checks passed. "render &lt;folder&gt;" instead writes sample images as PNG for a visual check.
    /// </summary>
    internal static class Program
    {
        private const int Port = 41212;
        private const int ClosedPort = 41213;
        private const int TlsPort = 41214;

        private static int _pass;
        private static int _fail;
        private static readonly CapturingSink LogSink = new CapturingSink();

        private sealed class CapturingSink : ILogEventSink
        {
            public readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
            public void Emit(LogEvent logEvent) => Lines.Enqueue(logEvent.Level + " " + logEvent.RenderMessage());
            public void Clear() { while (Lines.TryDequeue(out _)) { } }
        }

        private static void Check(string name, bool ok, string detail = null)
        {
            if (ok) _pass++; else _fail++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (!ok && detail != null ? "  -> " + detail : string.Empty));
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(LogSink).CreateLogger();
            DicomSetup.Initialize();

            if (args.Length == 2 && args[0] == "render")
            {
                RenderSamples(args[1]);
                return 0;
            }

            if (args.Length == 2 && args[0] == "matrix")
            {
                WriteMatrix(args[1]);
                return 0;
            }

            if (args.Length == 2 && args[0] == "fileset")
            {
                WriteFileSet(args[1]).GetAwaiter().GetResult();
                return 0;
            }

            if (args.Length >= 8 && args[0] == "send")
            {
                return SendTo(args);
            }

            if (args.Length >= 2 && args[0] == "scp")
            {
                RunStandaloneScp(int.Parse(args[1], CultureInfo.InvariantCulture), args.Length > 2 && args[2] == "mixed");
                return 0;
            }

            var temp = Path.Combine(Path.GetTempPath(), "DCST4Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                TestPersonnummer();
                TestNamesAndIdentifiers();
                TestCharacterSets(temp);
                TestValidation();
                TestGeneration();
                TestPixelDataSizes(temp);
                TestManualOverrides();
                TestJpegRoundTrip();

                using (DicomServerFactory.Create<MockStorageScp>(Port))
                {
                    TestConnectivity().GetAwaiter().GetResult();
                    TestSendingSyntaxes().GetAwaiter().GetResult();
                    TestStatuses().GetAwaiter().GetResult();
                    TestAssociationBehaviour().GetAwaiter().GetResult();
                    TestFaults().GetAwaiter().GetResult();
                    TestSaving(temp).GetAwaiter().GetResult();
                    ViewModelTests.Run(Port, (name, ok, detail) => Check(name, ok, detail));
                }
                TestTls().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  FAIL  harness exception: " + ex);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }

            Console.WriteLine($"\n{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- helpers

        private static ModalityProfile Modality(string code) => ModalityCatalog.Find(code);

        private static ExamRequest MakeRequest(string modality = "CT", int series = 1, int images = 1, int rows = 64, int columns = 64, int bits = 0,
            string charset = DicomCharacterSets.Latin1, IdentityOverrides overrides = null)
        {
            var m = Modality(modality);
            return new ExamRequest
            {
                Modality = m,
                Preset = m.Presets[0],
                Identity = new IdentityGenerator().Generate(overrides, m, m.Presets[0]),
                SeriesCount = series,
                ImagesPerSeries = images,
                Rows = rows,
                Columns = columns,
                BitsStored = bits > 0 ? bits : m.DefaultBitDepth,
                CharacterSet = charset,
            };
        }

        private static StoreRunOptions SendOptions(DicomTransferSyntax syntax = null, int port = Port, int responseTimeout = 30) => new StoreRunOptions
        {
            Send = true,
            TransferSyntax = syntax ?? DicomTransferSyntax.ExplicitVRLittleEndian,
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = responseTimeout },
        };

        private static async Task<RunSummary> Run(ExamRequest request, StoreRunOptions options, CancellationToken token = default(CancellationToken), Action<StoreResult> onResult = null)
        {
            var run = new StoreRun(new ExamGenerator(request), options);
            if (onResult != null) run.ImageCompleted += onResult;
            return await run.ExecuteAsync(token);
        }

        private static string Text(DicomDataset ds, DicomTag tag) => ds.GetSingleValueOrDefault(tag, string.Empty);

        // ---------------------------------------------------------------- personnummer

        private static void TestPersonnummer()
        {
            Console.WriteLine("\nTest personnummer");
            var registry = TestPersonnummerRegistry.Default;
            Check("Embedded list loaded (42,939 unique numbers as downloaded)", registry.Count == 42939, registry.Count.ToString());
            Check("No row rejected for bad date or check digit", registry.RejectedCount == 0, registry.RejectedCount.ToString());
            Check("Source description names Skatteverket", (registry.SourceDescription ?? string.Empty).Contains("Skatteverket"), registry.SourceDescription);

            var today = new DateTime(2026, 11, 6);
            var random = new Random(12345);
            var notListed = 0; var future = 0; var ageWrong = 0; var sexWrong = 0; var men = 0;
            for (var i = 0; i < 10000; i++)
            {
                var p = registry.Pick(random, today, 0, 100);
                if (!registry.Contains(p.Value)) notListed++;
                if (p.BirthDate > today) future++;
                var age = TestPersonnummerRegistry.AgeOn(p.BirthDate, today);
                if (age < 0 || age > 100) ageWrong++;
                var expectedSex = (p.Value[10] - '0') % 2 == 1 ? "M" : "F";
                if (p.Sex != expectedSex) sexWrong++;
                if (p.Sex == "M") men++;
            }
            Check("10,000 picks: all are on the list", notListed == 0, notListed.ToString());
            Check("10,000 picks: none has a future birth date", future == 0, future.ToString());
            Check("10,000 picks: all aged 0-100", ageWrong == 0, ageWrong.ToString());
            Check("10,000 picks: sex matches second-to-last digit", sexWrong == 0, sexWrong.ToString());
            Check("10,000 picks: both sexes occur", men > 3000 && men < 7000, men.ToString());

            var limited = registry.Pick(new Random(1), today, 30, 40);
            var limitedAge = TestPersonnummerRegistry.AgeOn(limited.BirthDate, today);
            Check("Age range is honoured", limitedAge >= 30 && limitedAge <= 40, limitedAge.ToString());

            Check("Known list number validates", Personnummer.TryParse("189001019802", out var known) && known.Sex == "F" && known.BirthDate == new DateTime(1890, 1, 1));
            Check("Bad check digit is rejected", !Personnummer.TryParse("189001019803", out _));
            Check("Invalid date is rejected", !Personnummer.TryParse("199913019802", out _));
            Check("Registry rejects bad rows on load", new TestPersonnummerRegistry(new[] { "189001019802", "189001019803", "abc" }).RejectedCount == 2);

            var thrown = false;
            try { new TestPersonnummerRegistry(new[] { "189001019802" }).Pick(new Random(), today, 0, 100); }
            catch (InvalidOperationException) { thrown = true; }
            Check("No eligible person gives an error, never a made-up number", thrown);
        }

        private static void TestNamesAndIdentifiers()
        {
            Console.WriteLine("\nNames and identifiers");
            var random = new Random(4711);
            var swedish = 0;
            var badForm = 0;
            const int count = 10000;
            for (var i = 0; i < count; i++)
            {
                var name = SwedishNames.Pick(random, i % 2 == 0 ? "M" : "F");
                if (SwedishNames.ContainsSwedishCharacter(name)) swedish++;
                if (name.Split('^').Length != 2) badForm++;
            }
            var share = swedish / (double)count;
            Check("About 40% of names contain å, ä or ö", Math.Abs(share - SwedishNames.SwedishCharacterShare) < 0.03, share.ToString("0.000"));
            Check("Names have the form Surname^Firstname", badForm == 0, badForm.ToString());

            var maleFirstNames = new HashSet<string>();
            for (var i = 0; i < 2000; i++) maleFirstNames.Add(SwedishNames.Pick(random, "M").Split('^')[1]);
            Check("First name follows sex", !maleFirstNames.Contains("Anna") && !maleFirstNames.Contains("Åsa") && maleFirstNames.Contains("Erik"));

            var date = new DateTime(2026, 11, 6);
            var accession = IdentifierGenerator.AccessionNumber(random, date);
            Check("Accession Number fits SH", accession.Length <= 16 && accession.StartsWith("DC261106"), accession);
            Check("Study ID fits SH", IdentifierGenerator.StudyId(random).Length <= 16);
            Check("Generated UID is valid", ExamValidator.IsValidUid(IdentifierGenerator.Uid()));

            var identity = new IdentityGenerator(random: new Random(5)).Generate(null, Modality("CT"), Modality("CT").Presets[0]);
            Personnummer.TryParse(identity.PatientId, out var parsed);
            Check("Auto Patient ID is on the list", TestPersonnummerRegistry.Default.Contains(identity.PatientId), identity.PatientId);
            Check("Auto birth date comes from the number", parsed != null && identity.PatientBirthDate == parsed.BirthDate.ToString("yyyyMMdd"), identity.PatientBirthDate);
            Check("Auto sex comes from the number", parsed != null && identity.PatientSex == parsed.Sex, identity.PatientSex);

            Check("Patient age in years", DicomImageBuilder.PatientAge("19800615", new DateTime(2026, 11, 6)) == "046Y");
            Check("Patient age in months", DicomImageBuilder.PatientAge("20260315", new DateTime(2026, 11, 6)) == "007M");
            Check("Patient age in days", DicomImageBuilder.PatientAge("20261101", new DateTime(2026, 11, 6)) == "005D");
            Check("Unknown birth date gives no age", DicomImageBuilder.PatientAge("", new DateTime(2026, 11, 6)) == null);
        }

        // ---------------------------------------------------------------- character sets

        private static void TestCharacterSets(string temp)
        {
            Console.WriteLine("\nSpecific Character Set");
            var overrides = new IdentityOverrides { PatientName = "Åström^Märta" };

            var latin1 = SavedBytes(MakeRequest(charset: DicomCharacterSets.Latin1, overrides: overrides), Path.Combine(temp, "latin1.dcm"), out var latin1Dataset);
            var utf8 = SavedBytes(MakeRequest(charset: DicomCharacterSets.Utf8, overrides: overrides), Path.Combine(temp, "utf8.dcm"), out var utf8Dataset);

            Check("ISO_IR 100: tag written", Text(latin1Dataset, DicomTag.SpecificCharacterSet) == "ISO_IR 100");
            Check("ISO_IR 192: tag written", Text(utf8Dataset, DicomTag.SpecificCharacterSet) == "ISO_IR 192");
            Check("ISO_IR 100: Å is byte C5 in the file", Contains(latin1, new byte[] { 0xC5, (byte)'s', (byte)'t', (byte)'r' }));
            Check("ISO_IR 100: not written as UTF-8", !Contains(latin1, new byte[] { 0xC3, 0x85, (byte)'s', (byte)'t', (byte)'r' }));
            Check("ISO_IR 192: Å is bytes C3 85 in the file", Contains(utf8, new byte[] { 0xC3, 0x85, (byte)'s', (byte)'t', (byte)'r' }));
            Check("ISO_IR 100: name reads back unchanged", Text(latin1Dataset, DicomTag.PatientName) == "Åström^Märta", Text(latin1Dataset, DicomTag.PatientName));
            Check("ISO_IR 192: name reads back unchanged", Text(utf8Dataset, DicomTag.PatientName) == "Åström^Märta", Text(utf8Dataset, DicomTag.PatientName));
        }

        private static byte[] SavedBytes(ExamRequest request, string path, out DicomDataset readBack)
        {
            var image = new ExamGenerator(request).GetImages(CancellationToken.None).First();
            image.File.Save(path);
            readBack = DicomFile.Open(path).Dataset;
            return File.ReadAllBytes(path);
        }

        private static bool Contains(byte[] data, byte[] pattern)
        {
            for (var i = 0; i <= data.Length - pattern.Length; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
                if (match) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- validation

        private static void TestValidation()
        {
            Console.WriteLine("\nValidation");
            Func<IdentityOverrides, int, int, string, List<string>> errors = (o, series, images, charset) =>
                ExamValidator.Validate(MakeRequest(series: series, images: images, charset: charset, overrides: o));

            Check("Default exam is valid", errors(null, 1, 1, DicomCharacterSets.Latin1).Count == 0, string.Join("; ", errors(null, 1, 1, DicomCharacterSets.Latin1)));

            var polish = new IdentityOverrides { PatientName = "Łukasz^Żak" };
            Check("ISO_IR 100 blocks a name it cannot represent", errors(polish, 1, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("Patient Name") && e.Contains("ISO_IR 100")));
            Check("ISO_IR 192 accepts the same name", errors(polish, 1, 1, DicomCharacterSets.Utf8).Count == 0);

            Check("Invalid UID is blocked and named", errors(new IdentityOverrides { StudyInstanceUid = "1.2.abc" }, 1, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("Study Instance UID")));
            Check("UID with leading zero is blocked", !ExamValidator.IsValidUid("1.02.3"));
            Check("Over-long UID is blocked", !ExamValidator.IsValidUid(new string('1', 65)));
            Check("Over-long Accession Number is blocked", errors(new IdentityOverrides { AccessionNumber = new string('A', 17) }, 1, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("Accession Number")));
            Check("Backslash in text is blocked", errors(new IdentityOverrides { PatientId = "12\\34" }, 1, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("backslash")));
            Check("Invalid birth date is blocked", errors(new IdentityOverrides { PatientBirthDate = "19991399" }, 1, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("Birth Date")));
            Check("Invalid sex is blocked", errors(new IdentityOverrides { PatientSex = "X" }, 1, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("Sex")));

            var uid = DicomUID.Generate().UID;
            Check("Manual Series UID with one series is allowed", errors(new IdentityOverrides { SeriesInstanceUid = uid }, 1, 5, DicomCharacterSets.Latin1).Count == 0);
            Check("Manual Series UID with two series is blocked", errors(new IdentityOverrides { SeriesInstanceUid = uid }, 2, 1, DicomCharacterSets.Latin1).Any(e => e.Contains("Series Instance UID")));
            Check("Manual SOP UID with one image is allowed", errors(new IdentityOverrides { SopInstanceUid = uid }, 1, 1, DicomCharacterSets.Latin1).Count == 0);
            Check("Manual SOP UID with two images is blocked", errors(new IdentityOverrides { SopInstanceUid = uid }, 1, 2, DicomCharacterSets.Latin1).Any(e => e.Contains("SOP Instance UID")));

            Check("CT at 8 bits is blocked", ExamValidator.Validate(MakeRequest("CT", bits: 8)).Any(e => e.Contains("does not allow")));
            Check("MR at 8 bits is blocked", ExamValidator.Validate(MakeRequest("MR", bits: 8)).Any(e => e.Contains("does not allow")));
            Check("CR at 8 bits is allowed", ExamValidator.Validate(MakeRequest("CR", bits: 8)).Count == 0);
            Check("Matrix below the limit is blocked", ExamValidator.Validate(MakeRequest(rows: 10)).Any(e => e.Contains("Rows")));
            Check("Matrix above the limit is blocked", ExamValidator.Validate(MakeRequest(columns: 5000)).Any(e => e.Contains("Columns")));

            var manualId = MakeRequest(overrides: new IdentityOverrides { PatientId = "NOT-ON-THE-LIST" });
            Check("Patient ID outside the test list gives a warning", ExamValidator.Warnings(manualId).Any(w => w.Contains("Skatteverket")));
            var listedId = MakeRequest(overrides: new IdentityOverrides { PatientId = "189001019802" });
            Check("Patient ID on the test list gives no warning", ExamValidator.Warnings(listedId).Count == 0);

            Check("AE title over 16 characters is blocked", new DicomEndpoint { Host = "h", Port = 104, CallingAe = new string('A', 17), CalledAe = "B" }.Validate() != null);
            Check("Empty host is blocked", new DicomEndpoint { Host = " ", Port = 104, CallingAe = "A", CalledAe = "B" }.Validate() != null);
            Check("Invalid port is blocked", new DicomEndpoint { Host = "h", Port = 70000, CallingAe = "A", CalledAe = "B" }.Validate() != null);
            Check("Valid endpoint passes", new DicomEndpoint { Host = "h", Port = 104, CallingAe = "A", CalledAe = "B" }.Validate() == null);
            Check("Neither send nor save is blocked", new StoreRunOptions { Send = false, Save = false }.Validate() != null);
            Check("Save without folder is blocked", new StoreRunOptions { Send = false, Save = true }.Validate() != null);
        }

        // ---------------------------------------------------------------- generation

        private static readonly DicomTag[] CommonRequired =
        {
            DicomTag.SpecificCharacterSet, DicomTag.SOPClassUID, DicomTag.SOPInstanceUID, DicomTag.PatientName, DicomTag.PatientID,
            DicomTag.PatientBirthDate, DicomTag.PatientSex, DicomTag.StudyInstanceUID, DicomTag.StudyDate, DicomTag.StudyTime,
            DicomTag.ReferringPhysicianName, DicomTag.StudyID, DicomTag.AccessionNumber, DicomTag.Modality, DicomTag.SeriesInstanceUID,
            DicomTag.SeriesNumber, DicomTag.Manufacturer, DicomTag.InstanceNumber, DicomTag.ContentDate, DicomTag.ContentTime,
            DicomTag.ImageType, DicomTag.SamplesPerPixel, DicomTag.PhotometricInterpretation, DicomTag.Rows, DicomTag.Columns,
            DicomTag.BitsAllocated, DicomTag.BitsStored, DicomTag.HighBit, DicomTag.PixelRepresentation, DicomTag.PixelData,
            DicomTag.BurnedInAnnotation, DicomTag.ImageComments, DicomTag.WindowCenter, DicomTag.WindowWidth,
        };

        private static void TestGeneration()
        {
            Console.WriteLine("\nGeneration");

            var required = new Dictionary<string, DicomTag[]>
            {
                { "CT", new[] { DicomTag.FrameOfReferenceUID, DicomTag.PatientPosition, DicomTag.ImagePositionPatient, DicomTag.ImageOrientationPatient, DicomTag.PixelSpacing, DicomTag.SliceThickness, DicomTag.KVP, DicomTag.AcquisitionNumber, DicomTag.RescaleIntercept, DicomTag.RescaleSlope, DicomTag.RescaleType, DicomTag.ConvolutionKernel } },
                { "MR", new[] { DicomTag.FrameOfReferenceUID, DicomTag.PatientPosition, DicomTag.ImagePositionPatient, DicomTag.ImageOrientationPatient, DicomTag.PixelSpacing, DicomTag.SliceThickness, DicomTag.ScanningSequence, DicomTag.SequenceVariant, DicomTag.ScanOptions, DicomTag.MRAcquisitionType, DicomTag.RepetitionTime, DicomTag.EchoTime, DicomTag.EchoTrainLength, DicomTag.MagneticFieldStrength, DicomTag.ImagingFrequency } },
                { "CR", new[] { DicomTag.BodyPartExamined, DicomTag.ViewPosition, DicomTag.ImagerPixelSpacing, DicomTag.PatientOrientation } },
                { "DX", new[] { DicomTag.PresentationIntentType, DicomTag.PresentationLUTShape, DicomTag.PixelIntensityRelationship, DicomTag.PixelIntensityRelationshipSign, DicomTag.RescaleIntercept, DicomTag.RescaleSlope, DicomTag.RescaleType, DicomTag.DetectorType, DicomTag.ImagerPixelSpacing, DicomTag.ImageLaterality, DicomTag.AnatomicRegionSequence, DicomTag.PatientOrientation, DicomTag.LossyImageCompression } },
                { "US", new[] { DicomTag.BodyPartExamined, DicomTag.PatientOrientation, DicomTag.TransducerData, DicomTag.TransducerType, DicomTag.SequenceOfUltrasoundRegions, DicomTag.LossyImageCompression } },
            };

            foreach (var modality in ModalityCatalog.All)
            {
                foreach (var preset in modality.Presets)
                {
                    var request = MakeRequest(modality.Code);
                    request.Preset = preset;
                    var image = new ExamGenerator(request).GetImages(CancellationToken.None).First();
                    var missing = CommonRequired.Concat(required[modality.Code]).Where(t => !image.File.Dataset.Contains(t)).Select(t => t.ToString()).ToList();
                    Check($"{modality.Code} {preset.Name}: all required tags present", missing.Count == 0, string.Join(" ", missing));
                }
                Check($"{modality.Code}: SOP class is correct", new ExamGenerator(MakeRequest(modality.Code)).GetImages(CancellationToken.None).First().SopClassUid == modality.SopClass.UID);
            }

            TestUltrasound();

            var dataset = new ExamGenerator(MakeRequest("CT")).GetImages(CancellationToken.None).First().File.Dataset;
            Check("BurnedInAnnotation is YES", Text(dataset, DicomTag.BurnedInAnnotation) == "YES");
            Check("Image comment marks it as a test image", Text(dataset, DicomTag.ImageComments).Contains("TEST IMAGE"));

            var stack = new ExamGenerator(MakeRequest("CT", series: 2, images: 5)).GetImages(CancellationToken.None).Select(i => i.File.Dataset).ToList();
            Check("2 series x 5 images gives 10 images", stack.Count == 10, stack.Count.ToString());
            Check("All SOP Instance UIDs are unique", stack.Select(d => Text(d, DicomTag.SOPInstanceUID)).Distinct().Count() == 10);
            Check("One Study Instance UID", stack.Select(d => Text(d, DicomTag.StudyInstanceUID)).Distinct().Count() == 1);
            Check("Two Series Instance UIDs", stack.Select(d => Text(d, DicomTag.SeriesInstanceUID)).Distinct().Count() == 2);
            Check("Series Numbers are 1 and 2", stack.Select(d => Text(d, DicomTag.SeriesNumber)).Distinct().SequenceEqual(new[] { "1", "2" }));
            Check("Series keep their own Frame of Reference", stack.Select(d => Text(d, DicomTag.FrameOfReferenceUID)).Distinct().Count() == 2);
            Check("Accession Number stays the same", stack.Select(d => Text(d, DicomTag.AccessionNumber)).Distinct().Count() == 1);
            Check("Instance Numbers restart per series", stack.Take(5).Select(d => Text(d, DicomTag.InstanceNumber)).SequenceEqual(new[] { "1", "2", "3", "4", "5" }) && Text(stack[5], DicomTag.InstanceNumber) == "1");
            Check("Each series has its own description", Text(stack[0], DicomTag.SeriesDescription) != Text(stack[5], DicomTag.SeriesDescription));

            var positions = stack.Take(5).Select(d => d.GetValues<decimal>(DicomTag.ImagePositionPatient)).ToList();
            var spacing = (double)Modality("CT").Presets[0].SliceThicknessMm;
            var evenly = Enumerable.Range(1, 4).All(i => Math.Abs((double)(positions[i][2] - positions[i - 1][2]) - spacing) < 0.0001);
            Check("Slice positions advance evenly by the slice thickness", evenly, string.Join(" ", positions.Select(p => p[2])));

            var locations = stack.Take(5).Select(d => d.GetSingleValue<decimal>(DicomTag.SliceLocation)).ToList();
            Check("Slice Location follows the position", Enumerable.Range(0, 5).All(i => locations[i] == positions[i][2]), string.Join(" ", locations));

            var knee = MakeRequest("MR");
            knee.Preset = Modality("MR").Presets.First(p => p.Name == "Knee");
            knee.ImagesPerSeries = 3;
            var kneeImages = new ExamGenerator(knee).GetImages(CancellationToken.None).Select(i => i.File.Dataset).ToList();
            var kneePositions = kneeImages.Select(d => d.GetValues<decimal>(DicomTag.ImagePositionPatient)).ToList();
            Check("Sagittal stack moves along X, not Z", kneePositions[0][0] != kneePositions[1][0] && kneePositions[0][2] == kneePositions[1][2]);
            Check("Sagittal orientation is written", kneeImages[0].GetValues<decimal>(DicomTag.ImageOrientationPatient).SequenceEqual(new decimal[] { 0, 1, 0, 0, 0, -1 }));

            var clockTime = new DateTime(2026, 11, 6, 12, 0, 0);
            var auto = MakeRequest("MR", images: 11);
            var autoImages = new ExamGenerator(auto, () => clockTime).GetImages(CancellationToken.None).Select(i => i.File.Dataset).ToList();
            var lastContent = DateTime.ParseExact(Text(autoImages[10], DicomTag.ContentDate) + Text(autoImages[10], DicomTag.ContentTime), "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture);
            var firstContent = DateTime.ParseExact(Text(autoImages[0], DicomTag.ContentDate) + Text(autoImages[0], DicomTag.ContentTime), "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture);
            Check("Auto exam ends now, no future timestamp", lastContent == clockTime, lastContent.ToString("O"));
            Check("Content Time advances within a series", lastContent - firstContent == TimeSpan.FromSeconds(10), (lastContent - firstContent).ToString());
            Check("Study Time is the exam start", Text(autoImages[0], DicomTag.StudyTime) == Text(autoImages[0], DicomTag.ContentTime));

            // Lazy generation: a long stack must not be produced up front.
            var produced = 0;
            var lazy = new ExamGenerator(MakeRequest("CT", images: 1000)).GetImages(CancellationToken.None);
            foreach (var image in lazy)
            {
                produced++;
                if (produced == 2) break;
            }
            Check("Images are generated lazily", produced == 2);

            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try { new ExamGenerator(MakeRequest("CT")).GetImages(cancelled.Token).First(); }
            catch (OperationCanceledException) { threw = true; }
            Check("Generation stops on cancel", threw);
        }

        private static void TestUltrasound()
        {
            var us = Modality("US");
            Check("US: only 8 bits, as the IOD requires for MONOCHROME2", us.BitDepths.SequenceEqual(new[] { 8 }) && us.DefaultBitDepth == 8);

            var request = MakeRequest("US", images: 2, rows: 600, columns: 800);
            request.Preset = us.Presets.First(p => p.Name == "Abdomen");
            var images = new ExamGenerator(request).GetImages(CancellationToken.None).Select(i => i.File.Dataset).ToList();
            var ds = images[0];
            Check("US: SOP class is Ultrasound Image Storage", Text(ds, DicomTag.SOPClassUID) == "1.2.840.10008.5.1.4.1.1.6.1", Text(ds, DicomTag.SOPClassUID));
            Check("US: Modality", Text(ds, DicomTag.Modality) == "US");
            Check("US: pixel format", Text(ds, DicomTag.PhotometricInterpretation) == "MONOCHROME2" && ds.GetSingleValue<ushort>(DicomTag.SamplesPerPixel) == 1
                && ds.GetSingleValue<ushort>(DicomTag.BitsAllocated) == 8 && ds.GetSingleValue<ushort>(DicomTag.BitsStored) == 8 && ds.GetSingleValue<ushort>(DicomTag.HighBit) == 7);
            Check("US: Image Type names the application and 2D imaging", ds.GetValues<string>(DicomTag.ImageType).SequenceEqual(new[] { "ORIGINAL", "PRIMARY", "ABDOMINAL", "0001" }), string.Join("\\", ds.GetValues<string>(DicomTag.ImageType)));
            Check("US: no CT or MR geometry", !ds.Contains(DicomTag.FrameOfReferenceUID) && !ds.Contains(DicomTag.ImagePositionPatient) && !ds.Contains(DicomTag.PixelSpacing));

            var region = ds.GetSequence(DicomTag.SequenceOfUltrasoundRegions).Items.Single();
            var expectedDelta = request.Preset.ScanDepthCm / request.Rows;
            Check("US: calibrated region in centimeters", region.GetSingleValue<ushort>(DicomTag.PhysicalUnitsXDirection) == 3 && region.GetSingleValue<ushort>(DicomTag.PhysicalUnitsYDirection) == 3
                && Math.Abs(region.GetSingleValue<double>(DicomTag.PhysicalDeltaX) - expectedDelta) < 1e-9 && Math.Abs(region.GetSingleValue<double>(DicomTag.PhysicalDeltaY) - expectedDelta) < 1e-9);
            Check("US: region covers the image", region.GetSingleValue<uint>(DicomTag.RegionLocationMaxX1) == 799 && region.GetSingleValue<uint>(DicomTag.RegionLocationMaxY1) == 599);
            Check("US: exam type sets the transducer", Text(ds, DicomTag.TransducerType) == "CURVED LINEAR");
            Check("US: 16 bits is blocked", ExamValidator.Validate(MakeRequest("US", bits: 16)).Any(e => e.Contains("does not allow")));
            Check("US: default is valid", ExamValidator.Validate(MakeRequest("US")).Count == 0, string.Join("; ", ExamValidator.Validate(MakeRequest("US"))));
        }

        private static void TestPixelDataSizes(string temp)
        {
            Console.WriteLine("\nMatrix and bit depth");
            foreach (var modality in ModalityCatalog.All)
            {
                foreach (var bits in modality.BitDepths)
                {
                    foreach (var size in new[] { new[] { 64, 64 }, new[] { 65, 65 }, new[] { 128, 256 } })
                    {
                        var request = MakeRequest(modality.Code, rows: size[0], columns: size[1], bits: bits);
                        var image = new ExamGenerator(request).GetImages(CancellationToken.None).First();
                        var path = Path.Combine(temp, $"size_{modality.Code}_{bits}_{size[0]}x{size[1]}.dcm");
                        image.File.Save(path);

                        var read = DicomFile.Open(path);
                        var pixelData = DicomPixelData.Create(read.Dataset);
                        var bytesPerPixel = bits <= 8 ? 1 : 2;
                        var expected = size[0] * size[1] * bytesPerPixel;
                        var actual = pixelData.GetFrame(0).Size;
                        var evenFile = new FileInfo(path).Length % 2 == 0;
                        var tagsOk = pixelData.BitsStored == bits && pixelData.BitsAllocated == (bits <= 8 ? 8 : 16) && pixelData.HighBit == bits - 1 && pixelData.Width == size[1] && pixelData.Height == size[0];
                        Check($"{modality.Code} {bits} bits {size[0]}x{size[1]}: size, tags, even length",
                            actual >= expected && actual <= expected + 1 && tagsOk && evenFile, $"frame {actual}, expected {expected}, even {evenFile}");
                    }
                }
            }

            var ct = new ExamGenerator(MakeRequest("CT", bits: 16)).GetImages(CancellationToken.None).First().File.Dataset;
            var frame = DicomPixelData.Create(ct).GetFrame(0).Data;
            var max = 0;
            for (var i = 0; i < frame.Length; i += 2) max = Math.Max(max, frame[i] | frame[i + 1] << 8);
            Check("CT at 16 bits stays within the Hounsfield range", max <= 4095, max.ToString());

            var low = ct.GetSingleValue<double>(DicomTag.WindowCenter) - ct.GetSingleValue<double>(DicomTag.WindowWidth) / 2;
            var high = ct.GetSingleValue<double>(DicomTag.WindowCenter) + ct.GetSingleValue<double>(DicomTag.WindowWidth) / 2;
            var intercept = ct.GetSingleValue<double>(DicomTag.RescaleIntercept);
            Check("CT window covers the pixel range after rescale", low <= intercept && high >= max + intercept, $"{low}..{high}, max {max + intercept}");

            var cr = new ExamGenerator(MakeRequest("CR", bits: 8)).GetImages(CancellationToken.None).First().File.Dataset;
            Check("8-bit image window covers the full range", cr.GetSingleValue<double>(DicomTag.WindowWidth) >= 200, cr.GetSingleValue<double>(DicomTag.WindowWidth).ToString());
            var values = DicomPixelData.Create(cr).GetFrame(0).Data;
            Check("Text is visible: image holds both black and white", values.Any(v => v >= 250) && values.Any(v => v <= 5));
        }

        private static void TestManualOverrides()
        {
            Console.WriteLine("\nManual values");
            var uid1 = DicomUID.Generate().UID;
            var uid2 = DicomUID.Generate().UID;
            var uid3 = DicomUID.Generate().UID;
            var manual = new IdentityOverrides
            {
                PatientName = "Testsson^Örjan",
                PatientId = "PID-123",
                PatientBirthDate = "19700102",
                PatientSex = "O",
                AccessionNumber = "ACC-1",
                StudyId = "STUDY-1",
                StudyInstanceUid = uid1,
                SeriesInstanceUid = uid2,
                SopInstanceUid = uid3,
                StudyDateTime = new DateTime(2020, 2, 3, 4, 5, 6),
                ReferringPhysicianName = "Doktor^Åsa",
                StudyDescription = "Manual study",
                SeriesDescription = "Manual series",
                InstitutionName = "Manual hospital",
                StationName = "STATION1",
            };
            var request = MakeRequest(overrides: manual);
            Check("Manual exam is valid", ExamValidator.Validate(request).Count == 0, string.Join("; ", ExamValidator.Validate(request)));
            var ds = new ExamGenerator(request).GetImages(CancellationToken.None).First().File.Dataset;

            var expected = new Dictionary<DicomTag, string>
            {
                { DicomTag.PatientName, "Testsson^Örjan" }, { DicomTag.PatientID, "PID-123" }, { DicomTag.PatientBirthDate, "19700102" },
                { DicomTag.PatientSex, "O" }, { DicomTag.AccessionNumber, "ACC-1" }, { DicomTag.StudyID, "STUDY-1" },
                { DicomTag.StudyInstanceUID, uid1 }, { DicomTag.SeriesInstanceUID, uid2 }, { DicomTag.SOPInstanceUID, uid3 },
                { DicomTag.StudyDate, "20200203" }, { DicomTag.ReferringPhysicianName, "Doktor^Åsa" }, { DicomTag.StudyDescription, "Manual study" },
                { DicomTag.SeriesDescription, "Manual series" }, { DicomTag.InstitutionName, "Manual hospital" }, { DicomTag.StationName, "STATION1" },
            };
            foreach (var pair in expected)
            {
                Check($"{pair.Key.DictionaryEntry.Name} is used unchanged", Text(ds, pair.Key) == pair.Value, Text(ds, pair.Key));
            }
            Check("Study Time starts at the manual time", Text(ds, DicomTag.StudyTime).StartsWith("040506"), Text(ds, DicomTag.StudyTime));

            var unknownId = new IdentityGenerator().Generate(new IdentityOverrides { PatientId = "NOT-A-NUMBER" }, Modality("CT"), Modality("CT").Presets[0]);
            Check("Unreadable manual ID leaves birth date and sex empty", unknownId.PatientBirthDate == string.Empty && unknownId.PatientSex == string.Empty);
            var readableId = new IdentityGenerator().Generate(new IdentityOverrides { PatientId = "189001019802" }, Modality("CT"), Modality("CT").Presets[0]);
            Check("Readable manual ID fills birth date and sex", readableId.PatientBirthDate == "18900101" && readableId.PatientSex == "F");
        }

        // ---------------------------------------------------------------- JPEG

        private static void TestJpegRoundTrip()
        {
            Console.WriteLine("\nJPEG Lossless encoder (checked with a decoder written from the standard)");
            foreach (var bits in new[] { 8, 12, 14, 16 })
            {
                var modality = bits == 8 ? "CR" : "DX";
                if (bits == 14) modality = "CT";
                foreach (var size in new[] { new[] { 64, 64 }, new[] { 65, 65 }, new[] { 256, 300 } })
                {
                    var image = new ExamGenerator(MakeRequest(modality, rows: size[0], columns: size[1], bits: bits)).GetImages(CancellationToken.None).First();
                    var original = FrameValues(DicomPixelData.Create(image.File.Dataset).GetFrame(0).Data, bits);
                    var encoded = TransferSyntaxConverter.Convert(image.File, DicomTransferSyntax.JPEGProcess14SV1);
                    var decoder = new LosslessJpegDecoder(DicomPixelData.Create(encoded.Dataset).GetFrame(0).Data);
                    var decoded = decoder.Decode();
                    var same = decoded.Length == original.Length && decoded.SequenceEqual(original);
                    Check($"{bits} bits {size[0]}x{size[1]}: exact pixels, SOF3 precision {bits}, predictor 1",
                        same && decoder.Precision == bits && decoder.Predictor == 1 && decoder.PointTransform == 0 && decoder.Width == size[1] && decoder.Height == size[0],
                        $"same {same}, precision {decoder.Precision}, predictor {decoder.Predictor}");

                    // Found by DCMTK: encapsulated Pixel Data must have VR OB, also with 16 bits allocated.
                    var path = Path.Combine(Path.GetTempPath(), "DCST4Vr_" + Guid.NewGuid().ToString("N") + ".dcm");
                    try
                    {
                        encoded.Save(path);
                        var bytes = File.ReadAllBytes(path);
                        var pixelTag = new byte[] { 0xE0, 0x7F, 0x10, 0x00 };
                        var writtenVr = string.Empty;
                        for (var i = bytes.Length - 8; i >= 0 && writtenVr.Length == 0; i--)
                        {
                            if (bytes[i] == pixelTag[0] && bytes[i + 1] == pixelTag[1] && bytes[i + 2] == pixelTag[2] && bytes[i + 3] == pixelTag[3])
                            {
                                writtenVr = Encoding.ASCII.GetString(bytes, i + 4, 2);
                            }
                        }
                        Check($"{bits} bits {size[0]}x{size[1]}: encapsulated Pixel Data is written as OB", writtenVr == "OB", writtenVr);
                    }
                    finally
                    {
                        File.Delete(path);
                    }
                }
            }

            // Worst case for category 16 (difference of exactly 32768).
            var hard = new byte[64 * 64 * 2];
            for (var i = 0; i < 64 * 64; i++)
            {
                var v = i % 2 == 0 ? 0 : 32768;
                hard[2 * i] = (byte)v;
                hard[2 * i + 1] = (byte)(v >> 8);
            }
            var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
            dataset.Add(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
            dataset.Add(DicomTag.SOPInstanceUID, DicomUID.Generate());
            dataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
            var pixels = DicomPixelData.Create(dataset, true);
            pixels.BitsStored = 16; pixels.HighBit = 15; pixels.SamplesPerPixel = 1; pixels.PixelRepresentation = PixelRepresentation.Unsigned;
            pixels.Width = 64; pixels.Height = 64; pixels.PhotometricInterpretation = PhotometricInterpretation.Monochrome2;
            pixels.AddFrame(new FellowOakDicom.IO.Buffer.MemoryByteBuffer(hard));
            var compressed = new DicomFile(dataset).Clone(DicomTransferSyntax.JPEGProcess14SV1);
            var result = new LosslessJpegDecoder(DicomPixelData.Create(compressed.Dataset).GetFrame(0).Data).Decode();
            Check("Differences of exactly 32768 survive", result.SequenceEqual(FrameValues(hard, 16)));
        }

        private static int[] FrameValues(byte[] frame, int bits)
        {
            if (bits <= 8) return frame.Select(b => (int)b).ToArray();
            var values = new int[frame.Length / 2];
            for (var i = 0; i < values.Length; i++) values[i] = frame[2 * i] | frame[2 * i + 1] << 8;
            return values;
        }

        // ---------------------------------------------------------------- connectivity

        private static async Task TestConnectivity()
        {
            Console.WriteLine("\nPing and C-ECHO");
            Scp.Reset();
            var options = SendOptions();

            var echo = await EchoService.EchoAsync(options.Endpoint, options.Network, CancellationToken.None);
            Check("Echo succeeds", echo.Outcome == ConnectivityOutcome.Success, echo.Message);
            Check("Echo reached the SCP", Scp.EchoCount == 1, Scp.EchoCount.ToString());

            var closed = SendOptions(port: ClosedPort);
            var refused = await EchoService.EchoAsync(closed.Endpoint, closed.Network, CancellationToken.None);
            Check("Echo to a closed port fails with a real reason", refused.Outcome == ConnectivityOutcome.Failed && !refused.Message.Contains("One or more errors") && !refused.Message.Contains("Ett eller flera fel"), refused.Message);

            Scp.RejectAssociation = true;
            var rejected = await EchoService.EchoAsync(options.Endpoint, options.Network, CancellationToken.None);
            Check("Echo with a rejected association fails", rejected.Outcome == ConnectivityOutcome.Failed, rejected.Message);
            Scp.RejectAssociation = false;

            var ping = await PingService.PingAsync("127.0.0.1");
            Check("Ping localhost succeeds", ping.Outcome == ConnectivityOutcome.Success, ping.Message);
            LogSink.Clear();
            var badPing = await PingService.PingAsync("no-such-host.invalid");
            Check("Ping to an unknown host reports failure", badPing.Outcome == ConnectivityOutcome.Failed, badPing.Message);
            Check("Ping failure is logged", LogSink.Lines.Any(l => l.Contains("Ping")));
        }

        // ---------------------------------------------------------------- sending

        private static async Task TestSendingSyntaxes()
        {
            Console.WriteLine("\nSending: transfer syntaxes");
            foreach (var option in TransferSyntaxOption.All)
            {
                foreach (var bits in new[] { 12, 16 })
                {
                    Scp.Reset();
                    var request = MakeRequest("CT", images: 3, rows: 128, columns: 128, bits: bits);
                    var expected = new ExamGenerator(request, () => new DateTime(2026, 11, 6, 12, 0, 0)).GetImages(CancellationToken.None).Select(i => FrameValues(DicomPixelData.Create(i.File.Dataset).GetFrame(0).Data, bits)).ToList();
                    var summary = await Run(request, SendOptions(option.Syntax));

                    Check($"{option.Name} {bits} bits: 3 images ok", summary.Success == 3 && summary.FatalError == null, summary + " / " + summary.FatalError);
                    var received = Scp.Received.ToList();
                    Check($"{option.Name} {bits} bits: arrived in that syntax", received.Count == 3 && received.All(r => r.Syntax == option.Syntax), string.Join(",", received.Select(r => r.Syntax.UID.Name)));
                    Check($"{option.Name} {bits} bits: that syntax is proposed first, in a context of its own", Scp.Proposed.FirstOrDefault() == option.Syntax.UID.UID, string.Join(" | ", Scp.Proposed));

                    var pixelsOk = received.Count == 3 && Enumerable.Range(0, 3).All(i =>
                    {
                        var values = option.Syntax == DicomTransferSyntax.JPEGProcess14SV1 ? new LosslessJpegDecoder(received[i].Frame).Decode() : FrameValues(received[i].Frame, bits);
                        return values.Length == 128 * 128 && values.Max() > 0;
                    });
                    Check($"{option.Name} {bits} bits: pixels decode at the SCP", pixelsOk);
                    Check($"{option.Name} {bits} bits: accepted syntax recorded", summary.Results.All(r => r.AcceptedTransferSyntax == option.Syntax.UID.Name), summary.Results.First().AcceptedTransferSyntax);
                }
            }

            // Exact pixels end to end, for the same image in all three syntaxes.
            var values1 = new Dictionary<string, int[]>();
            var manualRequest = MakeRequest("CR", rows: 128, columns: 128, bits: 12, overrides: new IdentityOverrides { PatientId = "189001019802", PatientName = "A^B", AccessionNumber = "A", StudyDateTime = new DateTime(2026, 1, 1) });
            manualRequest.Identity.ReferringPhysicianName = "C^D";
            foreach (var option in TransferSyntaxOption.All)
            {
                Scp.Reset();
                await Run(manualRequest, SendOptions(option.Syntax));
                var frame = Scp.Received.Single().Frame;
                values1[option.Name] = option.Syntax == DicomTransferSyntax.JPEGProcess14SV1 ? new LosslessJpegDecoder(frame).Decode() : FrameValues(frame, 12);
            }
            Check("Same image: identical pixels in all three syntaxes",
                values1.Values.All(v => v.SequenceEqual(values1[TransferSyntaxOption.ExplicitVrLe.Name])));

            // A PACS that cannot take the selected syntax must fail visibly, not quietly use another one.
            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
            var noJpeg = await Run(MakeRequest("CT", images: 2), SendOptions(DicomTransferSyntax.JPEGProcess14SV1));
            Check("SCP without JPEG: nothing stored", Scp.StoreCount == 0, Scp.StoreCount.ToString());
            Check("SCP without JPEG: reported as not sent", noJpeg.Success == 0 && noJpeg.NotSent == 2, noJpeg.ToString());
            Check("SCP without JPEG: error names the syntax", noJpeg.FatalError != null && noJpeg.FatalError.Contains("JPEG Lossless") && noJpeg.FatalError.Contains("did not accept"), noJpeg.FatalError);

            // This depends on timing inside fo-dicom, so one lucky pass is not enough.
            var storedInRepeats = 0;
            for (var i = 0; i < 25; i++)
            {
                Scp.Reset();
                Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
                await Run(MakeRequest("CT", images: 1 + i % 5), SendOptions(DicomTransferSyntax.JPEGProcess14SV1));
                storedInRepeats += Scp.StoreCount;
            }
            Check("SCP without JPEG: nothing stored in 25 repeated runs", storedInRepeats == 0, storedInRepeats + " images stored");

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
            var noExplicit = await Run(MakeRequest("CT"), SendOptions(DicomTransferSyntax.ExplicitVRLittleEndian));
            Check("SCP without Explicit VR: stops the same way", noExplicit.FatalError != null && noExplicit.FatalError.Contains("Explicit VR") && Scp.StoreCount == 0, noExplicit.FatalError);

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
            var fallbackOptions = SendOptions(DicomTransferSyntax.JPEGProcess14SV1);
            fallbackOptions.AllowFallback = true;
            var fallback = await Run(MakeRequest("CT", images: 2, bits: 12), fallbackOptions);
            Check("Fallback allowed: images are stored", fallback.Success == 2 && fallback.FatalError == null && Scp.StoreCount == 2, fallback + " / " + fallback.FatalError);
            Check("Fallback allowed: they arrive as Implicit VR", Scp.Received.All(r => r.Syntax == DicomTransferSyntax.ImplicitVRLittleEndian));
            Check("Fallback allowed: flagged in the summary", fallback.FallbackWarning != null && fallback.FallbackWarning.Contains("did not accept"), fallback.FallbackWarning);
            Check("Fallback allowed: result shows the syntax really used", fallback.Results.All(r => r.AcceptedTransferSyntax.Contains("Implicit")), fallback.Results.First().AcceptedTransferSyntax);
            Check("Fallback allowed: summary shows the syntax really used", (fallback.NegotiationSummary ?? string.Empty).Contains("Implicit"), fallback.NegotiationSummary);

            // An SCP that accepts everything must get the selected syntax, and that is what must be reported.
            Scp.Reset();
            var allAccepted = await Run(MakeRequest("CT"), SendOptions(DicomTransferSyntax.JPEGProcess14SV1));
            Check("Both contexts accepted: selected syntax used, no fallback flagged", allAccepted.FallbackWarning == null && allAccepted.Results.Single().AcceptedTransferSyntax.Contains("JPEG") && Scp.Received.Single().Syntax == DicomTransferSyntax.JPEGProcess14SV1, allAccepted.Results.Single().AcceptedTransferSyntax);

            Scp.Reset();
            Scp.AcceptedSyntaxes = new DicomTransferSyntax[0];
            var acceptsNothing = await Run(MakeRequest("CT"), fallbackOptions);
            Check("SCP accepts no context at all: nothing counted as stored", acceptsNothing.Success == 0 && Scp.StoreCount == 0, acceptsNothing + " / " + acceptsNothing.FatalError);
        }

        private static async Task TestStatuses()
        {
            Console.WriteLine("\nSending: response statuses");
            var cases = new[]
            {
                new { Code = (ushort)0x0000, State = StoreState.Success, Status = DicomStatus.Success, Text = "Success" },
                new { Code = (ushort)0xB000, State = StoreState.Warning, Status = DicomStatus.StorageCoercionOfDataElements, Text = "Coercion" },
                new { Code = (ushort)0xB006, State = StoreState.Warning, Status = DicomStatus.StorageElementsDiscarded, Text = "Discarded" },
                new { Code = (ushort)0xB007, State = StoreState.Warning, Status = DicomStatus.StorageDataSetDoesNotMatchSOPClassWarning, Text = "does not match" },
                new { Code = (ushort)0xA700, State = StoreState.Failure, Status = DicomStatus.StorageStorageOutOfResources, Text = "Out of Resources" },
                new { Code = (ushort)0xA900, State = StoreState.Failure, Status = DicomStatus.StorageDataSetDoesNotMatchSOPClassError, Text = "does not match" },
                new { Code = (ushort)0xC000, State = StoreState.Failure, Status = DicomStatus.StorageCannotUnderstand, Text = "Cannot understand" },
                new { Code = (ushort)0xC123, State = StoreState.Failure, Status = new DicomStatus("C123", DicomState.Failure, "Vendor"), Text = "Cannot understand" },
                new { Code = (ushort)0xBABE, State = StoreState.Warning, Status = new DicomStatus("BABE", DicomState.Warning, "Vendor"), Text = "vendor-specific" },
                new { Code = (ushort)0xD00D, State = StoreState.Failure, Status = new DicomStatus("D00D", DicomState.Failure, "Vendor"), Text = "vendor-specific" },
            };

            foreach (var c in cases)
            {
                Scp.Reset();
                Scp.StatusFor = _ => c.Status;
                LogSink.Clear();
                var summary = await Run(MakeRequest("CT"), SendOptions());
                var result = summary.Results.SingleOrDefault();
                var code = "0x" + c.Code.ToString("X4");
                Check($"{code}: state {c.State}, raw code kept", result != null && result.StatusCode == c.Code && result.State == c.State, result == null ? "no result" : result.StatusCodeText + " " + result.State);
                Check($"{code}: description", result != null && result.Description.IndexOf(c.Text, StringComparison.OrdinalIgnoreCase) >= 0, result?.Description);
                Check($"{code}: in the log", LogSink.Lines.Any(l => l.Contains("C-STORE") && l.Contains(code)));
                Check($"{code}: counted in the summary", c.State == StoreState.Success ? summary.Success == 1 : c.State == StoreState.Warning ? summary.Warning == 1 : summary.Failure == 1, summary.ToString());
            }

            Scp.Reset();
            Scp.StatusFor = _ => new DicomStatus(DicomStatus.StorageElementsDiscarded, "Dropped private tags");
            Scp.OffendingElement = new Scp.Tag { Value = DicomTag.PatientName };
            LogSink.Clear();
            var detailed = (await Run(MakeRequest("CT"), SendOptions())).Results.Single();
            Check("Error Comment is reported", detailed.ErrorComment == "Dropped private tags", detailed.ErrorComment);
            Check("Offending Element is reported", detailed.OffendingElement.Contains("0010") , detailed.OffendingElement);
            Check("SOP Instance UID is reported", ExamValidator.IsValidUid(detailed.SopInstanceUid));
            Check("Error Comment is in the log", LogSink.Lines.Any(l => l.Contains("Dropped private tags")));

            // A failure in the middle must not stop the rest: it is a useful result, not an error.
            Scp.Reset();
            Scp.StatusFor = i => i == 1 ? DicomStatus.StorageStorageOutOfResources : (i == 2 ? DicomStatus.StorageElementsDiscarded : DicomStatus.Success);
            var mixed = await Run(MakeRequest("CT", images: 5), SendOptions());
            Check("The run continues after a failure status", Scp.StoreCount == 5 && mixed.Results.Count == 5, Scp.StoreCount.ToString());
            Check("Mixed run is counted correctly", mixed.Success == 3 && mixed.Warning == 1 && mixed.Failure == 1 && mixed.NotSent == 0 && mixed.FatalError == null, mixed.ToString());
        }

        private static async Task TestAssociationBehaviour()
        {
            Console.WriteLine("\nSending: association behaviour");

            Scp.Reset();
            var sw = Stopwatch.StartNew();
            var one = await Run(MakeRequest("CT", images: 1), SendOptions());
            sw.Stop();
            Check("Single image ok", one.Success == 1 && one.FatalError == null, one + " / " + one.FatalError);
            Check("Run ends promptly: no wait for the association linger", sw.ElapsedMilliseconds < 5000, sw.ElapsedMilliseconds + " ms");

            Scp.Reset();
            sw.Restart();
            var longRun = await Run(MakeRequest("CT", series: 2, images: 50), SendOptions());
            sw.Stop();
            Check("100 images all acknowledged", longRun.Success == 100 && longRun.FatalError == null, longRun + " / " + longRun.FatalError);
            Check("100 images use one association", Scp.AssociationCount == 1, Scp.AssociationCount.ToString());
            Check("Run reports one association", longRun.AssociationCount == 1, longRun.AssociationCount.ToString());
            Check("100 images arrive in order", Scp.Received.Select(r => int.Parse(r.Dataset.GetString(DicomTag.InstanceNumber)) + 100 * int.Parse(r.Dataset.GetString(DicomTag.SeriesNumber)))
                .SequenceEqual(Enumerable.Range(101, 50).Concat(Enumerable.Range(201, 50))));
            Check("Long run ends promptly", sw.ElapsedMilliseconds < 20000, sw.ElapsedMilliseconds + " ms");
            Check("Throughput is measured", longRun.ImagesPerSecond > 0 && longRun.Elapsed > TimeSpan.Zero && longRun.PixelBytes > 0, longRun.ToString());

            // The SCP is faster than generation, so the client keeps running out of queued work. It must stay on one association.
            Scp.Reset();
            var slowGeneration = await Run(MakeRequest("CT", images: 12, rows: 1024, columns: 1024), SendOptions());
            Check("Generation slower than the SCP still uses one association", Scp.AssociationCount == 1 && slowGeneration.Success == 12, Scp.AssociationCount + " / " + slowGeneration);

            Scp.Reset();
            Scp.ResponseDelayMs = 20;
            var slowScp = await Run(MakeRequest("CT", images: 12), SendOptions());
            Check("SCP slower than generation: all acknowledged", slowScp.Success == 12 && Scp.AssociationCount == 1, slowScp.ToString());
            Check("Elapsed time is reported per image", slowScp.Results.All(r => r.ElapsedMs >= 10), string.Join(",", slowScp.Results.Select(r => r.ElapsedMs)));

            Scp.Reset();
            var aeOptions = SendOptions();
            aeOptions.Endpoint.CallingAe = "  MYCALLER ";
            aeOptions.Endpoint.CalledAe = " MYPACS  ";
            await Run(MakeRequest("CT"), aeOptions);
            var seen = Scp.Received.Single();
            Check("AE titles are trimmed and reach the SCP", seen.CallingAe == "MYCALLER" && seen.CalledAe == "MYPACS", seen.CallingAe + " / " + seen.CalledAe);
        }

        private static async Task TestFaults()
        {
            Console.WriteLine("\nSending: faults");

            Scp.Reset();
            Scp.RejectAssociation = true;
            var rejected = await Run(MakeRequest("CT", images: 3), SendOptions());
            Check("Rejected association: reported with reason", rejected.FatalError != null && rejected.FatalError.IndexOf("reject", StringComparison.OrdinalIgnoreCase) >= 0, rejected.FatalError);
            Check("Rejected association: images not sent", rejected.NotSent == 3 && rejected.Success == 0, rejected.ToString());

            Scp.Reset();
            var closed = await Run(MakeRequest("CT", images: 3), SendOptions(port: ClosedPort));
            Check("Closed port: reported", closed.FatalError != null, closed.ToString());
            Check("Closed port: images not sent", closed.NotSent == 3, closed.ToString());
            Check("Closed port: results listed as not sent", closed.Results.All(r => r.State == StoreState.NotSent), string.Join(",", closed.Results.Select(r => r.State)));

            Scp.Reset();
            Scp.HangFromImage = 1;
            var sw = Stopwatch.StartNew();
            var timeout = await Run(MakeRequest("CT", images: 6), SendOptions(responseTimeout: 2));
            sw.Stop();
            Check("Hanging SCP: times out", timeout.FatalError != null && timeout.FatalError.Contains("timed out"), timeout.FatalError);
            Check("Hanging SCP: stops soon after the timeout", sw.ElapsedMilliseconds < 8000, sw.ElapsedMilliseconds + " ms");
            Check("Hanging SCP: first image counted, the rest not sent", timeout.Success == 1 && timeout.NotSent == 5, timeout.ToString());

            Scp.Reset();
            Scp.ResponseDelayMs = 200;
            using (var cts = new CancellationTokenSource())
            {
                var seen = 0;
                var run = Run(MakeRequest("CT", images: 40), SendOptions(), cts.Token, _ =>
                {
                    if (Interlocked.Increment(ref seen) == 3) cts.Cancel();
                });
                sw.Restart();
                var cancelled = await run;
                sw.Stop();
                Check("Cancel: run reports cancelled", cancelled.WasCancelled, cancelled.ToString());
                Check("Cancel: stops well before all images are sent", Scp.StoreCount < 40 && sw.ElapsedMilliseconds < 8000, Scp.StoreCount + " images, " + sw.ElapsedMilliseconds + " ms");
                Check("Cancel: unsent images are counted", cancelled.NotSent > 0 && cancelled.Success + cancelled.NotSent == 40, cancelled.ToString());
            }

            // Generation failure mid-run must surface as an error, not hang.
            Scp.Reset();
            var failing = await new StoreRun(new FailingSource(), SendOptions()).ExecuteAsync(CancellationToken.None);
            Check("Image source failure: reported, run ends", failing.FatalError != null && failing.FatalError.Contains("source failed"), failing.FatalError);
        }

        private sealed class FailingSource : IImageSource
        {
            public int ExpectedCount => 3;
            public IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken)
            {
                foreach (var image in new ExamGenerator(MakeRequest("CT", images: 2)).GetImages(cancellationToken))
                {
                    yield return image;
                }
                throw new InvalidOperationException("source failed");
            }
        }

        // ---------------------------------------------------------------- saving

        private static string[] SavedImages(string folder) => Directory.GetFiles(Path.Combine(folder, "DICOM"), "*", SearchOption.AllDirectories);

        /// <summary>Everything the DICOMDIR says, flattened: one entry per image record.</summary>
        private sealed class DirectoryEntry
        {
            public string PatientId, PatientName, CharacterSet, StudyUid, Accession, SeriesUid, Modality, FileId, SopInstanceUid, SopClassUid, Syntax;
        }

        private static List<DirectoryEntry> ReadDicomDir(string folder)
        {
            var entries = new List<DirectoryEntry>();
            var directory = FellowOakDicom.Media.DicomDirectory.Open(Path.Combine(folder, "DICOMDIR"));
            foreach (var patient in directory.RootDirectoryRecordCollection)
            {
                foreach (var study in patient.LowerLevelDirectoryRecordCollection)
                {
                    foreach (var series in study.LowerLevelDirectoryRecordCollection)
                    {
                        foreach (var image in series.LowerLevelDirectoryRecordCollection)
                        {
                            entries.Add(new DirectoryEntry
                            {
                                PatientId = Text(patient, DicomTag.PatientID),
                                PatientName = Text(patient, DicomTag.PatientName),
                                CharacterSet = Text(patient, DicomTag.SpecificCharacterSet),
                                StudyUid = Text(study, DicomTag.StudyInstanceUID),
                                Accession = Text(study, DicomTag.AccessionNumber),
                                SeriesUid = Text(series, DicomTag.SeriesInstanceUID),
                                Modality = Text(series, DicomTag.Modality),
                                FileId = string.Join("\\", image.GetValues<string>(DicomTag.ReferencedFileID)),
                                SopInstanceUid = Text(image, DicomTag.ReferencedSOPInstanceUIDInFile),
                                SopClassUid = Text(image, DicomTag.ReferencedSOPClassUIDInFile),
                                Syntax = Text(image, DicomTag.ReferencedTransferSyntaxUIDInFile),
                            });
                        }
                    }
                }
            }
            return entries;
        }

        private static async Task TestSaving(string temp)
        {
            Console.WriteLine("\nSaving to disk");

            Scp.Reset();
            var folder = Path.Combine(temp, "saveonly");
            var request = MakeRequest("CT", series: 2, images: 2);
            var options = new StoreRunOptions { Send = false, Save = true, SaveFolder = folder };
            var summary = await Run(request, options);
            Check("Save only: no association opened", Scp.AssociationCount == 0, Scp.AssociationCount.ToString());
            Check("Save only: 4 images", summary.Saved == 4 && SavedImages(folder).Length == 4, summary.ToString());
            Check("Save only: layout", File.Exists(Path.Combine(folder, "DICOM", "EX000001", "S002", "IM0002")), string.Join(" ", SavedImages(folder)));
            Check("Saved file can be read back", DicomFile.Open(summary.Results[0].SavedPath).Dataset.GetString(DicomTag.PatientID) == request.Identity.PatientId);

            // ------------------------------------------------ DICOMDIR
            Check("DICOMDIR is written", summary.DicomDirPath == Path.Combine(folder, "DICOMDIR") && File.Exists(summary.DicomDirPath) && summary.DicomDirError == null, summary.DicomDirError);
            Check("No temporary file is left", !File.Exists(summary.DicomDirPath + ".tmp"));
            var entries = ReadDicomDir(folder);
            Check("DICOMDIR lists all images", entries.Count == 4, entries.Count.ToString());
            Check("All File IDs are valid: 8 characters, no extension", entries.All(e => DCST4.Core.Output.FileSetWriter.IsValidFileId(e.FileId)), string.Join(" ", entries.Select(e => e.FileId)));
            Check("All references point at existing files", entries.All(e => File.Exists(Path.Combine(folder, e.FileId))));
            var matching = entries.All(e =>
            {
                var ds = DicomFile.Open(Path.Combine(folder, e.FileId)).Dataset;
                return Text(ds, DicomTag.SOPInstanceUID) == e.SopInstanceUid && Text(ds, DicomTag.SOPClassUID) == e.SopClassUid
                    && Text(ds, DicomTag.SeriesInstanceUID) == e.SeriesUid && Text(ds, DicomTag.StudyInstanceUID) == e.StudyUid;
            });
            Check("Each record matches the file it points at", matching);
            Check("One patient and study, two series", entries.Select(e => e.StudyUid).Distinct().Count() == 1 && entries.Select(e => e.SeriesUid).Distinct().Count() == 2);
            Check("Records hold patient, accession and modality", entries.All(e => e.PatientId == request.Identity.PatientId && e.Accession == request.Identity.AccessionNumber && e.Modality == "CT"));
            Check("Record names the transfer syntax", entries.All(e => e.Syntax == DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID), entries[0].Syntax);

            Check("File ID rule: too long", !DCST4.Core.Output.FileSetWriter.IsValidFileId("DICOM\\TOOLONGNAME"));
            Check("File ID rule: extension", !DCST4.Core.Output.FileSetWriter.IsValidFileId("DICOM\\IM0001.DCM"));
            Check("File ID rule: lower case", !DCST4.Core.Output.FileSetWriter.IsValidFileId("dicom\\IM0001"));
            Check("File ID rule: too deep", !DCST4.Core.Output.FileSetWriter.IsValidFileId("A\\B\\C\\D\\E\\F\\G\\H\\I"));

            // ------------------------------------------------ Several runs into one folder, in different character sets
            var shared = Path.Combine(temp, "shared");
            var sharedOptions = new StoreRunOptions { Send = false, Save = true, SaveFolder = shared };
            await Run(MakeRequest("CT", images: 2, charset: DicomCharacterSets.Latin1, overrides: new IdentityOverrides { PatientName = "Åström^Märta", PatientId = "189001019802" }), sharedOptions);
            await Run(MakeRequest("US", images: 1, charset: DicomCharacterSets.Utf8, overrides: new IdentityOverrides { PatientName = "Öberg^Åke", PatientId = "189001029819" }), sharedOptions);
            var sharedEntries = ReadDicomDir(shared);
            Check("Two runs: both in the same DICOMDIR", sharedEntries.Count == 3, sharedEntries.Count.ToString());
            Check("Two runs: separate exam folders", Directory.Exists(Path.Combine(shared, "DICOM", "EX000001")) && Directory.Exists(Path.Combine(shared, "DICOM", "EX000002")));
            Check("Two runs: ISO_IR 100 name is correct in the DICOMDIR", sharedEntries.Where(e => e.PatientId == "189001019802").All(e => e.PatientName == "Åström^Märta" && e.CharacterSet == "ISO_IR 100"), string.Join(",", sharedEntries.Select(e => e.PatientName + "/" + e.CharacterSet)));
            Check("Two runs: ISO_IR 192 name is correct in the DICOMDIR", sharedEntries.Where(e => e.PatientId == "189001029819").All(e => e.PatientName == "Öberg^Åke" && e.CharacterSet == "ISO_IR 192"), string.Join(",", sharedEntries.Select(e => e.PatientName + "/" + e.CharacterSet)));
            Check("Two runs: both modalities listed", sharedEntries.Any(e => e.Modality == "US") && sharedEntries.Any(e => e.Modality == "CT"));

            // ------------------------------------------------ Send and save
            Scp.Reset();
            var both = Path.Combine(temp, "both");
            var bothOptions = SendOptions(DicomTransferSyntax.JPEGProcess14SV1);
            bothOptions.Save = true;
            bothOptions.SaveFolder = both;
            var bothSummary = await Run(MakeRequest("CT", images: 3), bothOptions);
            Check("Send and Save: sent", bothSummary.Success == 3 && Scp.StoreCount == 3, bothSummary.ToString());
            Check("Send and Save: saved", bothSummary.Saved == 3 && SavedImages(both).Length == 3, bothSummary.ToString());
            Check("Saved file uses the selected syntax", bothSummary.Results.All(r => DicomFile.Open(r.SavedPath).Dataset.InternalTransferSyntax == DicomTransferSyntax.JPEGProcess14SV1));
            Check("DICOMDIR names JPEG Lossless", ReadDicomDir(both).All(e => e.Syntax == DicomTransferSyntax.JPEGProcess14SV1.UID.UID));

            Scp.Reset();
            var sendOnly = await Run(MakeRequest("CT"), SendOptions());
            Check("Send only: no DICOMDIR", sendOnly.DicomDirPath == null && sendOnly.DicomDirError == null);

            // ------------------------------------------------ Runs that end early
            Scp.Reset();
            var failedSend = Path.Combine(temp, "failedsend");
            var failedOptions = SendOptions(port: ClosedPort);
            failedOptions.Save = true;
            failedOptions.SaveFolder = failedSend;
            var failedSummary = await Run(MakeRequest("CT", images: 1), failedOptions);
            Check("File exists even when sending fails", failedSummary.FatalError != null && SavedImages(failedSend).Length >= 1);
            Check("DICOMDIR is written even when sending fails", File.Exists(Path.Combine(failedSend, "DICOMDIR")) && ReadDicomDir(failedSend).Count == SavedImages(failedSend).Length, failedSummary.DicomDirError);

            var cancelFolder = Path.Combine(temp, "cancel");
            using (var cts = new CancellationTokenSource())
            {
                var seen = 0;
                var cancelled = await Run(MakeRequest("CT", images: 500), new StoreRunOptions { Send = false, Save = true, SaveFolder = cancelFolder }, cts.Token, _ =>
                {
                    if (Interlocked.Increment(ref seen) == 3) cts.Cancel();
                });
                Check("Cancelled save: stops early", cancelled.WasCancelled && SavedImages(cancelFolder).Length < 500, cancelled.ToString());
                Check("Cancelled save: DICOMDIR lists what was saved", ReadDicomDir(cancelFolder).Count == SavedImages(cancelFolder).Length, ReadDicomDir(cancelFolder).Count + " of " + SavedImages(cancelFolder).Length);
            }

            // ------------------------------------------------ Stray files
            var stray = Path.Combine(temp, "stray");
            var strayOptions = new StoreRunOptions { Send = false, Save = true, SaveFolder = stray };
            await Run(MakeRequest("CT"), strayOptions);
            File.WriteAllText(Path.Combine(stray, "DICOM", "EX000001", "NOTDICOM"), "not a DICOM file");
            File.WriteAllText(Path.Combine(stray, "DICOM", "EX000001", "notes.txt"), "also not one");
            var straySummary = await Run(MakeRequest("CT"), strayOptions);
            Check("Stray files: the DICOMDIR is still written", straySummary.DicomDirError == null, straySummary.DicomDirError);
            Check("Stray files: left out", ReadDicomDir(stray).Count == 2, ReadDicomDir(stray).Count.ToString());

            var existing = Path.Combine(temp, "existing");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "DICOMDIR"), "an old, damaged DICOMDIR");
            var replaced = await Run(MakeRequest("CT"), new StoreRunOptions { Send = false, Save = true, SaveFolder = existing });
            Check("A damaged DICOMDIR is replaced", replaced.DicomDirError == null && ReadDicomDir(existing).Count == 1, replaced.DicomDirError);

            var unwritable = Path.Combine(temp, "file_in_the_way");
            File.WriteAllText(unwritable, "x");
            var badSummary = await Run(MakeRequest("CT"), new StoreRunOptions { Send = false, Save = true, SaveFolder = unwritable });
            Check("Save failure is reported per image", badSummary.SaveFailed == 1 && badSummary.Results.Single().State == StoreState.SaveFailed, badSummary.ToString());
            Check("Nothing saved: no DICOMDIR attempt", badSummary.DicomDirPath == null && badSummary.DicomDirError == null, badSummary.DicomDirError);
        }

        // ---------------------------------------------------------------- TLS

        private static X509Certificate2 CreateSelfSignedCert()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)))
                {
                    // SChannel cannot use the ephemeral key of a freshly created certificate; re-import it from a PFX.
                    return new X509Certificate2(cert.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
                }
            }
        }

        private static async Task TestTls()
        {
            Console.WriteLine("\nTLS");
            X509Certificate2 certificate;
            try { certificate = CreateSelfSignedCert(); }
            catch (Exception ex)
            {
                // Not counted as a pass: this must stay visible in the output.
                Console.WriteLine("  SKIP  could not create a self-signed certificate: " + ex.Message);
                return;
            }

            using (DicomServerFactory.Create<MockStorageScp>(TlsPort, new FellowOakDicom.Network.Tls.DefaultTlsAcceptor(certificate)))
            {
                Scp.Reset();
                var strict = SendOptions(port: TlsPort);
                strict.Endpoint.UseTls = true;
                strict.Endpoint.TlsIgnoreCertErrors = false;

                var refused = await EchoService.EchoAsync(strict.Endpoint, strict.Network, CancellationToken.None);
                Check("Echo to a self-signed server fails when certificate errors are not accepted", refused.Outcome == ConnectivityOutcome.Failed && Scp.EchoCount == 0, refused.Message);
                var refusedStore = await Run(MakeRequest("CT", images: 2), strict);
                Check("Sending to a self-signed server fails the same way", refusedStore.FatalError != null && refusedStore.NotSent == 2 && Scp.StoreCount == 0, refusedStore + " / " + refusedStore.FatalError);

                var lenient = SendOptions(DicomTransferSyntax.JPEGProcess14SV1, TlsPort);
                lenient.Endpoint.UseTls = true;
                lenient.Endpoint.TlsIgnoreCertErrors = true;

                var echo = await EchoService.EchoAsync(lenient.Endpoint, lenient.Network, CancellationToken.None);
                Check("Echo over TLS succeeds when certificate errors are accepted", echo.Outcome == ConnectivityOutcome.Success && Scp.EchoCount == 1, echo.Message);

                var stored = await Run(MakeRequest("CT", images: 6), lenient);
                Check("Images are sent over TLS", stored.Success == 6 && Scp.StoreCount == 6 && Scp.AssociationCount >= 1, stored + " / " + stored.FatalError);
                Check("JPEG Lossless arrives intact over TLS", Scp.Received.All(r => r.Syntax == DicomTransferSyntax.JPEGProcess14SV1 && new LosslessJpegDecoder(r.Frame).Decode().Length == 64 * 64));

                var missingCertificate = SendOptions(port: TlsPort);
                missingCertificate.Endpoint.UseTls = true;
                missingCertificate.Endpoint.TlsIgnoreCertErrors = true;
                missingCertificate.Endpoint.TlsClientCertThumbprint = "0011223344556677889900112233445566778899";
                var noCert = await EchoService.EchoAsync(missingCertificate.Endpoint, missingCertificate.Network, CancellationToken.None);
                Check("Missing client certificate gives a clear error", noCert.Outcome == ConnectivityOutcome.Failed && noCert.Message.Contains("client certificate") && noCert.Message.Contains("was not found"), noCert.Message);
            }
        }

        // ---------------------------------------------------------------- checking with other DICOM tools

        /// <summary>
        /// Writes every exam type, bit depth, character set and transfer syntax as files, for checking with another toolkit:
        /// DCST4.Tests.exe matrix &lt;folder&gt;. The same image is written in each syntax, so their pixels can be compared.
        /// </summary>
        private static void WriteMatrix(string folder)
        {
            Directory.CreateDirectory(folder);
            var count = 0;
            foreach (var modality in ModalityCatalog.All)
            {
                foreach (var preset in modality.Presets)
                {
                    foreach (var bits in modality.BitDepths)
                    {
                        var charset = bits % 2 == 0 && bits != 12 ? DicomCharacterSets.Utf8 : DicomCharacterSets.Latin1;
                        var overrides = new IdentityOverrides { PatientName = "Åström^Märta" };
                        var request = MakeRequest(modality.Code, images: 3, rows: 256, columns: 300, bits: bits, charset: charset, overrides: overrides);
                        request.Preset = preset;
                        request.Identity = new IdentityGenerator().Generate(overrides, modality, preset);

                        var images = new ExamGenerator(request).GetImages(CancellationToken.None).ToList();
                        for (var i = 0; i < images.Count; i++)
                        {
                            foreach (var option in TransferSyntaxOption.All)
                            {
                                var suffix = option.Syntax == DicomTransferSyntax.ImplicitVRLittleEndian ? "implicit" : option.Syntax == DicomTransferSyntax.JPEGProcess14SV1 ? "jpeg" : "explicit";
                                var name = $"{modality.Code}_{preset.Name.Replace(" ", string.Empty)}_{bits}bit_{i + 1}_{suffix}.dcm";
                                TransferSyntaxConverter.Convert(images[i].File, option.Syntax).Save(Path.Combine(folder, name));
                                count++;
                            }
                        }
                    }
                }
            }
            Console.WriteLine($"Wrote {count} files to {folder}");
        }

        /// <summary>
        /// Writes File-sets with several runs, as the app does, for checking the DICOMDIR with another toolkit:
        /// DCST4.Tests.exe fileset &lt;folder&gt;. Every modality in both character sets, uncompressed and JPEG.
        /// </summary>
        private static async Task WriteFileSet(string folder)
        {
            foreach (var syntax in new[] { "explicit", "jpeg" })
            {
                var target = Path.Combine(folder, syntax);
                var options = new StoreRunOptions
                {
                    Send = false,
                    Save = true,
                    SaveFolder = target,
                    TransferSyntax = syntax == "jpeg" ? DicomTransferSyntax.JPEGProcess14SV1 : DicomTransferSyntax.ExplicitVRLittleEndian,
                };
                foreach (var modality in ModalityCatalog.All)
                {
                    foreach (var charset in new[] { DicomCharacterSets.Latin1, DicomCharacterSets.Utf8 })
                    {
                        var request = MakeRequest(modality.Code, series: 2, images: 2, rows: 128, columns: 128, charset: charset, overrides: new IdentityOverrides { PatientName = "Åström^Märta" });
                        var summary = await Run(request, options);
                        Console.WriteLine($"{syntax} {modality.Code} {charset}: {summary}; DICOMDIR {summary.DicomDirPath ?? summary.DicomDirError}");
                    }
                }
            }
        }

        /// <summary>Sends one exam to any SCP: DCST4.Tests.exe send &lt;host&gt; &lt;port&gt; &lt;calledAe&gt; &lt;modality&gt; &lt;images&gt; &lt;bits&gt; &lt;implicit|explicit|jpeg&gt; [fallback]</summary>
        private static int SendTo(string[] args)
        {
            var syntax = args[7] == "implicit" ? DicomTransferSyntax.ImplicitVRLittleEndian : args[7] == "jpeg" ? DicomTransferSyntax.JPEGProcess14SV1 : DicomTransferSyntax.ExplicitVRLittleEndian;
            var options = SendOptions(syntax, int.Parse(args[2], CultureInfo.InvariantCulture));
            options.Endpoint.Host = args[1];
            options.Endpoint.CalledAe = args[3];
            options.AllowFallback = args.Length > 8 && args[8] == "fallback";

            var request = MakeRequest(args[4], images: int.Parse(args[5], CultureInfo.InvariantCulture), rows: 256, columns: 256, bits: int.Parse(args[6], CultureInfo.InvariantCulture),
                overrides: new IdentityOverrides { PatientName = "Åström^Märta" });

            var summary = Run(request, options).GetAwaiter().GetResult();
            Console.WriteLine($"{summary} | stopped by: {summary.FatalError ?? "nothing"} | fallback: {summary.FallbackWarning ?? "no"} | associations: {summary.AssociationCount} | used: {summary.NegotiationSummary}");
            foreach (var line in summary.Results.Where(r => r.State != StoreState.Success).Take(3))
            {
                Console.WriteLine($"  series {line.SeriesNumber} instance {line.InstanceNumber}: {line.State} {line.StatusCodeText} {line.Description}");
            }
            return summary.Success == request.TotalImages ? 0 : 1;
        }

        // ---------------------------------------------------------------- standalone SCP

        /// <summary>
        /// Runs the mock SCP until the process is stopped, for trying the real app by hand: DCST4.Tests.exe scp 11112 [mixed].
        /// "mixed" answers with a warning for every 5th image and a failure for every 7th. It stores nothing.
        /// </summary>
        private static void RunStandaloneScp(int port, bool mixed)
        {
            Scp.Reset();
            if (mixed)
            {
                Scp.StatusFor = i =>
                {
                    if ((i + 1) % 7 == 0) return new DicomStatus(DicomStatus.StorageStorageOutOfResources, "Mock SCP: simulated full disk");
                    if ((i + 1) % 5 == 0) return new DicomStatus(DicomStatus.StorageCoercionOfDataElements, "Mock SCP: simulated coercion");
                    return DicomStatus.Success;
                };
                Scp.OffendingElement = new Scp.Tag { Value = DicomTag.PatientName };
            }

            using (DicomServerFactory.Create<MockStorageScp>(port))
            {
                Console.WriteLine($"Mock SCP listening on port {port}. Stop the process to end.");
                Thread.Sleep(Timeout.Infinite);
            }
        }

        // ---------------------------------------------------------------- visual check

        /// <summary>Writes sample images the way a viewer would show them: through the file, Modality LUT and window.</summary>
        private static void RenderSamples(string folder)
        {
            Directory.CreateDirectory(folder);
            var overrides = new IdentityOverrides { PatientName = "Åström^Märta" };
            var samples = new[]
            {
                new { Name = "ct_16bit_512_iso100_jpeg", Modality = "CT", Rows = 512, Columns = 512, Bits = 16, Images = 20, Charset = DicomCharacterSets.Latin1, Index = 2, Syntax = DicomTransferSyntax.JPEGProcess14SV1 },
                new { Name = "ct_12bit_512_slice_mid", Modality = "CT", Rows = 512, Columns = 512, Bits = 12, Images = 20, Charset = DicomCharacterSets.Utf8, Index = 10, Syntax = DicomTransferSyntax.ExplicitVRLittleEndian },
                new { Name = "mr_12bit_256_utf8", Modality = "MR", Rows = 256, Columns = 256, Bits = 12, Images = 24, Charset = DicomCharacterSets.Utf8, Index = 5, Syntax = DicomTransferSyntax.ImplicitVRLittleEndian },
                new { Name = "dx_8bit_1024_iso100", Modality = "DX", Rows = 1024, Columns = 1024, Bits = 8, Images = 1, Charset = DicomCharacterSets.Latin1, Index = 0, Syntax = DicomTransferSyntax.ExplicitVRLittleEndian },
                new { Name = "cr_small_128x200", Modality = "CR", Rows = 128, Columns = 200, Bits = 12, Images = 1, Charset = DicomCharacterSets.Latin1, Index = 0, Syntax = DicomTransferSyntax.ExplicitVRLittleEndian },
            };

            foreach (var s in samples)
            {
                var request = MakeRequest(s.Modality, images: s.Images, rows: s.Rows, columns: s.Columns, bits: s.Bits, charset: s.Charset, overrides: overrides);
                var image = new ExamGenerator(request).GetImages(CancellationToken.None).Skip(s.Index).First();
                var dicomPath = Path.Combine(folder, s.Name + ".dcm");
                TransferSyntaxConverter.Convert(image.File, s.Syntax).Save(dicomPath);

                var read = DicomFile.Open(dicomPath);
                var uncompressed = read.Dataset.InternalTransferSyntax == DicomTransferSyntax.JPEGProcess14SV1 ? read.Clone(DicomTransferSyntax.ExplicitVRLittleEndian) : read;
                WritePng(uncompressed.Dataset, Path.Combine(folder, s.Name + ".png"));
                Console.WriteLine($"{s.Name}: name in file = {read.Dataset.GetString(DicomTag.PatientName)}, charset = {read.Dataset.GetString(DicomTag.SpecificCharacterSet)}");
            }
        }

        private static void WritePng(DicomDataset dataset, string path)
        {
            var pixelData = DicomPixelData.Create(dataset);
            var frame = pixelData.GetFrame(0).Data;
            var values = FrameValues(frame, pixelData.BitsStored);
            var intercept = dataset.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
            var center = dataset.GetSingleValue<double>(DicomTag.WindowCenter);
            var width = dataset.GetSingleValue<double>(DicomTag.WindowWidth);

            using (var bitmap = new Bitmap(pixelData.Width, pixelData.Height, PixelFormat.Format24bppRgb))
            {
                var locked = bitmap.LockBits(new Rectangle(0, 0, pixelData.Width, pixelData.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                var row = new byte[Math.Abs(locked.Stride)];
                for (var y = 0; y < pixelData.Height; y++)
                {
                    for (var x = 0; x < pixelData.Width; x++)
                    {
                        var hu = values[y * pixelData.Width + x] + intercept;
                        var level = Math.Max(0, Math.Min(255, (int)((hu - (center - width / 2)) / width * 255)));
                        row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = (byte)level;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(locked.Scan0, y * locked.Stride), row.Length);
                }
                bitmap.UnlockBits(locked);
                bitmap.Save(path, ImageFormat.Png);
            }
        }
    }
}
