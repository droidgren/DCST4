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
    /// <summary>One test in the list on the Tests tab. Nothing is ticked at the start: many tests store objects that may not be possible to delete.</summary>
    public sealed class TestChoice : INotifyPropertyChanged
    {
        private bool _isSelected;

        public TestChoice(ITestCase test)
        {
            Test = test;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>Raised when <see cref="IsSelected"/> changes, for the group and the Check all box.</summary>
        public event Action SelectionChanged;

        public ITestCase Test { get; }
        public string Name => Test.Name;
        public string Category => Test.Category.ToString();
        public string Group => Test.Group;
        public string Description => Test.Description;

        /// <summary>What the test does, what it leaves on the destination, and its id for DCST4.Cli.exe.</summary>
        public string ToolTipText => Test.Description + (StorageText == null ? string.Empty : "\n\n" + StorageText) + "\n\nId: " + Test.Id;

        /// <summary>"Stores 1 examination, 1 object, about 34 KB on the destination." Null for a test that stores nothing.</summary>
        public string StorageText
        {
            get
            {
                if (Test.Storage != null) return $"Stores {Test.Storage.Describe()} on the destination." + (Test.Storage.Note == null ? string.Empty : " " + Test.Storage.Note);
                return Test.StoresObjects ? "Stores new objects on the destination." : null;
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>A group of tests in the list on the Tests tab, with a box that ticks or clears all of them.</summary>
    public sealed class TestGroupChoice : INotifyPropertyChanged
    {
        /// <summary>Folded at the start, so that the list shows the groups and is short.</summary>
        private bool _isExpanded;

        public TestGroupChoice(string name, IEnumerable<TestChoice> tests)
        {
            Name = name;
            Tests = new ObservableCollection<TestChoice>(tests);
            foreach (var test in Tests) test.SelectionChanged += () => { OnPropertyChanged(nameof(IsSelected)); OnPropertyChanged(nameof(CountText)); };
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Name { get; }
        public ObservableCollection<TestChoice> Tests { get; }

        /// <summary>True when all are ticked, false when none is, and null in between. Setting true or false ticks or clears all.</summary>
        public bool? IsSelected
        {
            get => TestSuiteViewModel.StateOf(Tests);
            set { if (value.HasValue) foreach (var test in Tests) test.IsSelected = value.Value; }
        }

        /// <summary>"2 of 5"</summary>
        public string CountText => $"{Tests.Count(t => t.IsSelected)} of {Tests.Count}";

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
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
            foreach (var test in TestCatalog.All.Where(t => t.Category != TestCategory.Performance))
            {
                var choice = new TestChoice(test);
                choice.SelectionChanged += () => { OnPropertyChanged(nameof(AllSelected)); OnPropertyChanged(nameof(SelectionText)); };
                Choices.Add(choice);
            }

            // In the order of TestGroups, and within a group in catalog order. A group the catalog has no test in is left out.
            foreach (var group in TestGroups.All)
            {
                var members = Choices.Where(c => c.Group == group).ToList();
                if (members.Count > 0) Groups.Add(new TestGroupChoice(group, members));
            }

            var saved = Settings.Default.TestResultsFolder;
            _resultsFolder = string.IsNullOrWhiteSpace(saved) ? DefaultResultsFolder : saved;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Every test of the tab, in catalog order.</summary>
        public ObservableCollection<TestChoice> Choices { get; } = new ObservableCollection<TestChoice>();

        /// <summary>The same tests as <see cref="Choices"/>, by group, as the list shows them.</summary>
        public ObservableCollection<TestGroupChoice> Groups { get; } = new ObservableCollection<TestGroupChoice>();

        /// <summary>The Check all box: true when all are ticked, false when none is, null in between.</summary>
        public bool? AllSelected
        {
            get => StateOf(Choices);
            set { if (value.HasValue) foreach (var choice in Choices) choice.IsSelected = value.Value; }
        }

        /// <summary>"12 of 35 ticked"</summary>
        public string SelectionText => $"{Choices.Count(c => c.IsSelected)} of {Choices.Count} ticked";

        internal static bool? StateOf(ICollection<TestChoice> choices)
        {
            var ticked = choices.Count(c => c.IsSelected);
            if (ticked == 0) return false;
            if (ticked == choices.Count) return true;
            return null;
        }

        /// <summary>After the settings were reset: the results folder from the settings, the store syntax from the matrix and nothing ticked.</summary>
        public void ReloadFromSettings()
        {
            var saved = Settings.Default.TestResultsFolder;
            _resultsFolder = string.IsNullOrWhiteSpace(saved) ? DefaultResultsFolder : saved;
            OnPropertyChanged(nameof(ResultsFolder));
            SelectedStoreSyntax = StoreSyntaxChoices[0];
            AllSelected = false;
        }
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
