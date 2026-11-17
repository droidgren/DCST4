using FellowOakDicom;
using FellowOakDicom.Media;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Files
{
    /// <summary>One DICOM file found in a folder, described by its header. The pixels are not read.</summary>
    public sealed class ScannedFile
    {
        public string Path { get; internal set; }
        public long Bytes { get; internal set; }
        public string SopClassUid { get; internal set; }
        public string SopInstanceUid { get; internal set; }
        public DicomTransferSyntax TransferSyntax { get; internal set; }
        public string Modality { get; internal set; }
        /// <summary>Which patient the file belongs to, from Patient ID, else name and birth date. Never logged.</summary>
        internal string PatientKey { get; set; }
        internal string PatientSex { get; set; }
        /// <summary>The patient's age on the study date, when the file has the birth date. Never logged.</summary>
        internal int? PatientAge { get; set; }
        public string StudyInstanceUid { get; internal set; }
        public string SeriesInstanceUid { get; internal set; }
        /// <summary>Study Date, else Series, Acquisition or Content Date. Null if the file has none.</summary>
        public DateTime? StudyDate { get; internal set; }
        public int? SeriesNumber { get; internal set; }
        public int? InstanceNumber { get; internal set; }
        public bool HasPixelData { get; internal set; }
        /// <summary>Compressed pixel data, which keeps its original stream and gets no watermark.</summary>
        public bool IsEncapsulated => HasPixelData && TransferSyntax.IsEncapsulated;
        public int Frames { get; internal set; }
        public string PhotometricInterpretation { get; internal set; }
        /// <summary>Burned In Annotation is YES: the pixels may show who the patient is, which re-identification cannot change.</summary>
        public bool BurnedInAnnotation { get; internal set; }
    }

    /// <summary>A file that will not be sent, and why.</summary>
    public sealed class SkippedFile
    {
        public SkippedFile(string path, string reason)
        {
            Path = path;
            Reason = reason;
        }

        public string Path { get; }
        public string Reason { get; }
        public override string ToString() => Path + ": " + Reason;
    }

    /// <summary>What a folder or a DICOMDIR holds, in the order the files are sent.</summary>
    public sealed class FolderScan
    {
        internal FolderScan(string source, string dicomDirPath, List<ScannedFile> files, List<SkippedFile> skipped)
        {
            Source = source;
            DicomDirPath = dicomDirPath;
            Files = files;
            Skipped = skipped;
        }

        /// <summary>The folder or DICOMDIR that was chosen.</summary>
        public string Source { get; }
        /// <summary>The DICOMDIR the files were listed by, or null when the folder was searched.</summary>
        public string DicomDirPath { get; }
        /// <summary>By patient, study, series number, instance number and path.</summary>
        public IReadOnlyList<ScannedFile> Files { get; }
        public IReadOnlyList<SkippedFile> Skipped { get; }

        public int PatientCount => Files.Select(f => f.PatientKey).Distinct(StringComparer.Ordinal).Count();
        public int StudyCount => Files.Select(f => f.StudyInstanceUid).Distinct(StringComparer.Ordinal).Count();
        public int SeriesCount => Files.Select(f => f.SeriesInstanceUid).Distinct(StringComparer.Ordinal).Count();
        public long TotalBytes => Files.Sum(f => f.Bytes);
        public int CompressedCount => Files.Count(f => f.IsEncapsulated);
        public int BurnedInAnnotationCount => Files.Count(f => f.BurnedInAnnotation);
        public IReadOnlyList<DicomTransferSyntax> TransferSyntaxes => Files.Select(f => f.TransferSyntax).Distinct().ToList();
        public IReadOnlyList<string> SopClasses => Files.Select(f => f.SopClassUid).Distinct(StringComparer.Ordinal).ToList();
        public IReadOnlyList<string> Modalities => Files.Select(f => f.Modality).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();

        /// <summary>The name of the folder, or of the folder a DICOMDIR is in, for descriptions.</summary>
        public string Name
        {
            get
            {
                var folder = DicomDirPath != null || File.Exists(Source) ? System.IO.Path.GetDirectoryName(DicomDirPath ?? Source) : Source;
                var name = System.IO.Path.GetFileName((folder ?? string.Empty).TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(name) ? folder : name;
            }
        }

        /// <summary>One line, for the Files tab and the log: what is there and how large it is. Says nothing about the patient.</summary>
        public string Summary => string.Format(CultureInfo.InvariantCulture,
            "{0} {1} in {2} {3}, {4} {5} and {6} {7}, {8}",
            Files.Count, Plural(Files.Count, "file", "files"), SeriesCount, Plural(SeriesCount, "series", "series"),
            StudyCount, Plural(StudyCount, "study", "studies"), PatientCount, Plural(PatientCount, "patient", "patients"), FormatBytes(TotalBytes));

        /// <summary>The syntaxes by name, the most used first.</summary>
        public string SyntaxSummary => string.Join(", ", Files.GroupBy(f => f.TransferSyntax).OrderByDescending(g => g.Count())
            .Select(g => string.Format(CultureInfo.InvariantCulture, "{0} ({1})", SyntaxName(g.Key), g.Count())));

        /// <summary>The short name of the Transfer syntax list where the syntax is in it, otherwise fo-dicom's name.</summary>
        public static string SyntaxName(DicomTransferSyntax syntax) =>
            Pipeline.TransferSyntaxOption.All.FirstOrDefault(o => o.Syntax == syntax)?.Name ?? syntax.UID.Name;

        internal static string Plural(int count, string one, string many) => count == 1 ? one : many;

        public static string FormatBytes(long bytes) =>
            bytes >= 1024L * 1024 * 1024 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", bytes / (1024.0 * 1024 * 1024))
            : bytes >= 1024 * 1024 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / (1024.0 * 1024))
            : string.Format(CultureInfo.InvariantCulture, "{0:0} KB", Math.Ceiling(bytes / 1024.0));
    }

    /// <summary>
    /// Finds the DICOM files of a folder or a DICOMDIR and reads their headers. A DICOMDIR at the top of the folder, or chosen
    /// itself, lists the files, as a CD or a USB export from a PACS has them. Without one, every file in the folder is tried,
    /// whatever its name, since files on media often have no extension. Nothing is written.
    /// </summary>
    public static class FolderScanner
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(FolderScanner));
        private const string DicomDirFileName = "DICOMDIR";
        private const string DirectoryStorageUid = "1.2.840.10008.1.3.10";

        /// <param name="path">A folder, or a DICOMDIR file.</param>
        /// <param name="includeSubfolders">When the folder is searched: also the folders in it. A DICOMDIR always lists what it lists.</param>
        /// <param name="progress">Told the files read so far and the files found, now and then.</param>
        public static FolderScan Scan(string path, bool includeSubfolders, CancellationToken token, Action<int, int> progress = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Select a folder or a DICOMDIR.", nameof(path));
            path = path.Trim();
            var skipped = new List<SkippedFile>();

            string dicomDir = null;
            List<string> candidates;
            if (File.Exists(path))
            {
                if (!IsDicomDir(path)) throw new ArgumentException($"{path} is not a DICOMDIR. Select a folder, or a DICOMDIR file.", nameof(path));
                dicomDir = path;
            }
            else if (Directory.Exists(path))
            {
                var top = Path.Combine(path, DicomDirFileName);
                if (File.Exists(top) && IsDicomDir(top)) dicomDir = top;
            }
            else
            {
                throw new DirectoryNotFoundException($"{path} does not exist.");
            }

            candidates = dicomDir != null ? FilesOfDicomDir(dicomDir, skipped, token) : FilesOfFolder(path, includeSubfolders, token);

            var files = new List<ScannedFile>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < candidates.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i % 50 == 0) progress?.Invoke(i, candidates.Count);
                var file = candidates[i];
                var scanned = Read(file, dicomDir != null, out var reason);
                if (scanned == null)
                {
                    if (reason != null) skipped.Add(new SkippedFile(file, reason));
                    continue;
                }
                if (seen.TryGetValue(scanned.SopInstanceUid, out var first))
                {
                    skipped.Add(new SkippedFile(file, "The same SOP Instance UID as " + first + ", which is sent instead."));
                    continue;
                }
                seen[scanned.SopInstanceUid] = file;
                files.Add(scanned);
            }
            progress?.Invoke(candidates.Count, candidates.Count);

            var ordered = Order(files);
            var scan = new FolderScan(path, dicomDir, ordered, skipped);
            Logger.Information("Read {source}{dicomdir}: {summary}. {skipped} skipped. Syntaxes: {syntaxes}",
                path, dicomDir == null ? string.Empty : " through its DICOMDIR", scan.Summary, skipped.Count, scan.SyntaxSummary);
            return scan;
        }

        /// <summary>A study keeps its files together, and the series of a study follow each other by number.</summary>
        private static List<ScannedFile> Order(List<ScannedFile> files)
        {
            var patientOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            var studyOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!patientOrder.ContainsKey(file.PatientKey)) patientOrder[file.PatientKey] = patientOrder.Count;
                if (!studyOrder.ContainsKey(file.StudyInstanceUid)) studyOrder[file.StudyInstanceUid] = studyOrder.Count;
            }
            return files
                .OrderBy(f => patientOrder[f.PatientKey])
                .ThenBy(f => studyOrder[f.StudyInstanceUid])
                .ThenBy(f => f.SeriesNumber ?? int.MaxValue).ThenBy(f => f.SeriesInstanceUid, StringComparer.Ordinal)
                .ThenBy(f => f.InstanceNumber ?? int.MaxValue).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsDicomDir(string path)
        {
            if (string.Equals(Path.GetFileName(path), DicomDirFileName, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var file = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                return file.FileMetaInfo?.GetSingleValueOrDefault(DicomTag.MediaStorageSOPClassUID, string.Empty) == DirectoryStorageUid;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static List<string> FilesOfFolder(string folder, bool includeSubfolders, CancellationToken token)
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(folder);
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    files.AddRange(Directory.EnumerateFiles(current));
                    if (includeSubfolders)
                    {
                        foreach (var sub in Directory.EnumerateDirectories(current)) pending.Push(sub);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    Logger.Warning("Could not read the folder {folder}: {reason}", current, ex.Message);
                }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>Every record with a Referenced File ID, at any level: images, but also reports, documents and presentation states.</summary>
        private static List<string> FilesOfDicomDir(string dicomDir, List<SkippedFile> skipped, CancellationToken token)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(dicomDir));
            var directory = DicomDirectory.Open(dicomDir);
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<DicomDirectoryRecord>(directory.RootDirectoryRecordCollection.Reverse());
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var record = pending.Pop();
                if (record.LowerLevelDirectoryRecordCollection != null)
                {
                    foreach (var child in record.LowerLevelDirectoryRecordCollection.Reverse()) pending.Push(child);
                }
                if (!record.Contains(DicomTag.ReferencedFileID)) continue;

                var parts = record.GetValues<string>(DicomTag.ReferencedFileID).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
                if (parts.Length == 0) continue;
                var file = Path.Combine(root, Path.Combine(parts));
                if (!seen.Add(file)) continue;
                if (File.Exists(file)) files.Add(file);
                else skipped.Add(new SkippedFile(file, "Listed in the DICOMDIR, but the file is not there."));
            }
            return files;
        }

        /// <summary>The header of one file, or null with the reason it is not sent. A DICOMDIR found in a folder is left out without a reason.</summary>
        private static ScannedFile Read(string path, bool listedByDicomDir, out string reason)
        {
            reason = null;
            DicomFile file;
            try
            {
                // Files on old media may lack the preamble, which fo-dicom reads too, but only a name that looks like DICOM is tried that way.
                if (!listedByDicomDir && !DicomFile.HasValidHeader(path) && !MayBeDicomWithoutPreamble(path))
                {
                    reason = "Not a DICOM file.";
                    return null;
                }
                file = DicomFile.Open(path, FileReadOption.ReadLargeOnDemand);
            }
            catch (Exception ex)
            {
                reason = "Could not be read as DICOM: " + ex.Message;
                return null;
            }

            var meta = file.FileMetaInfo;
            if (meta != null && meta.GetSingleValueOrDefault(DicomTag.MediaStorageSOPClassUID, string.Empty) == DirectoryStorageUid) return null;

            var dataset = file.Dataset;
            var sopClass = Text(dataset, DicomTag.SOPClassUID);
            var sopInstance = Text(dataset, DicomTag.SOPInstanceUID);
            if (sopClass.Length == 0 || sopInstance.Length == 0)
            {
                reason = "Has no SOP Class UID or SOP Instance UID.";
                return null;
            }

            var studyUid = Text(dataset, DicomTag.StudyInstanceUID);
            var seriesUid = Text(dataset, DicomTag.SeriesInstanceUID);
            var studyDate = Date(dataset, DicomTag.StudyDate) ?? Date(dataset, DicomTag.SeriesDate) ?? Date(dataset, DicomTag.AcquisitionDate) ?? Date(dataset, DicomTag.ContentDate);
            var birthDate = Date(dataset, DicomTag.PatientBirthDate);
            var patientId = Text(dataset, DicomTag.PatientID);
            var patientName = Text(dataset, DicomTag.PatientName);
            var sex = Text(dataset, DicomTag.PatientSex).ToUpperInvariant();

            return new ScannedFile
            {
                Path = path,
                Bytes = new FileInfo(path).Length,
                SopClassUid = sopClass,
                SopInstanceUid = sopInstance,
                TransferSyntax = dataset.InternalTransferSyntax,
                Modality = Text(dataset, DicomTag.Modality),
                PatientKey = patientId.Length > 0 ? "id:" + patientId
                    : patientName.Length > 0 ? "name:" + patientName + "|" + Text(dataset, DicomTag.PatientBirthDate)
                    : "study:" + studyUid,
                PatientSex = sex == "M" || sex == "F" ? sex : null,
                PatientAge = birthDate.HasValue ? (int?)Math.Max(0, TestData.TestPersonnummerRegistry.AgeOn(birthDate.Value, studyDate ?? DateTime.Today)) : null,
                // A file without them still goes, in a study and series of its own.
                StudyInstanceUid = studyUid.Length > 0 ? studyUid : "file:" + sopInstance,
                SeriesInstanceUid = seriesUid.Length > 0 ? seriesUid : "file:" + sopInstance,
                StudyDate = studyDate,
                SeriesNumber = Int(dataset, DicomTag.SeriesNumber),
                InstanceNumber = Int(dataset, DicomTag.InstanceNumber),
                HasPixelData = dataset.Contains(DicomTag.PixelData),
                Frames = Math.Max(1, Int(dataset, DicomTag.NumberOfFrames) ?? 1),
                PhotometricInterpretation = Text(dataset, DicomTag.PhotometricInterpretation),
                BurnedInAnnotation = string.Equals(Text(dataset, DicomTag.BurnedInAnnotation), "YES", StringComparison.OrdinalIgnoreCase),
            };
        }

        private static bool MayBeDicomWithoutPreamble(string path)
        {
            var extension = Path.GetExtension(path);
            return extension.Length == 0 || extension.Equals(".dcm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dicom", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Text(DicomDataset dataset, DicomTag tag)
        {
            try
            {
                return dataset.Contains(tag) ? (dataset.GetString(tag) ?? string.Empty).Trim() : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        internal static DateTime? Date(DicomDataset dataset, DicomTag tag)
        {
            var text = Text(dataset, tag);
            if (text.Length >= 8 && DateTime.TryParseExact(text.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            return null;
        }

        private static int? Int(DicomDataset dataset, DicomTag tag)
        {
            var text = Text(dataset, tag);
            var first = text.Split('\\')[0].Trim();
            return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? (int?)value : null;
        }
    }
}
