using DCST4.Core.Generation.Datasets;
using DCST4.Core.Pipeline;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Testing
{
    /// <summary>
    /// What a test leaves on the destination: how many examinations (studies) and objects, and about how much data. Sizes are
    /// uncompressed, which is the most a test sends; a compressed transfer syntax sends less.
    /// </summary>
    public sealed class StorageEstimate
    {
        /// <summary>An SR, KOS, presentation state, PDF or RT Structure Set as the generator writes it. They are 1.6 to 2 KB.</summary>
        public const long NonImageObjectBytes = 2048;

        private const int BitsPerByte = 8;

        public StorageEstimate(int studies, int objects, long bytes, string note = null)
        {
            Studies = studies;
            Objects = objects;
            Bytes = bytes;
            Note = note;
        }

        public int Studies { get; }
        public int Objects { get; }
        public long Bytes { get; }
        /// <summary>What the numbers leave out, such as a size that depends on the destination. Null if nothing.</summary>
        public string Note { get; }

        /// <summary>One uncompressed image file: header and pixel data.</summary>
        public static long ImageBytes(int rows, int columns, int frames = 1, int bitsAllocated = 16, int samplesPerPixel = 1)
        {
            var pixelBytes = ((long)rows * columns * frames * samplesPerPixel * bitsAllocated + BitsPerByte - 1) / BitsPerByte;
            return ExamSizeEstimator.HeaderBytes + pixelBytes + pixelBytes % 2;
        }

        /// <summary>CT images of <paramref name="matrix"/> by <paramref name="matrix"/> at 16 bits, as most tests send.</summary>
        public static StorageEstimate CtImages(int studies, int objects, int matrix, string note = null) =>
            new StorageEstimate(studies, objects, objects * ImageBytes(matrix, matrix), note);

        /// <summary>One study of a dataset profile at <paramref name="matrix"/>, with the image an object refers to when it needs one.</summary>
        public static StorageEstimate Of(DatasetProfile profile, int matrix, int frames = 0)
        {
            long bytes;
            if (profile.IsImage)
            {
                var image = profile.Image;
                var bits = image.IsBinaryMask ? 1 : image.DefaultBitDepth > BitsPerByte ? 16 : BitsPerByte;
                bytes = ImageBytes(matrix, matrix, frames > 0 ? frames : profile.DefaultFrames, bits, image.SamplesPerPixel);
            }
            else
            {
                bytes = NonImageObjectBytes;
            }

            var objects = 1;
            if (profile.NeedsSourceImage)
            {
                objects++;
                bytes += ImageBytes(matrix, matrix);
            }
            return new StorageEstimate(1, objects, bytes);
        }

        public static StorageEstimate Sum(IEnumerable<StorageEstimate> estimates)
        {
            var all = estimates.Where(e => e != null).ToList();
            return new StorageEstimate(all.Sum(e => e.Studies), all.Sum(e => e.Objects), all.Sum(e => e.Bytes));
        }

        /// <summary>"2 examinations, 3 objects, about 102 KB"</summary>
        public string Describe() =>
            $"{Count(Studies, "examination")}, {Count(Objects, "object")}, about {ExamSizeEstimator.Format(Bytes)}";

        private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "s");
    }
}
