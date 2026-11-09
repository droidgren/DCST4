using DCST4.Core.Testing;
using DCST4.Core.Verification;
using FellowOakDicom;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace DCST4.ViewModels
{
    /// <summary>One object type on the Chain tab.</summary>
    public sealed class ChainRowView
    {
        private const string NotRun = "-";

        public ChainRowView(ChainRow row, IReadOnlyList<IntegritySet> sets)
        {
            ObjectType = row.DisplayName;
            TransferSyntax = string.IsNullOrEmpty(row.TransferSyntaxUid) ? NotRun : DicomTransferSyntax.Parse(row.TransferSyntaxUid).UID.Name;
            Accepted = Text(row.Accepted);
            Indexed = Text(row.Indexed);
            Retrievable = Text(row.Retrievable);
            Intact = Text(row.Intact);
            Committed = Text(row.Committed);
            Worst = ResultSeverity.Worst(new[] { row.Accepted, row.Indexed, row.Retrievable, row.Intact, row.Committed }.Where(s => s != null).Select(s => s.Result));
            Detail = Describe(row, sets);
        }

        public string ObjectType { get; }
        public string TransferSyntax { get; }
        public string Accepted { get; }
        public string Indexed { get; }
        public string Retrievable { get; }
        public string Intact { get; }
        public string Committed { get; }
        public TestResultClass Worst { get; }
        public string Detail { get; }

        private static string Text(ChainStep step) => step == null ? NotRun : step.Result.ToString();

        private static string Describe(ChainRow row, IReadOnlyList<IntegritySet> sets)
        {
            var text = new StringBuilder();
            void Line(string name, ChainStep step)
            {
                if (step != null) text.AppendLine($"{name}: {step.Result}: {step.Explanation}");
            }

            Line("Accepted", row.Accepted);
            Line("Indexed", row.Indexed);
            Line("Retrievable", row.Retrievable);
            Line("Intact", row.Intact);

            foreach (var set in sets)
            {
                text.AppendLine();
                text.AppendLine($"{set.Method}:");
                foreach (var level in set.Levels) text.AppendLine($"  {level.Level}: {level.Result}: {level.Explanation}");
                foreach (var difference in set.Instances.SelectMany(i => i.Differences).Where(d => d.Class != AttributeChangeClass.Added && d.Class != AttributeChangeClass.VrLost))
                {
                    text.AppendLine($"  {difference.Class}: {difference.Name} {difference.Path}: \"{difference.Original}\" became \"{difference.Retrieved}\"");
                }
            }
            return text.ToString().TrimEnd();
        }
    }

    /// <summary>accepted, indexed, retrievable, intact: one row per object type of the last run.</summary>
    public sealed class ChainSummaryViewModel : INotifyPropertyChanged
    {
        private ChainRowView _selectedRow;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ObservableCollection<ChainRowView> Rows { get; } = new ObservableCollection<ChainRowView>();

        public ChainRowView SelectedRow
        {
            get => _selectedRow;
            set { _selectedRow = value; OnPropertyChanged(); }
        }

        /// <summary>Replaces what is shown with the chain of this run, if it has one.</summary>
        public void Show(TestRunReport report)
        {
            Rows.Clear();
            SelectedRow = null;
            if (report.Chain == null) return;

            var integrity = report.Results.Where(r => r.Integrity != null).SelectMany(r => r.Integrity.Sets).ToList();
            foreach (var row in report.Chain)
            {
                Rows.Add(new ChainRowView(row, integrity.Where(s => s.ObjectType == row.ObjectType).ToList()));
            }
        }
    }
}
