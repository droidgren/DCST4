using System.Linq;

namespace DCST4.Core.Network
{
    /// <summary>Where to connect and as whom.</summary>
    public sealed class DicomEndpoint
    {
        public const int MaxAeTitleLength = 16;

        public string Host { get; set; }
        public int Port { get; set; } = 104;
        public string CallingAe { get; set; }
        public string CalledAe { get; set; }
        public bool UseTls { get; set; }
        /// <summary>Accept self-signed or otherwise invalid server certificates.</summary>
        public bool TlsIgnoreCertErrors { get; set; }
        /// <summary>Thumbprint of the client certificate in CurrentUser\My, or empty for none.</summary>
        public string TlsClientCertThumbprint { get; set; }

        /// <summary>A copy with surrounding spaces removed from host and AE titles.</summary>
        public DicomEndpoint Trimmed() => new DicomEndpoint
        {
            Host = Host?.Trim(),
            Port = Port,
            CallingAe = CallingAe?.Trim(),
            CalledAe = CalledAe?.Trim(),
            UseTls = UseTls,
            TlsIgnoreCertErrors = TlsIgnoreCertErrors,
            TlsClientCertThumbprint = TlsClientCertThumbprint,
        };

        /// <summary>Returns what is wrong with the host only, or null if it is usable. Enough for Ping.</summary>
        public string ValidateHost() =>
            string.IsNullOrWhiteSpace(Host) ? "Called host is empty." : null;

        /// <summary>Returns the first problem with these connection values, or null if they are valid.</summary>
        public string Validate()
        {
            var hostError = ValidateHost();
            if (hostError != null) return hostError;
            if (Port < 1 || Port > 65535) return $"Port {Port} is invalid. It must be between 1 and 65535.";
            return ValidateAeTitle("Calling AET", CallingAe) ?? ValidateAeTitle("Called AET", CalledAe);
        }

        internal static string ValidateAeTitle(string field, string value)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return $"{field} is empty.";
            if (trimmed.Length > MaxAeTitleLength) return $"{field} '{trimmed}' is longer than {MaxAeTitleLength} characters.";
            if (trimmed.Any(c => c < 0x20 || c > 0x7E || c == '\\')) return $"{field} '{trimmed}' contains a character that is not allowed in an AE title.";
            return null;
        }
    }

    /// <summary>Timeouts and association logging.</summary>
    public sealed class NetworkOptions
    {
        public int ConnectionTimeoutSeconds { get; set; } = 10;
        public int AssociationTimeoutSeconds { get; set; } = 10;
        /// <summary>Time to wait for each response. 0 = no limit.</summary>
        public int ResponseTimeoutSeconds { get; set; } = 60;
        public bool LogDimseDatasets { get; set; }
        public bool LogDataPdus { get; set; }
    }
}
