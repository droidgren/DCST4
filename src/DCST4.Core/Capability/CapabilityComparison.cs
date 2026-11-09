using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Capability
{
    public enum CapabilityDifferenceKind
    {
        /// <summary>The vendor claims it, and the SCP refused it.</summary>
        ClaimedButRejected,
        /// <summary>The SCP accepted it, and the vendor does not claim it.</summary>
        AcceptedButNotClaimed,
        /// <summary>The vendor claims it, and the probe never got an answer, so nothing is known either way.</summary>
        ClaimedButNotProbed,
    }

    public sealed class CapabilityDifference
    {
        public CapabilityDifferenceKind Kind { get; set; }
        public string SopClassUid { get; set; }
        public string SopClassName { get; set; }
        public string TransferSyntaxUid { get; set; }
        public string TransferSyntaxName { get; set; }
        /// <summary>Null when the probe did not include this combination.</summary>
        public CapabilityResult? Actual { get; set; }
    }

    /// <summary>What a Conformance Statement claims against what the SCP did. Only a claim of Accepted counts as a claim.</summary>
    public sealed class CapabilityComparison
    {
        public List<CapabilityDifference> Differences { get; } = new List<CapabilityDifference>();
        /// <summary>Combinations where the claim and the SCP's own answer agree.</summary>
        public int Agreeing { get; private set; }

        public IEnumerable<CapabilityDifference> OfKind(CapabilityDifferenceKind kind) => Differences.Where(d => d.Kind == kind);

        public static CapabilityComparison Compare(CapabilityMatrix actual, CapabilityMatrix claimed)
        {
            if (actual == null) throw new ArgumentNullException(nameof(actual));
            if (claimed == null) throw new ArgumentNullException(nameof(claimed));

            var comparison = new CapabilityComparison();

            foreach (var entry in actual.AllCells())
            {
                var row = entry.Item1;
                var cell = entry.Item2;
                var isClaimed = claimed.Find(row.SopClassUid, cell.TransferSyntaxUid)?.Result == CapabilityResult.Accepted;

                if (cell.Result == CapabilityResult.Accepted)
                {
                    if (isClaimed) comparison.Agreeing++;
                    else comparison.Add(CapabilityDifferenceKind.AcceptedButNotClaimed, row.SopClassUid, cell.TransferSyntaxUid, cell.Result);
                }
                else if (!isClaimed)
                {
                    if (CapabilityMatrix.WasAnswered(cell.Result)) comparison.Agreeing++;
                }
                else if (CapabilityMatrix.WasAnswered(cell.Result))
                {
                    comparison.Add(CapabilityDifferenceKind.ClaimedButRejected, row.SopClassUid, cell.TransferSyntaxUid, cell.Result);
                }
                else
                {
                    comparison.Add(CapabilityDifferenceKind.ClaimedButNotProbed, row.SopClassUid, cell.TransferSyntaxUid, cell.Result);
                }
            }

            // Claims about combinations the probe did not propose at all.
            foreach (var entry in claimed.AllCells().Where(e => e.Item2.Result == CapabilityResult.Accepted))
            {
                if (actual.Find(entry.Item1.SopClassUid, entry.Item2.TransferSyntaxUid) == null)
                {
                    comparison.Add(CapabilityDifferenceKind.ClaimedButNotProbed, entry.Item1.SopClassUid, entry.Item2.TransferSyntaxUid, null);
                }
            }

            return comparison;
        }

        private void Add(CapabilityDifferenceKind kind, string sopClassUid, string transferSyntaxUid, CapabilityResult? actual)
        {
            Differences.Add(new CapabilityDifference
            {
                Kind = kind,
                SopClassUid = sopClassUid,
                SopClassName = CapabilityMatrix.DisplayName(sopClassUid),
                TransferSyntaxUid = transferSyntaxUid,
                TransferSyntaxName = CapabilityMatrix.DisplayName(transferSyntaxUid),
                Actual = actual,
            });
        }
    }
}
