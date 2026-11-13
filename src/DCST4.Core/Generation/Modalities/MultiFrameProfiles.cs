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
        public override string ObjectName => "Multi-frame (cine loop)";
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

    /// <summary>
    /// The Cine and Multi-frame modules, which the XA and XRF IODs require as soon as an object has more than one frame.
    /// Both are conditional modules of the same IOD, so a cine run is the single-frame SOP class with these tags added.
    /// </summary>
    public static class CineModule
    {
        /// <summary>Cine Rate and Recommended Display Frame Rate are IS, so the rate is a whole number of frames per second.</summary>
        public static void Add(DicomDataset dataset, int framesPerSecond)
        {
            var rate = framesPerSecond.ToString(CultureInfo.InvariantCulture);
            dataset.AddOrUpdate(DicomTag.FrameTime, ModalityProfile.Ds(1000.0 / framesPerSecond));
            dataset.AddOrUpdate(DicomTag.FrameIncrementPointer, DicomTag.FrameTime);
            dataset.AddOrUpdate(DicomTag.CineRate, rate);
            dataset.AddOrUpdate(DicomTag.RecommendedDisplayFrameRate, rate);
            // 0 is looping, which is how a cine run is shown.
            dataset.AddOrUpdate(DicomTag.PreferredPlaybackSequencing, (ushort)0);
        }
    }

    /// <summary>
    /// X-Ray Angiographic Image IOD with the Multi-frame and Cine modules: a cine run from a cath lab, in the same SOP class
    /// as the single-frame XA image. The frames are drawn as they are written, so a long run is never in memory at once.
    /// </summary>
    public sealed class XaCineProfile : XaProfile
    {
        /// <summary>Two seconds of a cine run.</summary>
        public const int CineFrames = 30;
        private const int FramesPerSecond = 15;

        public override string DisplayName => "XA Multi-frame";
        public override string ObjectName => "Multi-frame (cine run)";
        public override bool IsMultiFrame => true;
        public override bool RendersFramesOnDemand => true;
        public override int DefaultFrames => CineFrames;
        /// <summary>One object is a whole run, not one frame of it.</summary>
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(10);

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            base.AddModalityTags(dataset, preset, context);
            CineModule.Add(dataset, FramesPerSecond);
            // Type 2C in the XA Positioner module as soon as the object has more than one frame. The C-arm stands still here.
            dataset.AddOrUpdate(DicomTag.PositionerMotion, "STATIC");
        }
    }

    /// <summary>X-Ray Radiofluoroscopic Image IOD with the Multi-frame and Cine modules: a fluoroscopy loop.</summary>
    public sealed class RfCineProfile : RfProfile
    {
        /// <summary>Two seconds of fluoroscopy.</summary>
        public const int LoopFrames = 16;
        private const int FramesPerSecond = 8;

        public override string DisplayName => "RF Multi-frame";
        public override string ObjectName => "Multi-frame (fluoro loop)";
        public override bool IsMultiFrame => true;
        public override bool RendersFramesOnDemand => true;
        public override int DefaultFrames => LoopFrames;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(10);

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            base.AddModalityTags(dataset, preset, context);
            CineModule.Add(dataset, FramesPerSecond);
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

        /// <summary>
        /// The frames lie in a stack in the patient coordinate system: Pixel Measures, Plane Orientation and Plane Position.
        /// False for an IOD whose functional group macros do not include them, such as Enhanced XA.
        /// </summary>
        protected virtual bool UsesSliceGeometry => true;

        /// <summary>
        /// The object has a Dimension Organization, and each frame its index in it. False for an IOD where the
        /// Multi-frame Dimension module is not used, which also leaves Stack ID out of Frame Content.
        /// </summary>
        protected virtual bool UsesDimensionOrganization => true;

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            if (UsesSliceGeometry) dataset.AddOrUpdate(DicomTag.PositionReferenceIndicator, string.Empty);
            if (UsesDimensionOrganization) AddDimensions(dataset, context);

            var spacing = preset.FieldOfViewMm / Math.Max(context.Rows, context.Columns);
            var shared = new DicomDataset();
            if (UsesSliceGeometry)
            {
                shared.Add(new DicomSequence(DicomTag.PixelMeasuresSequence, new DicomDataset
                {
                    { DicomTag.PixelSpacing, Ds(spacing), Ds(spacing) },
                    { DicomTag.SliceThickness, Ds(preset.SliceThicknessMm) },
                    { DicomTag.SpacingBetweenSlices, Ds(preset.SliceThicknessMm) },
                }));
                shared.Add(new DicomSequence(DicomTag.PlaneOrientationSequence, new DicomDataset
                {
                    { DicomTag.ImageOrientationPatient, "1", "0", "0", "0", "1", "0" },
                }));
            }
            AddSharedGroups(shared, preset);
            dataset.AddOrUpdate(new DicomSequence(DicomTag.SharedFunctionalGroupsSequence, shared));

            var perFrame = new DicomDataset[context.Frames];
            for (var frame = 0; frame < context.Frames; frame++)
            {
                var offset = (frame - (context.Frames - 1) / 2.0) * preset.SliceThicknessMm;
                var frameContent = new DicomDataset();
                if (UsesDimensionOrganization)
                {
                    frameContent.Add(DicomTag.DimensionIndexValues, DimensionIndexValues(frame));
                    frameContent.Add(DicomTag.StackID, StackId);
                    frameContent.Add(DicomTag.InStackPositionNumber, (uint)(frame + 1));
                }
                AddFrameContent(frameContent, frame);
                var item = new DicomDataset { new DicomSequence(DicomTag.FrameContentSequence, frameContent) };
                if (UsesSliceGeometry)
                {
                    item.Add(new DicomSequence(DicomTag.PlanePositionSequence, new DicomDataset
                    {
                        { DicomTag.ImagePositionPatient, Ds(-context.Columns * spacing / 2), Ds(-context.Rows * spacing / 2), Ds(offset) },
                    }));
                }
                AddPerFrameGroups(item, context);
                perFrame[frame] = item;
            }
            dataset.AddOrUpdate(new DicomSequence(DicomTag.PerFrameFunctionalGroupsSequence, perFrame));
        }

        /// <summary>More of the Frame Content item of one frame, for an IOD that has no dimension index to put there.</summary>
        protected virtual void AddFrameContent(DicomDataset frameContent, int frame) { }

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

    /// <summary>
    /// What the Enhanced XA and Enhanced XRF Image IODs have in common (PS3.3 A.47 and A.48). Both are built so that every
    /// conditional module and macro of those IODs falls away: Image Type value 1 is DERIVED, so XA/XRF Acquisition,
    /// X-Ray Collimator and the positioner macros are not required; C-arm Positioner Tabletop Relationship is absent, so
    /// Frame of Reference, Synchronization, Patient Orientation in Frame and the table and geometry macros are not required;
    /// X-Ray Receptor Type is absent, so neither detector module is; Pixel Intensity Relationship is LIN, not LOG, so the
    /// Pixel Intensity Relationship LUT macro is not required; and there is no contrast or cardiac synchronization. What is
    /// left mandatory is the XA/XRF Series and Enhanced XA/XRF Image modules, Acquisition Context, the functional groups, and
    /// the Frame Content, Frame Anatomy, Frame VOI LUT, Irradiation Event Identification and X-Ray Frame Pixel Data
    /// Properties macros. Frame VOI LUT is written by <see cref="DicomImageBuilder"/>, which puts it in the shared groups.
    /// </summary>
    public abstract class EnhancedXrayProfile : FunctionalGroupProfile
    {
        /// <summary>Only the combinations of Table C.8.19.2-2: 8 bits allocated with 8 stored, or 16 allocated with 9 to 16.</summary>
        private static readonly int[] XrayBitDepths = { 8, 12, 16 };

        public override IReadOnlyList<int> BitDepths => XrayBitDepths;
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 1024;
        public override int DefaultColumns => 1024;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(10);
        public override int DefaultFrames => EnhancedFrames;
        public override bool RendersFramesOnDemand => true;

        // The frames are not a stack in the patient coordinate system, and the Multi-frame Dimension module is not used.
        public override bool UsesFrameOfReference => false;
        protected override bool UsesSliceGeometry => false;
        protected override bool UsesDimensionOrganization => false;

        /// <summary>Image Type value 3, the image flavour of this IOD.</summary>
        protected abstract string Flavour { get; }
        /// <summary>SNOMED CT code of the imaged region, with its meaning. From CID 4042, as both IODs require.</summary>
        protected abstract string[] Anatomy { get; }

        public override string[] ImageType(ExamPreset preset) => new[] { "DERIVED", "PRIMARY", Flavour, "NONE" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.AcquisitionDateTime, DicomImageBuilder.Dt(context.ContentTime));
            dataset.AddOrUpdate(DicomTag.ContentQualification, "RESEARCH");
            // One plane, as a cath lab with a single C-arm sends. Plane Identification is then Type 1C and must be present.
            dataset.AddOrUpdate(DicomTag.PlanesInAcquisition, "SINGLE PLANE");
            dataset.AddOrUpdate(DicomTag.PlaneIdentification, "MONOPLANE");
            dataset.AddOrUpdate(DicomTag.PresentationLUTShape, "IDENTITY");
            dataset.AddOrUpdate(DicomTag.LossyImageCompression, "00");
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AcquisitionContextSequence));
            base.AddModalityTags(dataset, preset, context);

            // The event UID has to follow from the UID root, which only the context knows, and the shared groups exist by now.
            var eventUid = context.Uids.Uid("irradiation-event/" + context.SeriesIndex.ToString(CultureInfo.InvariantCulture) + "/" + context.ImageIndex.ToString(CultureInfo.InvariantCulture));
            dataset.GetSequence(DicomTag.SharedFunctionalGroupsSequence).Items[0]
                .AddOrUpdate(new DicomSequence(DicomTag.IrradiationEventIdentificationSequence, new DicomDataset
                {
                    { DicomTag.IrradiationEventUID, eventUid },
                }));
        }

        protected override void AddSharedGroups(DicomDataset shared, ExamPreset preset)
        {
            shared.Add(new DicomSequence(DicomTag.FramePixelDataPropertiesSequence, new DicomDataset
            {
                { DicomTag.FrameType, ImageType(preset) },
                { DicomTag.PixelIntensityRelationship, "LIN" },
                { DicomTag.PixelIntensityRelationshipSign, (short)1 },
                { DicomTag.GeometricalProperties, "UNIFORM" },
                { DicomTag.ImageProcessingApplied, "NONE" },
            }));

            shared.Add(new DicomSequence(DicomTag.FrameAnatomySequence, new DicomDataset
            {
                new DicomSequence(DicomTag.AnatomicRegionSequence, CodeItem("SCT", Anatomy[0], Anatomy[1])),
                new DicomCodeString(DicomTag.FrameLaterality, "U"),
            }));
        }

        /// <summary>A DERIVED frame needs none of the Frame Content attributes, so it gets the one that says which frame it is.</summary>
        protected override void AddFrameContent(DicomDataset frameContent, int frame) =>
            frameContent.Add(DicomTag.FrameLabel, "Frame " + (frame + 1).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Enhanced XA Image IOD: a cine run as a multi-frame functional group object.</summary>
    public sealed class EnhancedXaProfile : EnhancedXrayProfile
    {
        public override string Code => "XA";
        public override string DisplayName => "Enhanced XA";
        public override DicomUID SopClass => DicomUID.EnhancedXAImageStorage;
        protected override string Flavour => "ANGIO";
        /// <summary>Coronary artery, from CID 3010, which CID 4042 includes.</summary>
        protected override string[] Anatomy { get; } = { "41801008", "Coronary artery" };

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Coronary", StudyDescription = "XA Coronary", SeriesDescription = "Coronary Enhanced", ProtocolName = "Coronary", BodyPart = "HEART", FieldOfViewMm = 200, RadiationSetting = "GR", Kvp = 80, TubeCurrentMa = 600, ExposureTimeMs = 7 },
        };
    }

    /// <summary>Enhanced XRF Image IOD: the RF modality's enhanced object, a fluoroscopy loop.</summary>
    public sealed class EnhancedRfProfile : EnhancedXrayProfile
    {
        public override string Code => "RF";
        public override string DisplayName => "Enhanced XRF";
        public override DicomUID SopClass => DicomUID.EnhancedXRFImageStorage;
        protected override string Flavour => "FLUOROSCOPY";
        /// <summary>Stomach, from CID 4031, which CID 4042 includes.</summary>
        protected override string[] Anatomy { get; } = { "69695003", "Stomach" };

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Upper GI", StudyDescription = "RF Upper GI", SeriesDescription = "Upper GI Enhanced", ProtocolName = "Upper GI", BodyPart = "STOMACH", FieldOfViewMm = 400, RadiationSetting = "SC", Kvp = 90, TubeCurrentMa = 400, ExposureTimeMs = 100 },
        };
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
