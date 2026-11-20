using DCST4.Core.Capability;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DCST4.ViewModels
{
    /// <summary>One cell of the matrix on the Capabilities tab.</summary>
    public sealed class CapabilityCellView
    {
        public string Code { get; set; }
        public Brush Background { get; set; }
        public string ToolTip { get; set; }
    }

    public sealed class CapabilityRowView
    {
        public string SopClass { get; set; }
        public string SopClassUid { get; set; }
        public IReadOnlyList<CapabilityCellView> Cells { get; set; }
    }

    public sealed class CapabilityColumnView
    {
        public string ShortName { get; set; }
        public string Name { get; set; }
    }

    public sealed class CapabilityDifferenceRow
    {
        /// <summary>The kind of difference, as the grid shows it.</summary>
        public string Kind { get; set; }
        public string SopClass { get; set; }
        public string TransferSyntax { get; set; }
        public string Actual { get; set; }
    }

    /// <summary>
    /// What the Capabilities tab shows: the last probe, and how it compares with what the vendor claims. A second instance
    /// shows the retrieve matrix of retrieve-syntaxes, and how it compares with the probe.
    /// </summary>
    public sealed class CapabilityViewModel : INotifyPropertyChanged
    {
        public const string Legend = "✓ accepted   A abstract syntax not supported   T transfer syntax not supported   U rejected by user   N rejected, no reason   ? not answered   ≠ another syntax accepted   R association rejected   F association failed   blank: not probed.   With a claimed file: ! claimed but rejected, + accepted but not claimed, ~ claimed but no answer.";

        public const string RetrieveLegend = "✓ delivered in this syntax   D context accepted, object not delivered   A abstract syntax not supported   T transfer syntax not supported   U rejected by user   N rejected, no reason   ? not answered   ≠ another syntax accepted   R association rejected   F association failed   blank: not asked.   Against the store matrix: ^ delivered but not accepted on store (transcodes on retrieve), v accepted on store but not delivered.";

        private const string NoMatrix = "No probe has run yet. Tick the negotiation probe on the Tests tab and run it.";
        private const string NoRetrieveMatrix = "retrieve-syntaxes has not run yet. Tick it on the Tests tab together with at least one store test, and run them.";

        private static readonly Brush AcceptedBrush = Frozen(0xC8, 0xE6, 0xC9);
        private static readonly Brush AbstractBrush = Frozen(0xF0, 0xF0, 0xF0);
        private static readonly Brush SyntaxBrush = Frozen(0xFF, 0xF4, 0xC2);
        private static readonly Brush RefusedBrush = Frozen(0xFA, 0xD4, 0xD0);
        private static readonly Brush IrregularBrush = Frozen(0xFF, 0xCC, 0x80);
        private static readonly Brush UnknownBrush = Frozen(0xE6, 0xE6, 0xE6);
        private static readonly Brush ClaimedButRejectedBrush = Frozen(0xE5, 0x73, 0x73);
        private static readonly Brush NotClaimedBrush = Frozen(0x90, 0xCA, 0xF9);

        private readonly bool _isRetrieve;
        private CapabilityMatrix _matrix;
        private CapabilityMatrix _claimed;
        private CapabilityMatrix _store;
        private string _remoteSummary;
        private string _comparisonSummary = string.Empty;

        /// <param name="isRetrieve">Shows the retrieve matrix, which is compared with the store matrix rather than with a claimed file.</param>
        public CapabilityViewModel(bool isRetrieve = false)
        {
            _isRetrieve = isRetrieve;
            _remoteSummary = isRetrieve ? NoRetrieveMatrix : NoMatrix;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>The columns are generated in the window, since their number follows the matrix.</summary>
        public event Action ColumnsChanged;

        public IReadOnlyList<CapabilityColumnView> Columns { get; private set; } = new CapabilityColumnView[0];
        public ObservableCollection<CapabilityRowView> Rows { get; } = new ObservableCollection<CapabilityRowView>();
        public ObservableCollection<CapabilityDifferenceRow> Differences { get; } = new ObservableCollection<CapabilityDifferenceRow>();

        public CapabilityMatrix Matrix => _matrix;
        public bool HasMatrix => _matrix != null;
        public bool HasClaimed => _claimed != null;
        public bool IsRetrieve => _isRetrieve;
        public string LegendText => _isRetrieve ? RetrieveLegend : Legend;

        public string RemoteSummary
        {
            get => _remoteSummary;
            private set { _remoteSummary = value; OnPropertyChanged(); }
        }

        public string ComparisonSummary
        {
            get => _comparisonSummary;
            private set { _comparisonSummary = value; OnPropertyChanged(); }
        }

        public void Show(CapabilityMatrix matrix)
        {
            _matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
            Columns = matrix.TransferSyntaxes.Select(s => new CapabilityColumnView { ShortName = s.ShortName, Name = s.Name }).ToList();
            RemoteSummary = _isRetrieve ? DescribeRetrieve(matrix) : Describe(matrix);
            OnPropertyChanged(nameof(HasMatrix));
            ColumnsChanged?.Invoke();
            Rebuild();
        }

        /// <summary>The store matrix to mark the retrieve matrix against, or null. Only for the retrieve matrix.</summary>
        public void SetStoreReference(CapabilityMatrix store)
        {
            _store = _isRetrieve ? store : null;
            Rebuild();
        }

        public void SetClaimed(CapabilityMatrix claimed)
        {
            _claimed = claimed;
            OnPropertyChanged(nameof(HasClaimed));
            Rebuild();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void Rebuild()
        {
            Rows.Clear();
            Differences.Clear();
            ComparisonSummary = string.Empty;
            if (_matrix == null) return;

            if (_isRetrieve)
            {
                RebuildRetrieve();
                return;
            }

            var comparison = _claimed != null ? CapabilityComparison.Compare(_matrix, _claimed) : null;
            var differences = comparison?.Differences.ToDictionary(d => d.SopClassUid + "|" + d.TransferSyntaxUid) ?? new Dictionary<string, CapabilityDifference>();

            foreach (var row in _matrix.Rows)
            {
                Rows.Add(new CapabilityRowView
                {
                    SopClass = row.SopClassName,
                    SopClassUid = row.SopClassUid,
                    Cells = Aligned(row, c => CellView(row, c, differences)),
                });
            }

            if (comparison == null) return;
            foreach (var difference in comparison.Differences)
            {
                Differences.Add(new CapabilityDifferenceRow
                {
                    Kind = difference.Kind.ToString(),
                    SopClass = difference.SopClassName,
                    TransferSyntax = difference.TransferSyntaxName,
                    Actual = difference.Actual?.ToString() ?? "Not part of the probe",
                });
            }

            ComparisonSummary = string.Format(CultureInfo.InvariantCulture, "Claimed vs actual: {0} agree, {1} claimed but rejected, {2} accepted but not claimed, {3} claimed but not answered.",
                comparison.Agreeing,
                comparison.OfKind(CapabilityDifferenceKind.ClaimedButRejected).Count(),
                comparison.OfKind(CapabilityDifferenceKind.AcceptedButNotClaimed).Count(),
                comparison.OfKind(CapabilityDifferenceKind.ClaimedButNotProbed).Count());
        }

        /// <summary>One cell per column, in column order. A syntax the row was not asked about gets an empty cell.</summary>
        private IReadOnlyList<CapabilityCellView> Aligned(MatrixRow row, Func<MatrixCell, CapabilityCellView> view) =>
            _matrix.TransferSyntaxes.Select(s =>
            {
                var cell = row.Cells.FirstOrDefault(c => c.TransferSyntaxUid == s.Uid);
                return cell == null ? new CapabilityCellView { Code = string.Empty, Background = Brushes.White, ToolTip = $"{row.SopClassName}\n{s.Name}\nNot asked for this object" } : view(cell);
            }).ToList();

        private void RebuildRetrieve()
        {
            var comparison = _store != null ? StoreRetrieveComparison.Compare(_store, _matrix) : null;
            foreach (var row in _matrix.Rows)
            {
                Rows.Add(new CapabilityRowView
                {
                    SopClass = row.SopClassName,
                    SopClassUid = row.SopClassUid,
                    Cells = Aligned(row, c => RetrieveCellView(row, c, comparison)),
                });
            }

            if (comparison == null) return;
            foreach (var difference in comparison.Differences)
            {
                Differences.Add(new CapabilityDifferenceRow
                {
                    Kind = difference.Kind == StoreRetrieveDifferenceKind.StoredButNotDelivered ? "Accepted on store, not delivered" : "Delivered, not accepted on store",
                    SopClass = difference.SopClassName,
                    TransferSyntax = difference.TransferSyntaxName,
                    Actual = $"Store: {difference.Store}, retrieve: {difference.Retrieve}",
                });
            }
            ComparisonSummary = string.Format(CultureInfo.InvariantCulture, "Store vs retrieve: {0} combinations in both, {1} accepted on store but not delivered, {2} delivered but not accepted on store.",
                comparison.Compared,
                comparison.OfKind(StoreRetrieveDifferenceKind.StoredButNotDelivered).Count(),
                comparison.OfKind(StoreRetrieveDifferenceKind.DeliveredButNotStored).Count());
        }

        private CapabilityCellView RetrieveCellView(MatrixRow row, MatrixCell cell, StoreRetrieveComparison comparison)
        {
            var view = new CapabilityCellView
            {
                Code = CodeOf(cell.Result),
                Background = BrushOf(cell.Result),
                ToolTip = TooltipOf(row, cell),
            };
            if (cell.Status != null) view.ToolTip += $"\nFinal C-GET status {cell.Status}";

            var difference = comparison?.Find(row.SopClassUid, cell.TransferSyntaxUid);
            if (difference == null) return view;
            if (difference.Kind == StoreRetrieveDifferenceKind.DeliveredButNotStored)
            {
                view.Code += "^";
                view.Background = NotClaimedBrush;
                view.ToolTip += "\nDelivered, but not accepted on store: the system transcodes on retrieve.";
            }
            else
            {
                view.Code += "v";
                view.Background = ClaimedButRejectedBrush;
                view.ToolTip += "\nAccepted on store, but not delivered.";
            }
            return view;
        }

        private CapabilityCellView CellView(MatrixRow row, MatrixCell cell, Dictionary<string, CapabilityDifference> differences)
        {
            var view = new CapabilityCellView
            {
                Code = CodeOf(cell.Result),
                Background = BrushOf(cell.Result),
                ToolTip = TooltipOf(row, cell),
            };

            if (differences.TryGetValue(row.SopClassUid + "|" + cell.TransferSyntaxUid, out var difference))
            {
                switch (difference.Kind)
                {
                    case CapabilityDifferenceKind.ClaimedButRejected:
                        view.Code += "!";
                        view.Background = ClaimedButRejectedBrush;
                        view.ToolTip += "\nClaimed by the vendor, but rejected.";
                        break;
                    case CapabilityDifferenceKind.AcceptedButNotClaimed:
                        view.Code += "+";
                        view.Background = NotClaimedBrush;
                        view.ToolTip += "\nAccepted, but not claimed by the vendor.";
                        break;
                    default:
                        view.Code += "~";
                        view.ToolTip += "\nClaimed by the vendor, but the probe got no answer.";
                        break;
                }
            }
            return view;
        }

        private string TooltipOf(MatrixRow row, MatrixCell cell)
        {
            var syntax = _matrix.TransferSyntaxes.FirstOrDefault(s => s.Uid == cell.TransferSyntaxUid);
            var text = $"{row.SopClassName}\n{syntax?.Name ?? cell.TransferSyntaxUid}\n{cell.Result}";
            if (cell.Association.HasValue) text += $"\nAssociation {cell.Association.Value}";
            if (cell.PresentationContextId.HasValue) text += $", presentation context {cell.PresentationContextId.Value}";
            if (cell.Result == CapabilityResult.AcceptedOtherSyntax) text += $"\nAccepted as {cell.AcceptedTransferSyntaxUid}";
            return text;
        }

        private static string Describe(CapabilityMatrix matrix)
        {
            var cells = matrix.AllCells().ToList();
            var accepted = cells.Count(c => c.Item2.Result == CapabilityResult.Accepted);
            var text = $"{matrix.Target?.CalledAe} at {matrix.Target?.Host}:{matrix.Target?.Port}: {accepted} of {cells.Count} accepted, {matrix.Associations.Count} associations.";
            if (matrix.RemoteImplementationClassUid != null)
            {
                text += $" Implementation {matrix.RemoteImplementationClassUid} {matrix.RemoteImplementationVersionName}, max PDU {matrix.RemoteMaxPduLength}.";
            }
            if (matrix.RoleSelectionReplies.Count > 0) text += $" Role selection answered for {matrix.RoleSelectionReplies.Count(r => r.Replied)} of {matrix.RoleSelectionReplies.Count}.";
            if (matrix.ExtendedNegotiationReplies.Count > 0) text += $" Extended negotiation answered for {matrix.ExtendedNegotiationReplies.Count(r => r.Replied)} of {matrix.ExtendedNegotiationReplies.Count}.";
            if (matrix.AsyncOperationsReply != null) text += $" Asynchronous Operations Window: {matrix.AsyncOperationsReply.Describe()}.";
            return text;
        }

        private static string DescribeRetrieve(CapabilityMatrix matrix)
        {
            var cells = matrix.AllCells().ToList();
            var delivered = cells.Count(c => c.Item2.Result == CapabilityResult.Accepted);
            return $"{matrix.Target?.CalledAe} at {matrix.Target?.Host}:{matrix.Target?.Port}: {delivered} of {cells.Count} delivered on C-GET, for {matrix.Rows.Count} SOP classes, in {matrix.Associations.Count} associations.";
        }

        private static string CodeOf(CapabilityResult result)
        {
            switch (result)
            {
                case CapabilityResult.Accepted: return "✓";
                case CapabilityResult.AbstractSyntaxNotSupported: return "A";
                case CapabilityResult.TransferSyntaxNotSupported: return "T";
                case CapabilityResult.UserRejection: return "U";
                case CapabilityResult.NoReason: return "N";
                case CapabilityResult.NotAnswered: return "?";
                case CapabilityResult.AcceptedOtherSyntax: return "≠";
                case CapabilityResult.AssociationRejected: return "R";
                case CapabilityResult.AssociationFailed: return "F";
                case CapabilityResult.NotDelivered: return "D";
                default: return string.Empty;
            }
        }

        private static Brush BrushOf(CapabilityResult result)
        {
            switch (result)
            {
                case CapabilityResult.Accepted: return AcceptedBrush;
                case CapabilityResult.AbstractSyntaxNotSupported: return AbstractBrush;
                case CapabilityResult.TransferSyntaxNotSupported: return SyntaxBrush;
                case CapabilityResult.UserRejection:
                case CapabilityResult.NoReason:
                case CapabilityResult.NotDelivered: return RefusedBrush;
                case CapabilityResult.NotAnswered:
                case CapabilityResult.AcceptedOtherSyntax: return IrregularBrush;
                default: return UnknownBrush;
            }
        }

        private static Brush Frozen(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
