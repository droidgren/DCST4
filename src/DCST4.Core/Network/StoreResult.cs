using System.Globalization;

namespace DCST4.Core.Network
{
    public enum StoreState
    {
        /// <summary>Not sent or sent without a response: association failure, cancel, or timeout.</summary>
        NotSent,
        Success,
        Warning,
        Failure,
        Cancel,
        /// <summary>Written to disk, sending not requested.</summary>
        Saved,
        /// <summary>Sending not requested, and writing to disk failed.</summary>
        SaveFailed,
    }

    /// <summary>
    /// The outcome of one image: the complete C-STORE response when it was sent. It keeps the SOP Class and Instance UIDs,
    /// which is what a later Storage Commitment request needs.
    /// </summary>
    public sealed class StoreResult
    {
        public int SeriesNumber { get; set; }
        public int InstanceNumber { get; set; }
        public string SopClassUid { get; set; }
        public string SopInstanceUid { get; set; }
        public StoreState State { get; set; }
        /// <summary>Raw (0000,0900), or null if no response was received.</summary>
        public ushort? StatusCode { get; set; }
        public string Description { get; set; }
        /// <summary>(0000,0902).</summary>
        public string ErrorComment { get; set; }
        /// <summary>(0000,0901), formatted like (0010,0010).</summary>
        public string OffendingElement { get; set; }
        public string AcceptedTransferSyntax { get; set; }
        public long ElapsedMs { get; set; }
        public long PixelBytes { get; set; }
        public string SavedPath { get; set; }
        public string SaveError { get; set; }

        public string StatusCodeText => StatusCode.HasValue ? "0x" + StatusCode.Value.ToString("X4", CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>Classifies raw C-STORE status codes. Works from the code itself so that vendor codes keep their real value.</summary>
    public static class StoreStatus
    {
        /// <summary>PS3.7 Annex C status classes.</summary>
        public static StoreState Classify(ushort code)
        {
            if (code == 0x0000) return StoreState.Success;
            if (code == 0xFE00) return StoreState.Cancel;
            if (code == 0x0001 || code == 0x0107 || code == 0x0116 || (code & 0xF000) == 0xB000) return StoreState.Warning;
            return StoreState.Failure;
        }

        public static string Describe(ushort code)
        {
            if ((code & 0xFF00) == 0xA700) return "Refused: Out of Resources";
            if ((code & 0xFF00) == 0xA900) return "Error: Data Set does not match SOP Class";
            if ((code & 0xF000) == 0xC000) return "Error: Cannot understand";

            switch (code)
            {
                case 0x0000: return "Success";
                case 0x0001: return "Warning: requested optional attributes are not supported";
                case 0x0107: return "Warning: Attribute List Error";
                case 0x0116: return "Warning: Attribute Value Out of Range";
                case 0xB000: return "Warning: Coercion of Data Elements";
                case 0xB006: return "Warning: Elements Discarded";
                case 0xB007: return "Warning: Data Set does not match SOP Class";
                case 0x0110: return "Failure: Processing Failure";
                case 0x0111: return "Failure: Duplicate SOP Instance";
                case 0x0117: return "Refused: Invalid SOP Instance";
                case 0x0122: return "Refused: SOP Class Not Supported";
                case 0x0124: return "Refused: Not Authorized";
                case 0x0210: return "Failure: Duplicate Invocation";
                case 0x0211: return "Failure: Unrecognized Operation";
                case 0x0212: return "Failure: Mistyped Argument";
                case 0x0213: return "Failure: Resource Limitation";
                case 0xFE00: return "Cancel";
            }

            switch (Classify(code))
            {
                case StoreState.Warning: return "Warning: vendor-specific or unknown status";
                default: return "Failure: vendor-specific or unknown status";
            }
        }
    }
}
