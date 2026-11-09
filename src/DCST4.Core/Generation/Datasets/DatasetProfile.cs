using DCST4.Core.Generation.Modalities;
using DCST4.Core.Generation.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Generation.Datasets
{
    /// <summary>A named kind of test-suite dataset: an image type or a non-image object.</summary>
    public sealed class DatasetProfile
    {
        private DatasetProfile(string id, ModalityProfile image, ObjectProfile document, int defaultFrames, bool includedByDefault)
        {
            Id = id;
            Image = image;
            Object = document;
            DefaultFrames = defaultFrames;
            IncludedByDefault = includedByDefault;
        }

        public string Id { get; }
        /// <summary>Null for a non-image object.</summary>
        public ModalityProfile Image { get; }
        /// <summary>Null for an image.</summary>
        public ObjectProfile Object { get; }
        public int DefaultFrames { get; }
        /// <summary>False for a profile too large to run without being asked for.</summary>
        public bool IncludedByDefault { get; }

        public string DisplayName => Image != null ? Image.DisplayName : Object.DisplayName;
        public bool IsImage => Image != null;
        public bool HasPixelData => Image != null;
        public ExamPreset Preset => Image != null ? Image.Presets[0] : Object.Preset;

        /// <summary>The object refers to an image, so one small image is generated first in each study.</summary>
        public bool NeedsSourceImage => Object != null ? Object.NeedsSourceImage : Image.IsBinaryMask;

        public override string ToString() => Id;

        internal static DatasetProfile ForImage(string id, ModalityProfile image, int defaultFrames = 1, bool includedByDefault = true) =>
            new DatasetProfile(id, image, null, defaultFrames, includedByDefault);

        internal static DatasetProfile ForObject(string id, ObjectProfile document) =>
            new DatasetProfile(id, null, document, 1, true);
    }

    public static class DatasetProfileCatalog
    {
        private const int DefaultCineFrames = 8;
        private const int DefaultSegmentationFrames = 4;

        public static IReadOnlyList<DatasetProfile> All { get; } = Create();

        /// <summary>Everything except the profiles that have to be asked for by name.</summary>
        public static IEnumerable<DatasetProfile> Default => All.Where(p => p.IncludedByDefault);

        public static DatasetProfile Find(string id) =>
            All.FirstOrDefault(p => string.Equals(p.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

        private static IReadOnlyList<DatasetProfile> Create()
        {
            var profiles = ModalityCatalog.All.Select(m => DatasetProfile.ForImage(m.Code.ToLowerInvariant(), m)).ToList();
            profiles.Add(DatasetProfile.ForImage("us-multiframe", new UsMultiFrameProfile(), DefaultCineFrames));
            profiles.Add(DatasetProfile.ForImage("secondary-capture", new SecondaryCaptureProfile()));
            profiles.Add(DatasetProfile.ForImage("enhanced-ct", new EnhancedCtProfile(), DefaultCineFrames));
            profiles.Add(DatasetProfile.ForImage("enhanced-mr", new EnhancedMrProfile(), DefaultCineFrames));
            profiles.Add(DatasetProfile.ForImage("tomosynthesis-large", new BreastTomosynthesisProfile(), BreastTomosynthesisProfile.DefaultFrames, includedByDefault: false));
            profiles.Add(DatasetProfile.ForObject("basic-text-sr", new BasicTextSrProfile()));
            profiles.Add(DatasetProfile.ForObject("key-object-selection", new KeyObjectProfile()));
            profiles.Add(DatasetProfile.ForObject("grayscale-presentation-state", new PresentationStateProfile()));
            profiles.Add(DatasetProfile.ForObject("encapsulated-pdf", new EncapsulatedPdfProfile()));
            profiles.Add(DatasetProfile.ForImage("segmentation", new SegmentationProfile(), DefaultSegmentationFrames));
            profiles.Add(DatasetProfile.ForObject("rt-structure-set", new RtStructureSetProfile()));
            return profiles;
        }
    }
}
