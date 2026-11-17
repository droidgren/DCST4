using DCST4.Core.Files;
using DCST4.Core.Generation;
using DCST4.Core.Generation.Modalities;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>1.13.0: existing files from a folder or a DICOMDIR, re-identified as test patients or sent unchanged.</summary>
    internal static class FolderSourceTests
    {
        private const string Root = "1.2.826.0.1.3680043.10.999";
        private const string Vendor = "ORIGVENDOR";
        /// <summary>Under a vendor's root, not DICOM's, so re-identifying must replace it.</summary>
        internal const string VendorUid = "1.2.840.113619.2.55.3.1234567890.1";
        private static readonly DateTime Today = new DateTime(2026, 11, 17);

        private static Action<string, bool, string> _check;
        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        /// <summary>The files of the fixture, as written. Patient A has a native and a JPEG Lossless series, patient B one RGB Secondary Capture in Implicit VR.</summary>
        internal sealed class Fixture
        {
            public string Folder;
            public string[] CtNative = new string[3];
            public string[] CtJpeg = new string[2];
            public string Us;
            public string Duplicate;
            public string Text;
        }

        public static async Task Run(int port, string temp, Action<string, bool, string> check)
        {
            _check = check;
            Console.WriteLine("\nFiles from a folder");
            var fixture = MakeFixture(Path.Combine(temp, "folder-source"));

            var scan = TestScan(fixture);
            TestReidentify(scan, fixture);
            TestWatermark(scan, fixture);
            TestKeep(scan);
            TestQuestion(scan);
            await TestSend(port, scan);
            await TestDicomDir(temp, scan);
            // Each start of the command line costs about two seconds, so only the two ways of using it are run.
            await TestCli(port, temp, fixture);
        }

        // ---------------------------------------------------------------- fixture

        /// <summary>Real-looking files with a modality's habits: a vendor private block, a reference between instances, staff names.</summary>
        internal static Fixture MakeFixture(string folder)
        {
            var fixture = new Fixture { Folder = folder };
            var a = Path.Combine(folder, "A");
            var b = Path.Combine(folder, "B", "sub");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);

            var ct = ModalityCatalog.Find("CT");
            var identity = new IdentityGenerator().Generate(null, ct, ct.Presets[0]);
            var native = Generate(ct, identity, 3, DicomTransferSyntax.ExplicitVRLittleEndian);
            var jpeg = Generate(ct, identity, 2, DicomTransferSyntax.JPEGProcess14SV1);
            for (var i = 0; i < native.Count; i++)
            {
                SetPatient(native[i].Dataset, "ORIG-A", "ORIGINAL^ALPHA", "19800315", "F", "ACC-A");
                native[i].Dataset.AddOrUpdate(DicomTag.PatientAge, "046Y");
                native[i].Dataset.Add(DicomVR.LO, new DicomTag(0x0019, 0x0010), Vendor);
                native[i].Dataset.Add(new DicomUniqueIdentifier(new DicomTag(0x0019, 0x1010), VendorUid));
                native[i].Dataset.Add(new DicomLongString(new DicomTag(0x0019, 0x1011), "VENDOR TEXT"));
                fixture.CtNative[i] = Save(native[i], Path.Combine(a, "S1_" + (i + 1) + ".dcm"));
            }
            for (var i = 0; i < jpeg.Count; i++)
            {
                SetPatient(jpeg[i].Dataset, "ORIG-A", "ORIGINAL^ALPHA", "19800315", "F", "ACC-A");
                jpeg[i].Dataset.AddOrUpdate(DicomTag.SeriesNumber, "2");
                var reference = new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, native[0].Dataset.GetString(DicomTag.SOPClassUID) },
                    { DicomTag.ReferencedSOPInstanceUID, native[0].Dataset.GetString(DicomTag.SOPInstanceUID) },
                };
                jpeg[i].Dataset.AddOrUpdate(new DicomSequence(DicomTag.ReferencedImageSequence, reference));
                fixture.CtJpeg[i] = Save(jpeg[i], Path.Combine(a, "S2_" + (i + 1) + ".dcm"));
            }

            var us = ModalityCatalog.Find("OT");
            var usFile = Generate(us, new IdentityGenerator().Generate(null, us, us.Presets[0]), 1, DicomTransferSyntax.ImplicitVRLittleEndian)[0];
            SetPatient(usFile.Dataset, "ORIG-B", "ORIGINAL^BETA", "20100601", "M", "ACC-B");
            usFile.Dataset.Add(DicomVR.LO, new DicomTag(0x0029, 0x0010), Vendor);
            usFile.Dataset.Add(new DicomUniqueIdentifier(new DicomTag(0x0029, 0x1010), VendorUid));
            // No extension, as on media.
            fixture.Us = Save(usFile, Path.Combine(b, "US1"));

            fixture.Duplicate = Path.Combine(a, "zz_copy.dcm");
            File.Copy(fixture.CtNative[0], fixture.Duplicate);
            fixture.Text = Path.Combine(folder, "readme.txt");
            File.WriteAllText(fixture.Text, "Not a DICOM file.");
            return fixture;
        }

        private static List<DicomFile> Generate(ModalityProfile modality, ExamIdentity identity, int images, DicomTransferSyntax syntax)
        {
            var request = new ExamRequest
            {
                Modality = modality,
                Preset = modality.Presets[0],
                Identity = identity,
                SeriesCount = 1,
                ImagesPerSeries = images,
                Rows = 64,
                Columns = 64,
                BitsStored = modality.DefaultBitDepth,
                CharacterSet = DicomCharacterSets.Latin1,
            };
            return new ExamGenerator(request).GetImages(CancellationToken.None)
                .Select(i => TransferSyntaxConverter.Convert(i.File, syntax)).ToList();
        }

        private static void SetPatient(DicomDataset dataset, string id, string name, string birth, string sex, string accession)
        {
            dataset.AddOrUpdate(DicomTag.PatientID, id);
            dataset.AddOrUpdate(DicomTag.PatientName, name);
            dataset.AddOrUpdate(DicomTag.PatientBirthDate, birth);
            dataset.AddOrUpdate(DicomTag.PatientSex, sex);
            dataset.AddOrUpdate(DicomTag.AccessionNumber, accession);
            dataset.AddOrUpdate(DicomTag.OtherPatientIDsRETIRED, "OLD-" + id);
            dataset.AddOrUpdate(DicomTag.OperatorsName, "TECH^ONE");
        }

        private static string Save(DicomFile file, string path)
        {
            file.Save(path);
            return path;
        }

        private static FolderSendOptions Options(bool reidentify = true, string nonce = "run-1", TestPatientNaming naming = TestPatientNaming.MarkedTestName) => new FolderSendOptions
        {
            Reidentify = reidentify,
            UidRoot = Root,
            RunNonce = nonce,
            Seed = 5,
            Today = Today,
            PatientNaming = naming,
        };

        private static Dictionary<string, DicomDataset> Images(FolderScan scan, FolderSendOptions options, out FolderImageSource source)
        {
            source = new FolderImageSource(scan, options);
            return source.GetImages(CancellationToken.None).ToDictionary(i => i.SourcePath, i => i.File.Dataset, StringComparer.OrdinalIgnoreCase);
        }

        private static DicomDataset Original(string path) => DicomFile.Open(path).Dataset;

        // ---------------------------------------------------------------- scan

        private static FolderScan TestScan(Fixture fixture)
        {
            var scan = FolderScanner.Scan(fixture.Folder, true, CancellationToken.None);
            Check("Scan: six files of two patients, two studies and three series", scan.Files.Count == 6 && scan.PatientCount == 2 && scan.StudyCount == 2 && scan.SeriesCount == 3, scan.Summary);
            Check("Scan: a file without an extension is read", scan.Files.Any(f => f.Path == fixture.Us));
            Check("Scan: a text file is skipped as not DICOM", scan.Skipped.Any(s => s.Path == fixture.Text && s.Reason == "Not a DICOM file."), string.Join("; ", scan.Skipped));
            Check("Scan: a second copy of an instance is skipped, naming the first", scan.Skipped.Any(s => s.Path == fixture.Duplicate && s.Reason.Contains(fixture.CtNative[0])), string.Join("; ", scan.Skipped));
            var order = scan.Files.Select(f => f.Path).ToList();
            Check("Scan: sent by patient, study, series and instance", order.Take(5).SequenceEqual(fixture.CtNative.Concat(fixture.CtJpeg)) && order[5] == fixture.Us, string.Join(", ", order.Select(Path.GetFileName)));
            Check("Scan: the syntaxes and the compressed files are counted", scan.TransferSyntaxes.Count == 3 && scan.CompressedCount == 2 && scan.SyntaxSummary.StartsWith("Explicit VR Little Endian (3)", StringComparison.Ordinal), scan.SyntaxSummary);
            Check("Scan: the summary says nothing about the patients but their number", !scan.Summary.Contains("ORIG") && scan.Summary.Contains("2 patients"), scan.Summary);

            var flat = FolderScanner.Scan(Path.Combine(fixture.Folder, "B"), false, CancellationToken.None);
            Check("Scan: without subfolders, only the folder's own files", flat.Files.Count == 0 && FolderScanner.Scan(Path.Combine(fixture.Folder, "B"), true, CancellationToken.None).Files.Count == 1);

            var thrown = false;
            try { FolderScanner.Scan(fixture.Text, true, CancellationToken.None); }
            catch (ArgumentException) { thrown = true; }
            Check("Scan: a file that is not a DICOMDIR is refused", thrown);
            return scan;
        }

        // ---------------------------------------------------------------- re-identify

        private static void TestReidentify(FolderScan scan, Fixture fixture)
        {
            var images = Images(scan, Options(), out var source);
            var registry = TestPersonnummerRegistry.Default;
            var all = images.Values.ToList();

            Check("Re-identify: every Patient ID is a test personnummer", all.All(d => registry.Contains(d.GetString(DicomTag.PatientID))), string.Join(", ", all.Select(d => d.GetString(DicomTag.PatientID))));
            Check("Re-identify: the two patients are two test patients", all.Select(d => d.GetString(DicomTag.PatientID)).Distinct().Count() == 2 && source.Reidentifier.Patients.Count == 2);
            Check("Re-identify: birth date and sex follow the personnummer", all.All(d =>
            {
                Personnummer.TryParse(d.GetString(DicomTag.PatientID), out var number);
                return d.GetString(DicomTag.PatientBirthDate) == number.BirthDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture) && d.GetString(DicomTag.PatientSex) == number.Sex;
            }));
            var a = images[fixture.CtNative[0]];
            var b = images[fixture.Us];
            Check("Re-identify: the sex of each patient is kept", a.GetString(DicomTag.PatientSex) == "F" && b.GetString(DicomTag.PatientSex) == "M");
            var studyDate = DateTime.ParseExact(a.GetString(DicomTag.StudyDate), "yyyyMMdd", CultureInfo.InvariantCulture);
            Personnummer.TryParse(a.GetString(DicomTag.PatientID), out var aNumber);
            var aAge = TestPersonnummerRegistry.AgeOn(aNumber.BirthDate, studyDate);
            var originalAge = TestPersonnummerRegistry.AgeOn(new DateTime(1980, 3, 15), studyDate);
            Check("Re-identify: about as old on the study date, and born before it", Math.Abs(aAge - originalAge) <= 2 && aNumber.BirthDate <= studyDate, aAge + " vs " + originalAge);
            Check("Re-identify: Patient's Age follows the new birth date", a.GetString(DicomTag.PatientAge) == FileReidentifier.Age(aNumber.BirthDate, studyDate), a.GetString(DicomTag.PatientAge));
            Check("Re-identify: the marked name DCST4^TEST^<number>", all.All(d => d.GetString(DicomTag.PatientName).StartsWith(TestPatientNames.MarkedPrefix, StringComparison.Ordinal)));
            Check("Re-identify: other patient IDs are removed, other names replaced", all.All(d => !d.Contains(DicomTag.OtherPatientIDsRETIRED) && d.GetString(DicomTag.OperatorsName) == FileReidentifier.OtherPersonName));
            Check("Re-identify: a new accession number, the same within a study", a.GetString(DicomTag.AccessionNumber) != "ACC-A" && images[fixture.CtJpeg[0]].GetString(DicomTag.AccessionNumber) == a.GetString(DicomTag.AccessionNumber)
                && b.GetString(DicomTag.AccessionNumber) != a.GetString(DicomTag.AccessionNumber));
            Check("Re-identify: Manufacturer and Image Comments are DCST4's", all.All(d => d.GetString(DicomTag.Manufacturer) == FileReidentifier.Manufacturer && d.GetString(DicomTag.ImageComments) == DicomImageBuilder.TestImageComment));

            var swedish = Images(scan, Options(naming: TestPatientNaming.RegistrySwedishName), out _);
            Check("Re-identify: a Swedish name under RegistrySwedishName", swedish.Values.All(d => !d.GetString(DicomTag.PatientName).StartsWith("DCST4", StringComparison.Ordinal) && d.GetString(DicomTag.PatientName).Contains("^")));

            // UIDs.
            var original = scan.Files.ToDictionary(f => f.Path, f => Original(f.Path), StringComparer.OrdinalIgnoreCase);
            var keys = new[] { DicomTag.SOPInstanceUID, DicomTag.StudyInstanceUID, DicomTag.SeriesInstanceUID, DicomTag.FrameOfReferenceUID };
            Check("Re-identify: SOP Instance, Study, Series and Frame of Reference UIDs are new, under the root", images.All(pair => keys.Where(k => original[pair.Key].Contains(k)).All(k =>
                pair.Value.GetString(k) != original[pair.Key].GetString(k) && pair.Value.GetString(k).StartsWith(Root + ".", StringComparison.Ordinal))));
            Check("Re-identify: SOP Class UID is kept", images.All(pair => pair.Value.GetString(DicomTag.SOPClassUID) == original[pair.Key].GetString(DicomTag.SOPClassUID)));
            Check("Re-identify: the two CT series stay one study, as two series", images[fixture.CtJpeg[1]].GetString(DicomTag.StudyInstanceUID) == a.GetString(DicomTag.StudyInstanceUID)
                && images[fixture.CtJpeg[1]].GetString(DicomTag.SeriesInstanceUID) != a.GetString(DicomTag.SeriesInstanceUID)
                && images[fixture.CtNative[2]].GetString(DicomTag.SeriesInstanceUID) == a.GetString(DicomTag.SeriesInstanceUID));
            var referenced = images[fixture.CtJpeg[0]].GetSequence(DicomTag.ReferencedImageSequence).Items[0];
            Check("Re-identify: a reference to another file points at its new UID", referenced.GetString(DicomTag.ReferencedSOPInstanceUID) == a.GetString(DicomTag.SOPInstanceUID)
                && referenced.GetString(DicomTag.ReferencedSOPClassUID) == a.GetString(DicomTag.SOPClassUID));
            Check("Re-identify: a private UI value is replaced in an explicit VR file", Private(a, 0x0019, 0x1010) == source.Reidentifier.MapUid(VendorUid) && Private(a, 0x0019, 0x1010) != VendorUid, Private(a, 0x0019, 0x1010));
            Check("Re-identify: a UID in a private value without VR, from an implicit VR file, is replaced too", Private(b, 0x0029, 0x1010) == source.Reidentifier.MapUid(VendorUid), Private(b, 0x0029, 0x1010));
            Check("Re-identify: other private values are kept", Private(a, 0x0019, 0x1011) == "VENDOR TEXT");

            var again = Images(scan, Options(nonce: "run-2"), out _);
            Check("Re-identify: a second run gives other UIDs, so it is never a duplicate", again.All(pair => pair.Value.GetString(DicomTag.SOPInstanceUID) != images[pair.Key].GetString(DicomTag.SOPInstanceUID)));
            Check("Re-identify: the files on disk are unchanged", Original(fixture.CtNative[0]).GetString(DicomTag.PatientID) == "ORIG-A");
        }

        /// <summary>The value of a private element by its tag number, as text, whatever its VR.</summary>
        private static string Private(DicomDataset dataset, ushort group, ushort element)
        {
            var item = dataset.FirstOrDefault(i => i.Tag.Group == group && i.Tag.Element == element) as DicomElement;
            return item == null ? null : Encoding.ASCII.GetString(item.Buffer.Data).TrimEnd('\0', ' ');
        }

        private static void TestWatermark(FolderScan scan, Fixture fixture)
        {
            var images = Images(scan, Options(), out var source);
            var nativeBefore = Original(fixture.CtNative[0]).GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            var nativeAfter = images[fixture.CtNative[0]].GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            var changed = 0;
            for (var i = 0; i < nativeBefore.Length; i++) if (nativeBefore[i] != nativeAfter[i]) changed++;
            Check("Watermark: burned into native pixels, the size kept", nativeAfter.Length == nativeBefore.Length && changed > 0 && changed < nativeBefore.Length / 2, changed + " of " + nativeBefore.Length + " bytes");

            var rgbBefore = Original(fixture.Us).GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            var rgbAfter = images[fixture.Us].GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            Check("Watermark: burned into RGB pixels too", rgbAfter.Length == rgbBefore.Length && !rgbAfter.SequenceEqual(rgbBefore));

            var fragmentsBefore = Original(fixture.CtJpeg[0]).GetDicomItem<DicomFragmentSequence>(DicomTag.PixelData).Fragments.Select(f => f.Data).ToList();
            var fragmentsAfter = images[fixture.CtJpeg[0]].GetDicomItem<DicomFragmentSequence>(DicomTag.PixelData).Fragments.Select(f => f.Data).ToList();
            Check("Watermark: compressed pixel data is sent byte for byte as in the file", fragmentsBefore.Count == fragmentsAfter.Count && fragmentsBefore.Zip(fragmentsAfter, (x, y) => x.SequenceEqual(y)).All(s => s));
            Check("Watermark: counted, with the reason for the files without it", source.Watermarks.TryGetValue(WatermarkOutcome.Drawn, out var drawn) && drawn == 4
                && source.Watermarks.TryGetValue(WatermarkOutcome.Compressed, out var compressed) && compressed == 2, string.Join(", ", source.Watermarks.Select(w => w.Key + " " + w.Value)));
        }

        private static void TestKeep(FolderScan scan)
        {
            var images = Images(scan, Options(reidentify: false), out var source);
            Check("Send unchanged: no re-identifier, and every file exactly as on disk", source.Reidentifier == null && images.All(pair => Bytes(pair.Value).SequenceEqual(Bytes(Original(pair.Key)))));
        }

        private static byte[] Bytes(DicomDataset dataset)
        {
            using (var stream = new MemoryStream())
            {
                new DicomFile(dataset.Clone()).Save(stream);
                return stream.ToArray();
            }
        }

        private static void TestQuestion(FolderScan scan)
        {
            var reidentify = string.Join("\n", FilesDescription.Describe(scan, true));
            var keep = string.Join("\n", FilesDescription.Describe(scan, false));
            Check("Description: the patients, and the files with and without a watermark", reidentify.Contains("Each of the 2 patients") && reidentify.Contains("4 files with uncompressed pixels get")
                && reidentify.Contains("2 compressed files keep their original pixel data"), reidentify);
            Check("Description, unchanged: says real patients go", keep.Contains("real patient names"), keep);
            Check("Description: names no patient", !reidentify.Contains("ORIG") && !keep.Contains("ORIG"));
        }

        // ---------------------------------------------------------------- send and save

        private static StoreRunOptions SendOptions(int port) => new StoreRunOptions
        {
            Send = true,
            TransferSyntax = null,
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 30 },
        };

        private static async Task TestSend(int port, FolderScan scan)
        {
            Scp.Reset();
            var summary = await new StoreRun(new FolderImageSource(scan, Options()), SendOptions(port)).ExecuteAsync(CancellationToken.None);
            var received = Scp.Received.ToList();
            Check("Send: every file stored", summary.Success == 6 && summary.FatalError == null && received.Count == 6, summary + " " + summary.FatalError + " " + string.Join("; ", summary.Results.Where(r => r.State != StoreState.Success).Select(r => r.Description + " [" + r.AcceptedTransferSyntax + "]")));
            Check("Send: each file in its own syntax, the compressed ones compressed", received.Count(r => r.Syntax == DicomTransferSyntax.JPEGProcess14SV1) == 2
                && received.Count(r => r.Syntax == DicomTransferSyntax.ImplicitVRLittleEndian) == 1 && received.Count(r => r.Syntax == DicomTransferSyntax.ExplicitVRLittleEndian) == 3,
                string.Join(", ", received.Select(r => r.Syntax.UID.Name)));
            Check("Send: the results name the source file, and the series are numbered through the run", summary.Results.All(r => scan.Files.Any(f => f.Path == r.SourcePath))
                && summary.Results.Select(r => r.SeriesNumber).Distinct().OrderBy(n => n).SequenceEqual(new[] { 1, 2, 3 }));
            Check("Send: what arrived is re-identified", received.All(r => TestPersonnummerRegistry.Default.Contains(r.Dataset.GetString(DicomTag.PatientID))));

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1 };
            var refused = await new StoreRun(new FolderImageSource(scan, Options()), SendOptions(port)).ExecuteAsync(CancellationToken.None);
            Check("Send: the last file's syntax, not accepted, stops the run before anything is sent", refused.FailureKind == ConnectivityFailure.ContextNotAccepted
                && Scp.Received.IsEmpty && (refused.FatalError ?? string.Empty).Contains("Implicit VR"), refused.FatalError);

            Scp.Reset();
            var unchanged = await new StoreRun(new FolderImageSource(scan, Options(reidentify: false)), SendOptions(port)).ExecuteAsync(CancellationToken.None);
            Check("Send unchanged: the original patients and UIDs arrive", unchanged.Success == 6 && Scp.Received.Count(r => r.Dataset.GetString(DicomTag.PatientID) == "ORIG-A") == 5
                && Scp.Received.Any(r => r.Dataset.GetString(DicomTag.SOPInstanceUID) == scan.Files[0].SopInstanceUid));

            Scp.Reset();
            var options = SendOptions(port);
            options.TransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
            var transcoded = await new StoreRun(new FolderImageSource(scan, Options()), options).ExecuteAsync(CancellationToken.None);
            Check("Send in one chosen syntax: the compressed files are decoded first", transcoded.Success == 6 && Scp.Received.All(r => r.Syntax == DicomTransferSyntax.ExplicitVRLittleEndian), transcoded.ToString());
            Scp.Reset();
        }

        private static async Task TestCli(int port, string temp, Fixture fixture)
        {
            var folder = Path.Combine(temp, "folder-source-cli");
            var logs = Path.Combine(folder, "logs");
            Directory.CreateDirectory(folder);
            var profile = Path.Combine(folder, "target.json");
            TestSuiteTests.MockProfile(port).Save(profile);

            Scp.Reset();
            var sent = await TestSuiteTests.RunCli("send-files", "--source", fixture.Folder, "--profile", profile, "--log-dir", logs);
            Check("send-files: exit 0, every file stored, re-identified by default", sent.ExitCode == 0 && Scp.Received.Count == 6
                && Scp.Received.All(r => TestPersonnummerRegistry.Default.Contains(r.Dataset.GetString(DicomTag.PatientID))), sent.Output);
            Check("send-files: prints what it found, what it does and the test patients, and no real patient", sent.Output.Contains("6 files in 3 series") && sent.Output.Contains("Test patients: ")
                && sent.Output.Contains("2 skipped") && !sent.Output.Contains("ORIGINAL^"), sent.Output);

            Scp.Reset();
            var saved = Path.Combine(folder, "saved");
            var kept = await TestSuiteTests.RunCli("send-files", "--source", fixture.Folder, "--save", saved, "--keep-identity", "--log-dir", logs);
            var savedScan = FolderScanner.Scan(saved, true, CancellationToken.None);
            Check("send-files --save --keep-identity: saved unchanged, nothing sent", kept.ExitCode == 0 && Scp.Received.IsEmpty && savedScan.Files.Count == 6
                && savedScan.Files.All(f => DicomFile.Open(f.Path).Dataset.GetString(DicomTag.PatientID).StartsWith("ORIG-", StringComparison.Ordinal)), kept.Output);

            Scp.Reset();
        }

        private static async Task TestDicomDir(string temp, FolderScan scan)
        {
            var folder = Path.Combine(temp, "folder-source-fileset");
            var saved = await new StoreRun(new FolderImageSource(scan, Options()), new StoreRunOptions { Send = false, Save = true, SaveFolder = folder, TransferSyntax = null }).ExecuteAsync(CancellationToken.None);
            Check("Save: every file saved as a File-set with a DICOMDIR", saved.Saved == 6 && saved.DicomDirPath != null, saved + " " + saved.DicomDirError);

            var throughDir = FolderScanner.Scan(folder, true, CancellationToken.None);
            Check("DICOMDIR: a folder with one at its top is read through it, and it is not sent", throughDir.DicomDirPath != null && throughDir.Files.Count == 6
                && throughDir.Files.All(f => !f.Path.EndsWith("DICOMDIR", StringComparison.OrdinalIgnoreCase)), throughDir.Summary);
            var chosen = FolderScanner.Scan(saved.DicomDirPath, true, CancellationToken.None);
            Check("DICOMDIR: the file itself can be chosen", chosen.DicomDirPath == saved.DicomDirPath && chosen.Files.Count == 6 && chosen.StudyCount == 2 && chosen.SeriesCount == 3, chosen.Summary);

            File.Delete(chosen.Files[0].Path);
            var missing = FolderScanner.Scan(saved.DicomDirPath, true, CancellationToken.None);
            Check("DICOMDIR: a listed file that is gone is skipped, with the reason", missing.Files.Count == 5 && missing.Skipped.Any(s => s.Reason.StartsWith("Listed in the DICOMDIR", StringComparison.Ordinal)), string.Join("; ", missing.Skipped));
        }
    }
}
