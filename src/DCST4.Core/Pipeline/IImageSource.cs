using FellowOakDicom;
using System.Collections.Generic;
using System.Threading;

namespace DCST4.Core.Pipeline
{
    /// <summary>
    /// Anything that can supply images to a run. This is the only thing the sending side knows about where images
    /// come from, so a future folder source (with optional UID regeneration) plugs in here without changes to sending.
    /// </summary>
    public interface IImageSource
    {
        /// <summary>Images in sending order. Implementations produce them lazily, so a long stack does not sit in memory.</summary>
        IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken);

        /// <summary>Number of images expected, for progress and for counting images never sent.</summary>
        int ExpectedCount { get; }
    }

    public sealed class SourceImage
    {
        public SourceImage(DicomFile file, int seriesNumber, int instanceNumber)
        {
            File = file;
            SeriesNumber = seriesNumber;
            InstanceNumber = instanceNumber;
        }

        public DicomFile File { get; set; }
        public int SeriesNumber { get; }
        public int InstanceNumber { get; }

        public string SopClassUid => File.Dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
        public string SopInstanceUid => File.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);

        /// <summary>Where the image was saved, if it was.</summary>
        public string SavedPath { get; set; }
        /// <summary>Why saving failed, if it did.</summary>
        public string SaveError { get; set; }
        /// <summary>Size of the pixel data in the selected transfer syntax.</summary>
        public long PixelBytes { get; set; }
    }
}
