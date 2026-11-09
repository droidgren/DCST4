using DCST4.Core.Pipeline;
using FellowOakDicom;
using FellowOakDicom.Media;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DCST4.Core.Output
{
    /// <summary>
    /// Writes images as a DICOM File-set: &lt;folder&gt;\DICOMDIR and &lt;folder&gt;\DICOM\EX000001\S001\IM0001.
    ///
    /// PS3.10 limits each part of a File ID to 8 characters from A-Z, 0-9 and underscore, so the folders cannot carry
    /// patient details and the files have no extension. Each run gets its own EX folder, so nothing is ever overwritten.
    /// </summary>
    public sealed class FileSetWriter
    {
        public const string DicomDirFileName = "DICOMDIR";
        private const string ContentFolderName = "DICOM";
        private const string FileSetId = "DCST4";

        private static readonly ILogger Logger = Log.ForContext<FileSetWriter>();

        private readonly string _root;
        private string _examFolder;

        public FileSetWriter(string rootFolder)
        {
            _root = rootFolder;
        }

        public string DicomDirPath => Path.Combine(_root, DicomDirFileName);
        private string ContentFolder => Path.Combine(_root, ContentFolderName);

        public string Save(SourceImage image)
        {
            if (_examFolder == null) _examFolder = CreateExamFolder();

            var folder = Path.Combine(_examFolder, "S" + image.SeriesNumber.ToString("000", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, "IM" + image.InstanceNumber.ToString("0000", CultureInfo.InvariantCulture));
            if (File.Exists(path)) throw new IOException($"{path} already exists.");

            image.File.Save(path);
            return path;
        }

        /// <summary>
        /// Builds the DICOMDIR from every file in the File-set. Rebuilding rather than appending means repeated runs into
        /// one folder end up in the same DICOMDIR, and files left behind by an interrupted run are included too.
        /// </summary>
        public int WriteDicomDir()
        {
            var directory = new DicomDirectory { FileSetID = FileSetId };
            var count = 0;

            foreach (var path in Directory.EnumerateFiles(ContentFolder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var fileId = ToFileId(path);
                if (!IsValidFileId(fileId))
                {
                    Logger.Warning("Left out of the DICOMDIR, the name is not a valid DICOM File ID: {path}", path);
                    continue;
                }

                try
                {
                    var file = DicomFile.Open(path, FileReadOption.SkipLargeTags);
                    var entry = directory.AddFile(file, fileId);
                    AddCharacterSet(file.Dataset, entry);
                    SetRecordType(file.Dataset, entry);
                    count++;
                }
                catch (Exception ex) when (ex is DicomException || ex is IOException || ex is InvalidOperationException || ex is ArgumentException || ex is UnauthorizedAccessException)
                {
                    // A stray or damaged file must not cost the user the rest of the directory.
                    Logger.Warning(ex, "Left out of the DICOMDIR: {path}", path);
                }
            }

            // Swap a finished file into place, so a full disk or crash cannot leave a truncated DICOMDIR behind.
            var temporary = DicomDirPath + ".tmp";
            directory.Save(temporary);
            if (File.Exists(DicomDirPath)) File.Replace(temporary, DicomDirPath, null);
            else File.Move(temporary, DicomDirPath);

            return count;
        }

        /// <summary>
        /// Patient names in the records are copied as text from each image, and a DICOMDIR can hold exams in different
        /// character sets, so every record must say which one its own text is in (PS3.3 F.5).
        /// </summary>
        private static void AddCharacterSet(DicomDataset source, DicomDirectoryEntry entry)
        {
            var characterSet = source.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, string.Empty);
            if (characterSet.Length == 0) return;

            foreach (var record in new DicomDirectoryRecord[] { entry.PatientRecord, entry.StudyRecord, entry.SeriesRecord, entry.InstanceRecord })
            {
                record?.AddOrUpdate(DicomTag.SpecificCharacterSet, characterSet);
            }
        }

        /// <summary>
        /// fo-dicom knows the record types for images, reports and presentation states. Anything else it files as an IMAGE,
        /// which a viewer would then try to open as one, so these get their own type and the keys it requires (PS3.3 F.5).
        /// </summary>
        private static void SetRecordType(DicomDataset source, DicomDirectoryEntry entry)
        {
            var sopClass = source.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
            string recordType;
            DicomTag[] keys;
            if (sopClass == DicomUID.KeyObjectSelectionDocumentStorage.UID)
            {
                recordType = "KEY OBJECT DOC";
                keys = new[] { DicomTag.InstanceNumber, DicomTag.ContentDate, DicomTag.ContentTime, DicomTag.ConceptNameCodeSequence };
            }
            else if (sopClass == DicomUID.EncapsulatedPDFStorage.UID)
            {
                recordType = "ENCAP DOC";
                keys = new[] { DicomTag.InstanceNumber, DicomTag.ContentDate, DicomTag.ContentTime, DicomTag.ConceptNameCodeSequence, DicomTag.DocumentTitle, DicomTag.MIMETypeOfEncapsulatedDocument };
            }
            else if (sopClass == DicomUID.RTStructureSetStorage.UID)
            {
                recordType = "RT STRUCTURE SET";
                keys = new[] { DicomTag.InstanceNumber, DicomTag.StructureSetLabel, DicomTag.StructureSetDate, DicomTag.StructureSetTime };
            }
            else
            {
                return;
            }

            entry.InstanceRecord.AddOrUpdate(DicomTag.DirectoryRecordType, recordType);
            foreach (var key in keys)
            {
                var item = source.GetDicomItem<DicomItem>(key);
                if (item != null) entry.InstanceRecord.AddOrUpdate(item);
            }
        }

        /// <summary>PS3.10: at most 8 parts, each 1-8 characters from A-Z, 0-9 and underscore.</summary>
        public static bool IsValidFileId(string fileId)
        {
            var parts = fileId.Split('\\');
            return parts.Length <= 8 && parts.All(p => p.Length >= 1 && p.Length <= 8 && p.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'));
        }

        private string ToFileId(string path)
        {
            var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).Substring(Path.GetFullPath(root).Length);
        }

        private string CreateExamFolder()
        {
            Directory.CreateDirectory(ContentFolder);
            var used = new HashSet<string>(
                Directory.EnumerateDirectories(ContentFolder).Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);

            for (var number = 1; number <= 999999; number++)
            {
                var name = "EX" + number.ToString("000000", CultureInfo.InvariantCulture);
                if (used.Contains(name)) continue;
                var folder = Path.Combine(ContentFolder, name);
                Directory.CreateDirectory(folder);
                return folder;
            }

            throw new IOException($"{ContentFolder} has no free exam folder name left.");
        }
    }
}
