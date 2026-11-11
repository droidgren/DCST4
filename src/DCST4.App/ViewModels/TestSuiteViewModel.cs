using DCST4.Core.Network;
using DCST4.Core.Testing;
using DCST4.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DCST4.ViewModels
{
    /// <summary>One test in the list on the Tests tab.</summary>
    public sealed class TestChoice : INotifyPropertyChanged
    {
        private bool _isSelected;

        public TestChoice(ITestCase test)
        {
            Test = test;
            // The limit and fault tests strain the system under test and break transfers on purpose, so they are asked for, not assumed.
            _isSelected = test.Category != TestCategory.Limits;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ITestCase Test { get; }
        public string Name => Test.Name;
        public string Category => Test.Category.ToString();
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
    }

    /// <summary>One entry in the list of what the store tests send in.</summary>
    public sealed class StoreSyntaxChoice
    {
        public StoreSyntaxChoice(string name, FellowOakDicom.DicomTransferSyntax syntax)
        {
            Name = name;
            Syntax = syntax;
        }

        public string Name { get; }
        /// <summary>Null chooses per object type.</summary>
        public FellowOakDicom.DicomTransferSyntax Syntax { get; }
        public override string ToString() => Name;
    }

    /// <summary>One result in the grid on the Tests tab.</summary>
    public sealed class TestResultRow
    {
        public TestResultRow(TestResult result)
        {
            TestId = result.TestId;
            Name = result.Name;
            Category = result.Category.ToString();
            Result = result.Result;
            Status = string.Join(" ", result.StatusCodes.Select(s => s.Code));
            DurationMs = result.DurationMs;
            Explanation = result.Explanation;
            Timeline = result.Timeline;
        }

        public string TestId { get; }
        public string Name { get; }
        public string Category { get; }
        public TestResultClass Result { get; }
        public string Status { get; }
        public long DurationMs { get; }
        public string Explanation { get; }
        public IReadOnlyList<ProtocolEvent> Timeline { get; }
    }

    /// <summary>What the Tests tab shows. Running goes through <see cref="MainViewModel"/>, which owns the busy state and Cancel.</summary>
    public sealed class TestSuiteViewModel : INotifyPropertyChanged
    {
        /// <summary>%LocalAppData%\DCST4\TestRuns</summary>
        public static string DefaultResultsFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"DCST4\TestRuns");

        private string _resultsFolder;
        private TestResultRow _selectedRow;
        private ResultFilePaths _lastResultFiles;

        public TestSuiteViewModel()
        {
            // The Performance tests have a tab of their own, with their settings, the planned total and the chart.
            foreach (var test in TestCatalog.All.Where(t => t.Category != TestCategory.Performance)) Choices.Add(new TestChoice(test));
            var saved = Settings.Default.TestResultsFolder;
            _resultsFolder = string.IsNullOrWhiteSpace(saved) ? DefaultResultsFolder : saved;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ObservableCollection<TestChoice> Choices { get; } = new ObservableCollection<TestChoice>();
        public ObservableCollection<TestResultRow> Rows { get; } = new ObservableCollection<TestResultRow>();
        public CapabilityViewModel Capabilities { get; } = new CapabilityViewModel();
        public ChainSummaryViewModel Chain { get; } = new ChainSummaryViewModel();

        /// <summary>What the store tests send in. The first choice leaves it to the capability matrix, per object type.</summary>
        public IReadOnlyList<StoreSyntaxChoice> StoreSyntaxChoices { get; } =
            new[] { new StoreSyntaxChoice("Per object type (capability matrix)", null) }
                .Concat(DCST4.Core.Pipeline.TransferSyntaxOption.All.Select(o => new StoreSyntaxChoice(o.Name, o.Syntax))).ToList();

        private StoreSyntaxChoice _selectedStoreSyntax;
        public StoreSyntaxChoice SelectedStoreSyntax
        {
            get => _selectedStoreSyntax ?? StoreSyntaxChoices[0];
            set { _selectedStoreSyntax = value; OnPropertyChanged(); }
        }

        public IReadOnlyList<ITestCase> SelectedTests => Choices.Where(c => c.IsSelected).Select(c => c.Test).ToList();

        public string ResultsFolder
        {
            get => _resultsFolder;
            set { _resultsFolder = value ?? string.Empty; OnPropertyChanged(); Settings.Default.TestResultsFolder = _resultsFolder; }
        }

        public TestResultRow SelectedRow
        {
            get => _selectedRow;
            set { _selectedRow = value; OnPropertyChanged(); }
        }

        /// <summary>The files of the last run, or null if it wrote none.</summary>
        public ResultFilePaths LastResultFiles
        {
            get => _lastResultFiles;
            set { _lastResultFiles = value; OnPropertyChanged(); }
        }
    }
}
