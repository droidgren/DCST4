using DCST4.Core.Testing;
using System.Collections.Generic;

namespace DCST4.Core.Verification
{
    public enum IntegrityLevel { Transport, Identity, Attributes, Pixel, TransferSyntax }

    /// <summary>The names are written to the result files as they stand.</summary>
    public enum PixelVerdict
    {
        NoPixelData,
        /// <summary>The same encoded bytes.</summary>
        ByteIdentical,
        /// <summary>The same decoded pixels, in another transfer syntax.</summary>
        LosslessEquivalent,
        /// <summary>The decoded pixels differ.</summary>
        LossyChanged,
        /// <summary>A wrong number of frames or size, or a stream that cannot be decoded.</summary>
        Corrupt,
        /// <summary>No decoder for the syntax it came back in, so nothing can be said.</summary>
        NotCompared,
    }

    /// <summary>The names are written to the result files as they stand.</summary>
    public enum AttributeChangeClass
    {
        /// <summary>Changed, and Original Attributes Sequence says so.</summary>
        DocumentedCoercion,
        /// <summary>Changed or dropped, and nothing says so.</summary>
        SilentModification,
        /// <summary>The start of the value is kept and the rest is gone, and nothing says so.</summary>
        Truncated,
        /// <summary>A private element is gone, and nothing says so.</summary>
        PrivateDropped,
        /// <summary>Not in what was sent. Not a failure: a PACS adds such things as Retrieve AE Title.</summary>
        Added,
        /// <summary>An attribute that was sent empty is gone, which holds no information.</summary>
        EmptyDropped,
        /// <summary>Came back as UN with the same value. What Implicit VR does to a private element.</summary>
        VrLost,
        /// <summary>Could not be compared, such as a private sequence that came back as UN bytes.</summary>
        NotCompared,
    }

    /// <summary>One difference between what was sent and what came back.</summary>
    public sealed class AttributeDifference
    {
        /// <summary>For example (0010,0010), or (0008,1140)[0].(0008,1155) inside a sequence.</summary>
        public string Path { get; set; }
        public string Name { get; set; }
        public AttributeChangeClass Class { get; set; }
        public string Original { get; set; }
        public string Retrieved { get; set; }
    }

    /// <summary>One instance compared. A level's result is on the set it belongs to.</summary>
    public sealed class InstanceIntegrity
    {
        public string SopInstanceUid { get; set; }
        public string SopClassUid { get; set; }
        /// <summary>From the manifest: target or source-image.</summary>
        public string Role { get; set; }

        /// <summary>The UIDs that are not what was sent, as "Series Instance UID: sent → returned".</summary>
        public List<string> IdentityChanges { get; set; } = new List<string>();

        public List<AttributeDifference> Differences { get; set; } = new List<AttributeDifference>();
        public bool NormalizedHashMatchesManifest { get; set; }
        /// <summary>Paths of the private elements that survived.</summary>
        public List<string> PrivateKept { get; set; } = new List<string>();
        /// <summary>Paths of the private elements that did not.</summary>
        public List<string> PrivateDropped { get; set; } = new List<string>();
        public string CharacterSetSent { get; set; }
        public string CharacterSetReturned { get; set; }
        public bool CharacterSetChanged { get; set; }

        public PixelVerdict Pixel { get; set; }
        public string PixelDetail { get; set; }
        /// <summary>Largest absolute difference over all samples. Null unless the pixels were compared and differ.</summary>
        public double? MaxAbsoluteDifference { get; set; }
        /// <summary>Mean absolute difference over all samples. Null unless the pixels were compared and differ.</summary>
        public double? MeanAbsoluteDifference { get; set; }
        /// <summary>Whether the decoded frames match the manifest's hashes. Null when they cannot, as for a lossy original.</summary>
        public bool? FramesMatchManifest { get; set; }

        public string TransferSyntaxSent { get; set; }
        public string TransferSyntaxReturned { get; set; }
        public List<string> TransferSyntaxesOffered { get; set; } = new List<string>();
        /// <summary>Whether the PACS transcodes on store or on retrieve, as far as can be told from outside.</summary>
        public string TranscodingInference { get; set; }
    }

    /// <summary>The verdict of one level for one retrieve of one object type.</summary>
    public sealed class IntegrityLevelResult
    {
        public IntegrityLevel Level { get; set; }
        public TestResultClass Result { get; set; }
        public string Explanation { get; set; }
    }

    /// <summary>One retrieve of one object type, compared with what was sent.</summary>
    public sealed class IntegritySet
    {
        public string ObjectType { get; set; }
        public string Method { get; set; }
        public int ExpectedCount { get; set; }
        public int ReturnedCount { get; set; }
        /// <summary>SOP Instance UIDs that were sent and did not come back.</summary>
        public List<string> Missing { get; set; } = new List<string>();
        /// <summary>SOP Instance UIDs that came back and were not sent.</summary>
        public List<string> Extra { get; set; } = new List<string>();
        public List<IntegrityLevelResult> Levels { get; set; } = new List<IntegrityLevelResult>();
        public List<InstanceIntegrity> Instances { get; set; } = new List<InstanceIntegrity>();
        /// <summary>The worst of the levels.</summary>
        public TestResultClass Result { get; set; }
    }

    public sealed class IntegrityReport
    {
        public List<IntegritySet> Sets { get; set; } = new List<IntegritySet>();
    }
}
