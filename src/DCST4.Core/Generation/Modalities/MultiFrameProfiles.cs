using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Generation.Modalities
{
    // These are used by the test-suite datasets. They are not in ModalityCatalog, which is the list offered in Generate & Send.

    /// <summary>
    /// Ultrasound Multi-frame Image IOD: a colour cine loop, RGB at 8 bits per sample. A real loop has hundreds of frames, so
    /// they are drawn as they are written.
    /// </summary>
    public sealed class UsMultiFrameProfile : UsProfile
    {
        public const int CineFrames = 30;
        private const int FrameTimeMs = 33;
        private const int FramesPerSecond = 30;

        public override string DisplayName => "US Multi-frame";
        public override string ObjectName => "Ultrasound Multi-frame Image";
        public override DicomUID SopClass => DicomUID.UltrasoundMultiFrameImageStorage;
        public override int SamplesPerPixel => 3;
        public override bool IsMultiFrame => true;
        public override bool RendersFramesOnDemand => true;
        public override int DefaultFrames => CineFrames;

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            base.AddModalityTags(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.FrameTime, FrameTimeMs.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.FrameIncrementPointer, DicomTag.FrameTime);
            dataset.AddOrUpdate(DicomTag.CineRate, FramesPerSecond.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.RecommendedDisplayFrameRate, FramesPerSecond.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Secondary Capture Image IOD, RGB at 8 bits per sample.</summary>
    public sealed class SecondaryCaptureProfile : ModalityProfile
    {
        public override string Code => "OT";
        public override string DisplayName => "Secondary Capture";
        public override DicomUID SopClass => DicomUID.SecondaryCaptureImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8 };
        public override int DefaultBitDepth => 8;
        public override int SamplesPerPixel => 3;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Screen capture", StudyDescription = "Screen capture", SeriesDescription = "Screen capture", ProtocolName = "Screen capture", BodyPart = string.Empty },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "DERIVED", "SECONDARY" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.ConversionType, "WSD");
            dataset.AddOrUpdate(DicomTag.PatientOrientation, new string[0]);
        }
    }

    /// <summary>
    /// The multi-frame functional group structure shared by the enhanced IODs. Image Type value 1 is DERIVED: these frames are
    /// drawn, not measured, and the many acquisition macros of the IODs are only required for ORIGINAL and MIXED images.
    /// </summary>
    public abstract class FunctionalGroupProfile : ModalityProfile
    {
        public const int EnhancedFrames = 8;
        private const string StackId = "1";

        public override bool IsMultiFrame => true;
        public override bool UsesFunctionalGroups => true;
        public override bool UsesFrameOfReference => true;

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.PositionReferenceIndicator, string.Empty);
            AddDimensions(dataset, context);

            var spacing = preset.FieldOfViewMm / Math.Max(context.Rows, context.Columns);
            var shared = new DicomDataset
            {
                new DicomSequence(DicomTag.PixelMeasuresSequence, new DicomDataset
                {
                    { DicomTag.PixelSpacing, Ds(spacing), Ds(spacing) },
                    { DicomTag.SliceThickness, Ds(preset.SliceThicknessMm) },
                    { DicomTag.SpacingBetweenSlices, Ds(preset.SliceThicknessMm) },
                }),
                new DicomSequence(DicomTag.PlaneOrientationSequence, new DicomDataset
                {
                    { DicomTag.ImageOrientationPatient, "1", "0", "0", "0", "1", "0" },
                }),
            };
            AddSharedGroups(shared, preset);
            dataset.AddOrUpdate(new DicomSequence(DicomTag.SharedFunctionalGroupsSequence, shared));

            var perFrame = new DicomDataset[context.Frames];
            for (var frame = 0; frame < context.Frames; frame++)
            {
                var offset = (frame - (context.Frames - 1) / 2.0) * preset.SliceThicknessMm;
                var item = new DicomDataset
                {
                    new DicomSequence(DicomTag.FrameContentSequence, new DicomDataset
                    {
                        { DicomTag.DimensionIndexValues, DimensionIndexValues(frame) },
                        { DicomTag.StackID, StackId },
                        { DicomTag.InStackPositionNumber, (uint)(frame + 1) },
                    }),
                    new DicomSequence(DicomTag.PlanePositionSequence, new DicomDataset
                    {
                        { DicomTag.ImagePositionPatient, Ds(-context.Columns * spacing / 2), Ds(-context.Rows * spacing / 2), Ds(offset) },
                    }),
                };
                AddPerFrameGroups(item, context);
                perFrame[frame] = item;
            }
            dataset.AddOrUpdate(new DicomSequence(DicomTag.PerFrameFunctionalGroupsSequence, perFrame));
        }

        /// <summary>What a frame's Dimension Index Values point at, as pairs of index and the sequence holding it.</summary>
        protected virtual DicomTag[][] DimensionPointers { get; } =
        {
            new[] { DicomTag.StackID, DicomTag.FrameContentSequence },
            new[] { DicomTag.InStackPositionNumber, DicomTag.FrameContentSequence },
        };

        protected virtual uint[] DimensionIndexValues(int frame) => new[] { 1u, (uint)(frame + 1) };

        protected abstract void AddSharedGroups(DicomDataset shared, ExamPreset preset);

        protected virtual void AddPerFrameGroups(DicomDataset item, ImageContext context) { }

        private void AddDimensions(DicomDataset dataset, ImageContext context)
        {
            var organizationUid = context.Uids.Uid("dimension-organization/" + context.SeriesIndex.ToString(CultureInfo.InvariantCulture) + "/" + context.ImageIndex.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.DimensionOrganizationSequence, new DicomDataset
            {
                { DicomTag.DimensionOrganizationUID, organizationUid },
            }));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.DimensionIndexSequence, DimensionPointers.Select(pointer => new DicomDataset
            {
                { DicomTag.DimensionOrganizationUID, organizationUid },
                { DicomTag.DimensionIndexPointer, pointer[0] },
                { DicomTag.FunctionalGroupPointer, pointer[1] },
            }).ToArray()));
        }
    }

    /// <summary>
    /// What Enhanced CT, Enhanced MR and Breast Tomosynthesis have in common: a frame type and the anatomy of the frames.
    /// Their frames are drawn as they are written, so that the whole object is never in memory at once.
    /// </summary>
    public abstract class EnhancedImageProfile : FunctionalGroupProfile
    {
        public override bool RendersFramesOnDemand => true;
        public override int DefaultFrames => EnhancedFrames;

        protected abstract DicomTag FrameTypeSequence { get; }
        /// <summary>SNOMED CT code of the imaged region, with its meaning.</summary>
        protected abstract string[] Anatomy { get; }

        protected virtual void AddFrameTypeDetails(DicomDataset frameType) { }

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.AcquisitionDateTime, DicomImageBuilder.Dt(context.ContentTime));
            dataset.AddOrUpdate(DicomTag.ContentQualification, "RESEARCH");
            dataset.AddOrUpdate(DicomTag.PresentationLUTShape, "IDENTITY");
            dataset.AddOrUpdate(DicomTag.PixelPresentation, "MONOCHROME");
            dataset.AddOrUpdate(DicomTag.VolumetricProperties, "VOLUME");
            dataset.AddOrUpdate(DicomTag.VolumeBasedCalculationTechnique, "NONE");
            base.AddModalityTags(dataset, preset, context);
        }

        protected override void AddSharedGroups(DicomDataset shared, ExamPreset preset)
        {
            var frameType = new DicomDataset
            {
                { DicomTag.FrameType, ImageType(preset) },
                { DicomTag.PixelPresentation, "MONOCHROME" },
                { DicomTag.VolumetricProperties, "VOLUME" },
                { DicomTag.VolumeBasedCalculationTechnique, "NONE" },
            };
            AddFrameTypeDetails(frameType);
            shared.Add(new DicomSequence(FrameTypeSequence, frameType));

            shared.Add(new DicomSequence(DicomTag.FrameAnatomySequence, new DicomDataset
            {
                new DicomSequence(DicomTag.AnatomicRegionSequence, CodeItem("SCT", Anatomy[0], Anatomy[1])),
                new DicomCodeString(DicomTag.FrameLaterality, preset.ImageLaterality),
            }));
        }
    }

    /// <summary>Enhanced CT Image IOD.</summary>
    public sealed class EnhancedCtProfile : EnhancedImageProfile
    {
        public override string Code => "CT";
        public override string DisplayName => "Enhanced CT";
        public override string ObjectName => "Enhanced CT Image";
        public override DicomUID SopClass => DicomUID.EnhancedCTImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 16 };
        public override int DefaultBitDepth => 16;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);
        public override double? RescaleIntercept => -1024;
        public override string RescaleType => "HU";
        protected override DicomTag FrameTypeSequence => DicomTag.CTImageFrameTypeSequence;
        protected override string[] Anatomy { get; } = { "69536005", "Head" };

        /// <summary>The same Hounsfield range as the single-frame CT profile.</summary>
        public override int MaxStoredValue(int bitsStored) => Math.Min((1 << bitsStored) - 1, 4095);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Head", StudyDescription = "CT Head", SeriesDescription = "Head Enhanced", ProtocolName = "Head Enhanced", BodyPart = "HEAD", FieldOfViewMm = 230, SliceThicknessMm = 5 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "DERIVED", "PRIMARY", "VOLUME", "NONE" };
    }

    /// <summary>Enhanced MR Image IOD.</summary>
    public sealed class EnhancedMrProfile : EnhancedImageProfile
    {
        public override string Code => "MR";
        public override string DisplayName => "Enhanced MR";
        public override string ObjectName => "Enhanced MR Image";
        public override DicomUID SopClass => DicomUID.EnhancedMRImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 256;
        public override int DefaultColumns => 256;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);
        protected override DicomTag FrameTypeSequence => DicomTag.MRImageFrameTypeSequence;
        protected override string[] Anatomy { get; } = { "12738006", "Brain" };

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Brain", StudyDescription = "MR Brain", SeriesDescription = "Brain Enhanced", ProtocolName = "Brain Enhanced", BodyPart = "BRAIN", FieldOfViewMm = 230, SliceThicknessMm = 5 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "DERIVED", "PRIMARY", "VOLUME", "NONE" };

        protected override void AddFrameTypeDetails(DicomDataset frameType)
        {
            frameType.Add(DicomTag.ComplexImageComponent, "MAGNITUDE");
            frameType.Add(DicomTag.AcquisitionContrast, "UNKNOWN");
        }

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.ComplexImageComponent, "MAGNITUDE");
            dataset.AddOrUpdate(DicomTag.AcquisitionContrast, "UNKNOWN");
            base.AddModalityTags(dataset, preset, context);
        }
    }

    /// <summary>Breast Tomosynthesis Image IOD. A real object is hundreds of megabytes.</summary>
    public sealed class BreastTomosynthesisProfile : EnhancedImageProfile
    {
        /// <summary>The frames of the <c>tomosynthesis-large</c> dataset, which makes an object of about 700 MB.</summary>
        public const int LargeFrames = 60;

        public override string Code => "MG";
        public override string DisplayName => "Breast Tomosynthesis";
        public override string ObjectName => "Breast Tomosynthesis Image";
        public override DicomUID SopClass => DicomUID.BreastTomosynthesisImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 14, 16 };
        public override int DefaultBitDepth => 14;
        public override int DefaultRows => 2816;
        public override int DefaultColumns => 2048;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(30);
        public override double? RescaleIntercept => 0;
        public override string RescaleType => "US";
        protected override DicomTag FrameTypeSequence => DicomTag.XRay3DFrameTypeSequence;
        protected override string[] Anatomy { get; } = { "76752008", "Breast" };

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Left CC", StudyDescription = "Breast Tomosynthesis", SeriesDescription = "L CC Tomo", ProtocolName = "Tomosynthesis", BodyPart = "BREAST", FieldOfViewMm = 240, SliceThicknessMm = 1, ImageLaterality = "L" },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "DERIVED", "PRIMARY", "TOMOSYNTHESIS", "NONE" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.ImageLaterality, preset.ImageLaterality);
            dataset.AddOrUpdate(DicomTag.PresentationIntentType, "FOR PRESENTATION");
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AcquisitionContextSequence));
            base.AddModalityTags(dataset, preset, context);
        }
    }

    /// <summary>Segmentation IOD: one binary segment, with every frame derived from the source image. Rows and Columns must be multiples of 8.</summary>
    public sealed class SegmentationProfile : FunctionalGroupProfile
    {
        public const int SegmentationFrames = 4;
        private const ushort SegmentNumber = 1;

        public override int DefaultFrames => SegmentationFrames;

        public override string Code => "SEG";
        public override string DisplayName => "Segmentation";
        public override DicomUID SopClass => DicomUID.SegmentationStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 1 };
        public override int DefaultBitDepth => 1;
        public override int DefaultRows => 256;
        public override int DefaultColumns => 256;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);
        public override bool IsBinaryMask => true;

        /// <summary>The picture is drawn with full 8-bit levels first, and thresholded when it is packed to one bit.</summary>
        public override int MaxStoredValue(int bitsStored) => byte.MaxValue;

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Head", StudyDescription = "CT Head", SeriesDescription = "Segmentation", ProtocolName = "Segmentation", BodyPart = "HEAD", FieldOfViewMm = 230, SliceThicknessMm = 5 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "DERIVED", "PRIMARY" };

        protected override DicomTag[][] DimensionPointers { get; } =
        {
            new[] { DicomTag.ReferencedSegmentNumber, DicomTag.SegmentIdentificationSequence },
            new[] { DicomTag.InStackPositionNumber, DicomTag.FrameContentSequence },
        };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.SegmentationType, "BINARY");
            dataset.AddOrUpdate(DicomTag.ContentLabel, "DCST4_TEST");
            dataset.AddOrUpdate(DicomTag.ContentDescription, "DCST4 test segmentation");
            dataset.AddOrUpdate(DicomTag.ContentCreatorName, "DCST4");

            dataset.AddOrUpdate(new DicomSequence(DicomTag.SegmentSequence, new DicomDataset
            {
                { DicomTag.SegmentNumber, SegmentNumber },
                { DicomTag.SegmentLabel, "TEST" },
                { DicomTag.SegmentAlgorithmType, "AUTOMATIC" },
                { DicomTag.SegmentAlgorithmName, "DCST4 threshold" },
                new DicomSequence(DicomTag.SegmentedPropertyCategoryCodeSequence, CodeItem("SCT", "91723000", "Anatomical Structure")),
                new DicomSequence(DicomTag.SegmentedPropertyTypeCodeSequence, CodeItem("SCT", "69536005", "Head")),
            }));

            var source = context.SourceImage;
            if (source != null)
            {
                if (source.FrameOfReferenceUid != null) dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, source.FrameOfReferenceUid);
                dataset.AddOrUpdate(new DicomSequence(DicomTag.ReferencedSeriesSequence, new DicomDataset
                {
                    { DicomTag.SeriesInstanceUID, source.SeriesInstanceUid },
                    new DicomSequence(DicomTag.ReferencedInstanceSequence, new DicomDataset
                    {
                        { DicomTag.ReferencedSOPClassUID, source.SopClassUid },
                        { DicomTag.ReferencedSOPInstanceUID, source.SopInstanceUid },
                    }),
                }));
            }

            base.AddModalityTags(dataset, preset, context);
        }

        protected override void AddSharedGroups(DicomDataset shared, ExamPreset preset) { }

        protected override void AddPerFrameGroups(DicomDataset item, ImageContext context)
        {
            item.Add(new DicomSequence(DicomTag.SegmentIdentificationSequence, new DicomDataset { { DicomTag.ReferencedSegmentNumber, SegmentNumber } }));

            var source = context.SourceImage;
            if (source == null) return;
            item.Add(new DicomSequence(DicomTag.DerivationImageSequence, new DicomDataset
            {
                new DicomSequence(DicomTag.DerivationCodeSequence, CodeItem("DCM", "113076", "Segmentation")),
                new DicomSequence(DicomTag.SourceImageSequence, new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, source.SopClassUid },
                    { DicomTag.ReferencedSOPInstanceUID, source.SopInstanceUid },
                    new DicomSequence(DicomTag.PurposeOfReferenceCodeSequence, CodeItem("DCM", "121322", "Source image for image processing operation")),
                }),
            }));
        }
    }
}
