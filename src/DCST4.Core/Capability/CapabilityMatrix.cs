using DCST4.Core.Testing;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCST4.Core.Capability
{
    /// <summary>The names are written to the matrix files as they stand. Renaming one breaks comparison across versions.</summary>
    public enum CapabilityResult
    {
        /// <summary>The probe never got this far, for example because the run was cancelled.</summary>
        NotProbed,
        Accepted,
        AbstractSyntaxNotSupported,
        TransferSyntaxNotSupported,
        UserRejection,
        NoReason,
        /// <summary>The association was accepted, but the A-ASSOCIATE-AC had no item for this context. PS3.8 requires one.</summary>
        NotAnswered,
        /// <summary>Accepted, but with a transfer syntax that was not the one proposed.</summary>
        AcceptedOtherSyntax,
        /// <summary>The whole association was refused with A-ASSOCIATE-RJ, so the context was never considered.</summary>
        AssociationRejected,
        /// <summary>The association failed before the SCP answered, for example by timeout or a closed connection.</summary>
        AssociationFailed,
    }

    public enum AssociationEnding { NotStarted, Released, Rejected, Aborted, TimedOut, ConnectionFailed, Cancelled, LocalProblem }

    /// <summary>Thrown when a matrix file cannot be read or written. The message is ready to show to the user.</summary>
    public sealed class CapabilityFileException : Exception
    {
        public CapabilityFileException(string message, Exception inner = null) : base(message, inner) { }
    }

    public sealed class MatrixSyntax
    {
        public string Uid { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
    }

    public sealed class MatrixCell
    {
        [JsonRequired]
        public string TransferSyntaxUid { get; set; }
        [JsonRequired]
        public CapabilityResult Result { get; set; }
        /// <summary>What the SCP chose, when it accepted the context.</summary>
        public string AcceptedTransferSyntaxUid { get; set; }
        /// <summary>Which association of the probe carried this cell, from 1.</summary>
        public int? Association { get; set; }
        public int? PresentationContextId { get; set; }
    }

    public sealed class MatrixRow
    {
        [JsonRequired]
        public string SopClassUid { get; set; }
        public string SopClassName { get; set; }
        public List<MatrixCell> Cells { get; set; } = new List<MatrixCell>();
    }

    /// <summary>How one association of the probe went.</summary>
    public sealed class ProbeAssociationRecord
    {
        public int Index { get; set; }
        public bool IsFeatureProbe { get; set; }
        public int ProposedContexts { get; set; }
        public AssociationEnding Ending { get; set; }
        /// <summary>Why it did not end in a release, as the user should read it. Null when it did.</summary>
        public string Detail { get; set; }
        public AssociationRejectionRecord Rejection { get; set; }
        public string RemoteImplementationClassUid { get; set; }
        public string RemoteImplementationVersionName { get; set; }
        public long? RemoteMaxPduLength { get; set; }
    }

    public sealed class AssociationRejectionRecord
    {
        public string Result { get; set; }
        public string Source { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>The answer to our proposal to take the SCP role for a storage SOP class, which C-GET needs.</summary>
    public sealed class RoleSelectionReply
    {
        public string SopClassUid { get; set; }
        public string SopClassName { get; set; }
        /// <summary>False if the SCP sent no role selection item for this SOP class, so the default roles apply.</summary>
        public bool Replied { get; set; }
        public bool? ScuRole { get; set; }
        public bool? ScpRole { get; set; }
    }

    public sealed class ExtendedNegotiationReply
    {
        public string SopClassUid { get; set; }
        public string SopClassName { get; set; }
        /// <summary>The application information we proposed, as hexadecimal bytes.</summary>
        public string Requested { get; set; }
        /// <summary>False if the SCP did not answer this item, which means no optional behaviour is agreed.</summary>
        public bool Replied { get; set; }
        public string Accepted { get; set; }
    }

    /// <summary>
    /// What an SCP supports, per SOP class and transfer syntax. A vendor's claims are written in the same shape, of which only
    /// rows[].sopClassUid and rows[].cells[].transferSyntaxUid and result are required.
    /// </summary>
    public sealed class CapabilityMatrix
    {
        public const int CurrentSchemaVersion = 1;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly string[] CsvColumns =
        {
            "sop_class_uid", "sop_class_name", "transfer_syntax_uid", "transfer_syntax_name", "result", "accepted_transfer_syntax_uid", "association", "presentation_context_id",
        };

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string ToolVersion { get; set; }
        public ResultProfile Target { get; set; }
        public string RemoteImplementationClassUid { get; set; }
        public string RemoteImplementationVersionName { get; set; }
        public long? RemoteMaxPduLength { get; set; }
        public List<MatrixSyntax> TransferSyntaxes { get; set; } = new List<MatrixSyntax>();
        public List<MatrixRow> Rows { get; set; } = new List<MatrixRow>();
        public List<ProbeAssociationRecord> Associations { get; set; } = new List<ProbeAssociationRecord>();
        public List<RoleSelectionReply> RoleSelectionReplies { get; set; } = new List<RoleSelectionReply>();
        public List<ExtendedNegotiationReply> ExtendedNegotiationReplies { get; set; } = new List<ExtendedNegotiationReply>();

        /// <summary>The cell of this combination, or null if the matrix does not hold it.</summary>
        public MatrixCell Find(string sopClassUid, string transferSyntaxUid) =>
            Rows.FirstOrDefault(r => r.SopClassUid == sopClassUid)?.Cells.FirstOrDefault(c => c.TransferSyntaxUid == transferSyntaxUid);

        /// <summary>All cells with the row each belongs to.</summary>
        public IEnumerable<Tuple<MatrixRow, MatrixCell>> AllCells() => Rows.SelectMany(r => r.Cells.Select(c => Tuple.Create(r, c)));

        /// <summary>The SCP gave its own verdict on this context, as opposed to never getting to see it.</summary>
        public static bool WasAnswered(CapabilityResult result) =>
            result != CapabilityResult.NotProbed && result != CapabilityResult.AssociationRejected && result != CapabilityResult.AssociationFailed;

        public string ToJson() => JsonSerializer.Serialize(this, ResultJson.Options);

        /// <summary>One row per cell, UTF-8 and independent of the culture.</summary>
        public string ToCsv()
        {
            var names = TransferSyntaxes.ToDictionary(s => s.Uid, s => s.Name);
            var text = new StringBuilder();
            ResultFileWriter.AppendRow(text, CsvColumns);
            foreach (var entry in AllCells())
            {
                var cell = entry.Item2;
                names.TryGetValue(cell.TransferSyntaxUid, out var syntaxName);
                ResultFileWriter.AppendRow(text, new[]
                {
                    entry.Item1.SopClassUid, entry.Item1.SopClassName, cell.TransferSyntaxUid, syntaxName, cell.Result.ToString(), cell.AcceptedTransferSyntaxUid,
                    Number(cell.Association), Number(cell.PresentationContextId),
                });
            }
            return text.ToString();
        }

        public void SaveJson(string path) => Save(path, ToJson());
        public void SaveCsv(string path) => Save(path, ToCsv());

        /// <summary>Reads a matrix, or a vendor's claims in the same shape.</summary>
        public static CapabilityMatrix Load(string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                throw new CapabilityFileException($"Could not read {path}: {ex.Message}", ex);
            }

            CapabilityMatrix matrix;
            try
            {
                matrix = JsonSerializer.Deserialize<CapabilityMatrix>(json, ResultJson.Options) ?? throw new JsonException("The file holds no matrix.");
            }
            catch (JsonException ex)
            {
                throw new CapabilityFileException($"{path} is not valid: {ex.Message}", ex);
            }

            var problem = matrix.Validate();
            if (problem != null) throw new CapabilityFileException($"{path} is not valid: {problem}");
            return matrix;
        }

        /// <summary>The display name of a UID, or the UID itself if fo-dicom does not know it.</summary>
        public static string DisplayName(string uid)
        {
            var known = DicomUID.Parse(uid);
            return known.Name == "Unknown" || string.IsNullOrEmpty(known.Name) ? uid : known.Name;
        }

        private string Validate()
        {
            if (SchemaVersion > CurrentSchemaVersion) return $"it has schema version {SchemaVersion}, and this version of DCST4 reads up to {CurrentSchemaVersion}.";
            if (Rows == null || Rows.Count == 0) return "it has no rows.";

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.SopClassUid)) return "a row has no sopClassUid.";
                if (row.Cells == null) return $"{row.SopClassUid} has no cells.";
                foreach (var cell in row.Cells)
                {
                    if (cell == null || string.IsNullOrWhiteSpace(cell.TransferSyntaxUid)) return $"a cell of {row.SopClassUid} has no transferSyntaxUid.";
                    if (!seen.Add(row.SopClassUid + "|" + cell.TransferSyntaxUid)) return $"{row.SopClassUid} lists {cell.TransferSyntaxUid} twice.";
                }
            }
            return null;
        }

        private static void Save(string path, string content)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, content, Utf8NoBom);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                throw new CapabilityFileException($"Could not save {path}: {ex.Message}", ex);
            }
        }

        private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        private static bool IsFileProblem(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException;
    }
}
