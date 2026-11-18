using FellowOakDicom;
using System.Collections.Generic;
using System.Threading;

namespace DCST4.Core.Pipeline
{
    /// <summary>
    /// Anything that can supply images to a run. This is the only thing the sending side knows about where images
    /// come from, so the generators and the folder source (FolderImageSource) plug in here without changes to sending.
    /// </summary>
    public interface IImageSource
    {
        /// <summary>Images in sending order. Implementations produce them lazily, so a long stack does not sit in memory.</summary>
        IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken);

        /// <summary>Number of images expected, for progress and for counting images never sent.</summary>
        int ExpectedCount { get; }
    }

    /// <summary>
    /// A source that knows in advance which SOP classes it sends in which syntaxes, as a folder of files does. Sending
    /// proposes them all when the association opens, not only those of the first images.
    /// </summary>
    public interface IPlannedContexts
    {
        /// <summary>SOP Class UID and transfer syntax, once each. <paramref name="target"/> is the syntax of the run, or null for each image's own.</summary>
        IReadOnlyCollection<KeyValuePair<string, DicomTransferSyntax>> PlannedContexts(DicomTransferSyntax target);
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

        /// <summary>The file the image was read from, for a source of existing files. Null for a generated image.</summary>
        public string SourcePath { get; set; }

        /// <summary>Where the image was saved, if it was.</summary>
        public string SavedPath { get; set; }
        /// <summary>Why saving failed, if it did.</summary>
        public string SaveError { get; set; }
        /// <summary>Size of the pixel data in the selected transfer syntax.</summary>
        public long PixelBytes { get; set; }
    }
}
