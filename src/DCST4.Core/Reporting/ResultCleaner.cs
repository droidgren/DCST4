using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DCST4.Core.Reporting
{
    /// <summary>What <see cref="ResultCleaner.Clean"/> did.</summary>
    public sealed class CleanResult
    {
        public int Deleted { get; set; }
        /// <summary>Old enough, and still there: open in another program, read-only or not ours to delete.</summary>
        public int Skipped { get; set; }
    }

    /// <summary>
    /// Deletes old result files from a results folder. It only ever touches files that DCST4 itself names: those that lie
    /// directly in the folder, start with DCST4-run- or DCST4-compare- and end in .json, .csv or .html. Anything else in the
    /// folder, and every subfolder, is left alone, since the folder is the user's own choice and may hold other things.
    /// </summary>
    public static class ResultCleaner
    {
        public const int LargestKeepDays = 3650;

        private static readonly string[] Prefixes = { "DCST4-run-", "DCST4-compare-" };
        private static readonly string[] Extensions = { ".json", ".csv", ".html" };

        /// <summary>Whether the file is one that a run or a comparison writes, by its name alone.</summary>
        public static bool IsResultFile(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            return Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                && Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Deletes the result files in <paramref name="folder"/> that were last written more than <paramref name="keepDays"/>
        /// days before <paramref name="nowUtc"/>. 0 or less keeps everything. <paramref name="keep"/> are never deleted,
        /// whatever their age: the files of the run that was just saved. A file that cannot be deleted is counted and left.
        /// </summary>
        public static CleanResult Clean(string folder, int keepDays, DateTime nowUtc, IEnumerable<string> keep = null)
        {
            var result = new CleanResult();
            if (keepDays <= 0 || string.IsNullOrWhiteSpace(folder)) return result;

            var kept = new HashSet<string>((keep ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            var limit = nowUtc.AddDays(-keepDays);
            string[] files;
            try
            {
                if (!Directory.Exists(folder)) return result;
                files = Directory.GetFiles(folder, "DCST4-*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (RunFile.IsFileProblem(ex))
            {
                return result;
            }

            foreach (var file in files)
            {
                if (!IsResultFile(file) || kept.Contains(Path.GetFullPath(file))) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= limit) continue;
                    File.Delete(file);
                    result.Deleted++;
                }
                catch (Exception ex) when (RunFile.IsFileProblem(ex))
                {
                    result.Skipped++;
                }
            }
            return result;
        }
    }
}
