using DCST4.Core.Reporting;
using System.Globalization;

namespace DCST4.ViewModels
{
    /// <summary>
    /// The Test and Performance Results tab of Settings: a copy of where runs on the Tests and Performance tabs write their
    /// result files, which files and for how long, so that Cancel leaves them as they were. OK checks them and only then
    /// hands them to <see cref="MainViewModel"/>, which saves them.
    /// </summary>
    public sealed class ResultSettingsViewModel
    {
        private readonly MainViewModel _main;

        public ResultSettingsViewModel(MainViewModel main)
        {
            _main = main;
            ResultsFolder = main.TestSuite.ResultsFolder;
            WriteJson = main.WriteResultJson;
            WriteCsv = main.WriteResultCsv;
            WriteHtml = main.WriteResultHtml;
            WriteSeries = main.WriteResultSeries;
            KeepDays = main.ResultsKeepDays.ToString(CultureInfo.InvariantCulture);
        }

        public string ResultsFolder { get; set; }
        public bool WriteJson { get; set; }
        public bool WriteCsv { get; set; }
        public bool WriteHtml { get; set; }
        /// <summary>The series files of the Performance tests, written while they run.</summary>
        public bool WriteSeries { get; set; }
        /// <summary>Result files older than this many days are deleted after a run. 0 keeps everything.</summary>
        public string KeepDays { get; set; }

        /// <summary>Checks every value and, if all can be used, applies them. Returns the first problem, or null when applied.</summary>
        public string Apply()
        {
            if (string.IsNullOrWhiteSpace(ResultsFolder)) return "Enter a folder for the results.";
            if (!int.TryParse((KeepDays ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var keepDays))
            {
                return "Delete results older than must be a whole number of 0 or more.";
            }
            if (keepDays > ResultCleaner.LargestKeepDays)
            {
                return $"Delete results older than must be at most {ResultCleaner.LargestKeepDays.ToString(CultureInfo.InvariantCulture)} days. 0 keeps everything.";
            }

            _main.TestSuite.ResultsFolder = ResultsFolder.Trim();
            _main.WriteResultJson = WriteJson;
            _main.WriteResultCsv = WriteCsv;
            _main.WriteResultHtml = WriteHtml;
            _main.WriteResultSeries = WriteSeries;
            _main.ResultsKeepDays = keepDays;
            return null;
        }
    }
}
