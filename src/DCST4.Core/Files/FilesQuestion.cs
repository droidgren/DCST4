using System.Collections.Generic;
using System.Globalization;

namespace DCST4.Core.Files
{
    /// <summary>The words the App asks with and the command line prints, about what sending a folder will do.</summary>
    public static class FilesQuestion
    {
        public const string Title = "Re-identify the files?";
        public const string ReidentifyButton = "Re-identify as test patients";
        public const string KeepButton = "Send unchanged";

        /// <summary>The question shown after a folder has been read. Re-identifying is the default answer.</summary>
        public static string Text(FolderScan scan)
        {
            var lines = new List<string>
            {
                (scan.DicomDirPath != null ? "Read through the DICOMDIR: " : "Read: ") + scan.Summary + ".",
                "Transfer syntaxes: " + scan.SyntaxSummary + ".",
            };
            if (scan.Skipped.Count > 0) lines.Add(Count(scan.Skipped.Count, "file is", "files are") + " skipped, listed on the Files tab.");
            lines.Add(string.Empty);
            lines.Add(ReidentifyButton + " (default):");
            foreach (var line in Describe(scan, true)) lines.Add("  " + line);
            lines.Add(string.Empty);
            lines.Add(KeepButton + ":");
            foreach (var line in Describe(scan, false)) lines.Add("  " + line);
            lines.Add(string.Empty);
            lines.Add("The answer can be changed on the Files tab before anything is sent.");
            return string.Join("\n", lines);
        }

        /// <summary>What is done to the files, one sentence a line.</summary>
        public static IReadOnlyList<string> Describe(FolderScan scan, bool reidentify)
        {
            var lines = new List<string>();
            if (!reidentify)
            {
                lines.Add("The files go exactly as they are: the real patient names and IDs, the files' own UIDs, and no DCST4 marking.");
                lines.Add("Use it only for data that may be sent to this destination. A second send of the same files is a duplicate.");
                return lines;
            }

            lines.Add(string.Format(CultureInfo.InvariantCulture, "Each of the {0} becomes a test patient from the test personnummer list, about as old and of the same sex.",
                Count(scan.PatientCount, "patient", "patients")));
            lines.Add("Accession Number and Study ID are new, and other names of people are replaced. Private tags are kept as they are.");
            lines.Add("Every UID of an instance, series or study gets a new one under the UID root, and references between the files still hold.");
            lines.Add("Manufacturer is set to " + FileReidentifier.Manufacturer + ", and images get the project's address in Image Comments.");

            var native = 0;
            foreach (var file in scan.Files) if (file.HasPixelData && !file.IsEncapsulated) native++;
            if (native > 0) lines.Add(Count(native, "file with uncompressed pixels gets", "files with uncompressed pixels get") + " the TEST IMAGE watermark.");
            if (scan.CompressedCount > 0)
            {
                lines.Add(Count(scan.CompressedCount, "compressed file keeps its original pixel data, which is what is tested, and gets",
                    "compressed files keep their original pixel data, which is what is tested, and get") + " no watermark.");
            }
            if (scan.BurnedInAnnotationCount > 0)
            {
                lines.Add(Count(scan.BurnedInAnnotationCount, "file has", "files have") + " Burned In Annotation YES: the pixels may show who the patient is, which re-identifying does not change.");
            }
            return lines;
        }

        private static string Count(int count, string one, string many) => count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
    }
}
