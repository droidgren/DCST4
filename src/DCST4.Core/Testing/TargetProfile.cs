using DCST4.Core.Generation;
using DCST4.Core.Network;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DCST4.Core.Testing
{
    /// <summary>Thrown when a target profile file cannot be read or written. The message is ready to show to the user.</summary>
    public sealed class TargetProfileException : Exception
    {
        public TargetProfileException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>The system under test and how to talk to it. Saved as JSON.</summary>
    public sealed class TargetProfile
    {
        public const int CurrentSchemaVersion = 1;
        public const string DefaultLocalScpAe = "DCST4";
        public const int DefaultLocalScpPort = 11113;
        /// <summary>The UUID-derived arc of PS3.5 B.2, which needs no registered organisation root.</summary>
        public const string DefaultUidRoot = "2.25";
        /// <summary>Leaves room within the 64 characters of a UID for what is added after the root.</summary>
        public const int MaxUidRootLength = TestData.DeterministicUidSource.MaxRootLength;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string Name { get; set; }
        public DicomEndpoint Endpoint { get; set; }
        public NetworkOptions Network { get; set; } = new NetworkOptions();
        /// <summary>Our AE title for C-MOVE and Storage Commitment callbacks.</summary>
        public string LocalScpAe { get; set; } = DefaultLocalScpAe;
        public int LocalScpPort { get; set; } = DefaultLocalScpPort;
        public string UidRoot { get; set; } = DefaultUidRoot;

        /// <summary>A copy with surrounding spaces removed from the values that are sent on the wire.</summary>
        public TargetProfile Trimmed() => new TargetProfile
        {
            SchemaVersion = SchemaVersion,
            Name = Name,
            Endpoint = Endpoint?.Trimmed(),
            Network = Network,
            LocalScpAe = LocalScpAe?.Trim(),
            LocalScpPort = LocalScpPort,
            UidRoot = UidRoot?.Trim(),
        };

        /// <summary>Returns the first problem with this profile, or null if it is valid.</summary>
        public string Validate()
        {
            if (SchemaVersion > CurrentSchemaVersion) return $"The profile has schema version {SchemaVersion}, and this version of DCST4 reads up to {CurrentSchemaVersion}.";
            if (Endpoint == null) return "The profile has no endpoint section.";
            if (Network == null) return "The profile has an empty network section.";

            var problem = Endpoint.Validate() ?? DicomEndpoint.ValidateAeTitle("Local SCP AET", LocalScpAe);
            if (problem != null) return problem;

            if (LocalScpPort < 1 || LocalScpPort > 65535) return $"Local SCP port {LocalScpPort} is invalid. It must be between 1 and 65535.";

            problem = ValidateUidRoot(UidRoot);
            if (problem != null) return problem;

            if (Network.ConnectionTimeoutSeconds < 0 || Network.AssociationTimeoutSeconds < 0 || Network.ResponseTimeoutSeconds < 0) return "Timeouts cannot be negative.";
            return null;
        }

        /// <summary>Returns what is wrong with a UID root, or null if it can be used.</summary>
        public static string ValidateUidRoot(string uidRoot)
        {
            var root = uidRoot?.Trim();
            if (!ExamValidator.IsValidUid(root) || root.Length > MaxUidRootLength)
            {
                return $"UID root '{root}' is invalid. It must be at most {MaxUidRootLength} characters, digits and dots, with no leading zeros in a component.";
            }
            return null;
        }

        public static TargetProfile Load(string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new TargetProfileException($"Could not read the profile {path}: {ex.Message}", ex);
            }

            try
            {
                return JsonSerializer.Deserialize<TargetProfile>(json, ResultJson.Options)
                    ?? throw new JsonException("The file holds no profile.");
            }
            catch (JsonException ex)
            {
                throw new TargetProfileException($"The profile {path} is not valid: {ex.Message}", ex);
            }
        }

        public void Save(string path)
        {
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonSerializer.Serialize(this, ResultJson.Options), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                throw new TargetProfileException($"Could not save the profile {path}: {ex.Message}", ex);
            }
        }
    }
}
