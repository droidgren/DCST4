using DCST4.Core.TestData;
using FellowOakDicom;
using System;

namespace DCST4.Core.Generation
{
    /// <summary>
    /// A large private value, in the shape some ultrasound systems use for the raw data they send along with the image:
    /// a private sequence with one item, which holds a label and the bytes. It is never in memory as a whole.
    /// </summary>
    public static class PrivatePayload
    {
        public const string PrivateCreator = "DCST4 TEST";
        public const string Label = "DCST4 test payload";

        public const long BytesPerMegabyte = 1024 * 1024;
        public const int MaxMegabytes = 2000;
        public const long MaxBytes = MaxMegabytes * BytesPerMegabyte;

        // The same group and creator as the private tags of the edge cases, with elements those do not use.
        private const ushort Group = 0x0099;
        private const ushort CreatorElement = 0x0010;
        private const ushort SequenceElement = 0x1020;
        private const ushort LabelElement = 0x1021;
        private const ushort BytesElement = 0x1022;

        /// <summary>The sequence that holds the payload, at the top level of the object.</summary>
        public static DicomTag SequenceTag { get; } = new DicomTag(Group, SequenceElement, PrivateCreator);

        /// <summary>The payload itself, inside the item of <see cref="SequenceTag"/>.</summary>
        public static DicomTag BytesTag { get; } = new DicomTag(Group, BytesElement, PrivateCreator);

        /// <summary>Adds a payload of this many bytes, rounded up to an even number. Nothing is added for 0.</summary>
        public static void Add(DicomDataset dataset, long bytes)
        {
            if (bytes <= 0) return;
            if (bytes > MaxBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
            var size = bytes + bytes % 2;

            // The bytes follow from the object's own UID, so two objects never carry the same payload.
            var seed = BitConverter.ToUInt64(SeededValues.Sha256("private-payload/" + dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty)), 0);

            // Each item names its own creator, as PS3.5 7.8.1 requires.
            var item = new DicomDataset();
            item.Add(DicomVR.LO, new DicomTag(Group, CreatorElement), PrivateCreator);
            item.Add(DicomVR.LO, new DicomTag(Group, LabelElement, PrivateCreator), Label);
            item.Add(new DicomOtherByte(BytesTag, new GeneratedBytesBuffer(size, seed)));

            dataset.AddOrUpdate(DicomVR.LO, new DicomTag(Group, CreatorElement), PrivateCreator);
            dataset.AddOrUpdate(new DicomSequence(SequenceTag, item));
        }

        /// <summary>The size of the payload in this object, or 0 if it has none.</summary>
        public static long BytesIn(DicomDataset dataset)
        {
            if (!dataset.TryGetSequence(SequenceTag, out var sequence) || sequence.Items.Count == 0) return 0;
            var element = sequence.Items[0].GetDicomItem<DicomElement>(BytesTag);
            return element?.Buffer.Size ?? 0;
        }
    }
}
