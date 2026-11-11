using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Performance
{
    /// <summary>One object type in a mix, as a dataset profile with its image size.</summary>
    public sealed class DataMixEntry
    {
        public DataMixEntry(string profileId, int rows, int columns, int bitsStored, int frames = 1, int samplesPerPixel = 1)
        {
            ProfileId = profileId;
            Rows = rows;
            Columns = columns;
            BitsStored = bitsStored;
            Frames = frames;
            SamplesPerPixel = samplesPerPixel;
        }

        public string ProfileId { get; }
        public int Rows { get; }
        public int Columns { get; }
        public int BitsStored { get; }
        public int Frames { get; }
        public int SamplesPerPixel { get; }

        /// <summary>The pixel data uncompressed, plus a few kilobytes of attributes.</summary>
        public long EstimatedBytes => (long)Rows * Columns * (BitsStored > 8 ? 2 : 1) * SamplesPerPixel * Frames + HeaderEstimate;

        internal const int HeaderEstimate = 4000;
    }

    /// <summary>What a load run sends: object types in one transfer syntax, sent in turn.</summary>
    public sealed class DataMix
    {
        /// <summary>Generated per object type, so that not every send has the same pixels.</summary>
        public const int DefaultPoolPerEntry = 4;

        public DataMix(string id, string description, DicomTransferSyntax syntax, params DataMixEntry[] entries)
        {
            Id = id;
            Description = description;
            Syntax = syntax;
            Entries = entries;
        }

        public string Id { get; }
        public string Description { get; }
        public DicomTransferSyntax Syntax { get; }
        public IReadOnlyList<DataMixEntry> Entries { get; }
        public int PoolPerEntry { get; set; } = DefaultPoolPerEntry;

        /// <summary>The mean over the object types, uncompressed. For a compressed mix it is an upper bound.</summary>
        public long EstimatedBytesPerObject => Entries.Count == 0 ? 0 : (long)Entries.Average(e => (double)e.EstimatedBytes);

        public override string ToString() => Id;
    }

    /// <summary>The mixes a load run can send. The ids are stable, since result files name them.</summary>
    public static class DataMixCatalog
    {
        public static IReadOnlyList<DataMix> All { get; } = new[]
        {
            new DataMix("ct", "CT 256 x 256, 16 bits, Explicit VR LE, about 130 KB each", DicomTransferSyntax.ExplicitVRLittleEndian,
                new DataMixEntry("ct", 256, 256, 16)),
            new DataMix("small", "Many small objects: CR 512 x 512 and US 800 x 600, Explicit VR LE", DicomTransferSyntax.ExplicitVRLittleEndian,
                new DataMixEntry("cr", 512, 512, 12), new DataMixEntry("us", 600, 800, 8)),
            new DataMix("small-lossless", "The small objects in JPEG Lossless (Process 14 SV1)", DicomTransferSyntax.JPEGProcess14SV1,
                new DataMixEntry("cr", 512, 512, 12), new DataMixEntry("us", 600, 800, 8)),
            new DataMix("large", "Few large objects: Enhanced CT 512 x 512 x 32 frames and Breast Tomosynthesis 1024 x 1024 x 16 frames, Explicit VR LE", DicomTransferSyntax.ExplicitVRLittleEndian,
                new DataMixEntry("enhanced-ct", 512, 512, 16, 32), new DataMixEntry("tomosynthesis-large", 1024, 1024, 14, 16)),
            new DataMix("large-lossless", "Enhanced CT 512 x 512 x 32 frames in JPEG Lossless. Breast Tomosynthesis is generated uncompressed only", DicomTransferSyntax.JPEGProcess14SV1,
                new DataMixEntry("enhanced-ct", 512, 512, 16, 32)),
        };

        public static DataMix Find(string id) =>
            All.FirstOrDefault(m => string.Equals(m.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
