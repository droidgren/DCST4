using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Generation.Modalities
{
    public static class ModalityCatalog
    {
        public static IReadOnlyList<ModalityProfile> All { get; } = new ModalityProfile[]
        {
            new CtProfile(), new MrProfile(), new CrProfile(), new DxProfile(), new UsProfile(),
        };

        public static ModalityProfile Find(string code) =>
            All.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>CT Image IOD. Requires 16 bits allocated and 12-16 bits stored.</summary>
    public sealed class CtProfile : ModalityProfile
    {
        public override string Code => "CT";
        public override string DisplayName => "CT";
        public override DicomUID SopClass => DicomUID.CTImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 14, 16 };
        public override int DefaultBitDepth => 16;
        public override TimeSpan InstanceInterval => TimeSpan.FromMilliseconds(100);
        public override double? RescaleIntercept => -1024;
        public override string RescaleType => "HU";

        /// <summary>Real scanners store Hounsfield values, so the range stays at -1024..3071 HU whatever Bits Stored is.</summary>
        public override int MaxStoredValue(int bitsStored) => Math.Min((1 << bitsStored) - 1, 4095);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Head", StudyDescription = "CT Head", SeriesDescription = "Head 5.0 H31", ProtocolName = "Head Routine", BodyPart = "HEAD", FieldOfViewMm = 230, SliceThicknessMm = 5, DefaultImages = 30, Kvp = 120, TubeCurrentMa = 280, ExposureTimeMs = 1000, ConvolutionKernel = "H31s" },
            new ExamPreset { Name = "Thorax", StudyDescription = "CT Thorax", SeriesDescription = "Thorax 1.0 B70", ProtocolName = "Thorax Routine", BodyPart = "CHEST", FieldOfViewMm = 350, SliceThicknessMm = 1, DefaultImages = 120, Kvp = 110, TubeCurrentMa = 90, ExposureTimeMs = 500, ConvolutionKernel = "B70f" },
            new ExamPreset { Name = "Abdomen", StudyDescription = "CT Abdomen", SeriesDescription = "Abdomen 3.0 B31", ProtocolName = "Abdomen Routine", BodyPart = "ABDOMEN", FieldOfViewMm = 380, SliceThicknessMm = 3, DefaultImages = 80, Kvp = 120, TubeCurrentMa = 180, ExposureTimeMs = 500, ConvolutionKernel = "B31f" },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY", "AXIAL" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddSliceGeometry(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.KVP, Ds(preset.Kvp));
            dataset.AddOrUpdate(DicomTag.XRayTubeCurrent, preset.TubeCurrentMa.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.ExposureTime, preset.ExposureTimeMs.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.ConvolutionKernel, preset.ConvolutionKernel);
            dataset.AddOrUpdate(DicomTag.GantryDetectorTilt, "0");
            dataset.AddOrUpdate(DicomTag.ReconstructionDiameter, Ds(preset.FieldOfViewMm));
            dataset.AddOrUpdate(DicomTag.DataCollectionDiameter, "500");
            dataset.AddOrUpdate(DicomTag.ScanOptions, "HELICAL MODE");
        }
    }

    /// <summary>MR Image IOD. Requires 16 bits allocated.</summary>
    public sealed class MrProfile : ModalityProfile
    {
        public override string Code => "MR";
        public override string DisplayName => "MR";
        public override DicomUID SopClass => DicomUID.MRImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 14, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 256;
        public override int DefaultColumns => 256;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Brain T1", StudyDescription = "MR Brain", SeriesDescription = "t1_se_tra", ProtocolName = "Brain T1 SE", BodyPart = "BRAIN", FieldOfViewMm = 230, SliceThicknessMm = 5, DefaultImages = 24, ScanningSequence = "SE", SequenceVariant = "SK", ScanOptions = "SAT1", MrAcquisitionType = "2D", RepetitionTimeMs = 550, EchoTimeMs = 12, EchoTrainLength = 1, FlipAngle = 90 },
            new ExamPreset { Name = "Brain T2", StudyDescription = "MR Brain", SeriesDescription = "t2_tse_tra", ProtocolName = "Brain T2 TSE", BodyPart = "BRAIN", FieldOfViewMm = 230, SliceThicknessMm = 5, DefaultImages = 24, ScanningSequence = "SE", SequenceVariant = "SK\\SP", ScanOptions = "PFP", MrAcquisitionType = "2D", RepetitionTimeMs = 4500, EchoTimeMs = 95, EchoTrainLength = 15, FlipAngle = 150 },
            new ExamPreset { Name = "Knee", StudyDescription = "MR Knee", SeriesDescription = "pd_tse_fs_sag", ProtocolName = "Knee PD FS", BodyPart = "KNEE", PatientPosition = "FFS", FieldOfViewMm = 160, SliceThicknessMm = 3, Orientation = SliceOrientation.Sagittal, DefaultImages = 28, ScanningSequence = "SE", SequenceVariant = "SK\\SP", ScanOptions = "FS", MrAcquisitionType = "2D", RepetitionTimeMs = 3200, EchoTimeMs = 35, EchoTrainLength = 7, FlipAngle = 150 },
            new ExamPreset { Name = "Spine", StudyDescription = "MR Lumbar Spine", SeriesDescription = "t2_tse_sag", ProtocolName = "L-Spine T2", BodyPart = "LSPINE", FieldOfViewMm = 300, SliceThicknessMm = 4, Orientation = SliceOrientation.Sagittal, DefaultImages = 15, ScanningSequence = "SE", SequenceVariant = "SK\\SP", ScanOptions = "PFP", MrAcquisitionType = "2D", RepetitionTimeMs = 3500, EchoTimeMs = 100, EchoTrainLength = 17, FlipAngle = 160 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY", "M", "ND" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddSliceGeometry(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.ScanningSequence, preset.ScanningSequence.Split('\\'));
            dataset.AddOrUpdate(DicomTag.SequenceVariant, preset.SequenceVariant.Split('\\'));
            dataset.AddOrUpdate(DicomTag.ScanOptions, preset.ScanOptions.Split('\\'));
            dataset.AddOrUpdate(DicomTag.MRAcquisitionType, preset.MrAcquisitionType);
            dataset.AddOrUpdate(DicomTag.RepetitionTime, Ds(preset.RepetitionTimeMs));
            dataset.AddOrUpdate(DicomTag.EchoTime, Ds(preset.EchoTimeMs));
            dataset.AddOrUpdate(DicomTag.EchoTrainLength, preset.EchoTrainLength.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.FlipAngle, Ds(preset.FlipAngle));
            dataset.AddOrUpdate(DicomTag.MagneticFieldStrength, Ds(preset.MagneticFieldStrengthT));
            dataset.AddOrUpdate(DicomTag.ImagingFrequency, Ds(42.577 * preset.MagneticFieldStrengthT));
            dataset.AddOrUpdate(DicomTag.ImagedNucleus, "1H");
            dataset.AddOrUpdate(DicomTag.NumberOfAverages, "1");
        }
    }

    /// <summary>Computed Radiography Image IOD.</summary>
    public sealed class CrProfile : ModalityProfile
    {
        public override string Code => "CR";
        public override string DisplayName => "CR";
        public override DicomUID SopClass => DicomUID.ComputedRadiographyImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8, 12, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 1024;
        public override int DefaultColumns => 1024;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(30);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Chest PA", StudyDescription = "Chest X-ray", SeriesDescription = "Chest PA", ProtocolName = "Chest PA", BodyPart = "CHEST", FieldOfViewMm = 430, Kvp = 125, TubeCurrentMa = 250, ExposureTimeMs = 10, ViewPosition = "PA", PatientOrientation = "L\\F" },
            new ExamPreset { Name = "Hand", StudyDescription = "Hand X-ray", SeriesDescription = "Hand PA", ProtocolName = "Hand PA", BodyPart = "HAND", FieldOfViewMm = 240, Kvp = 50, TubeCurrentMa = 100, ExposureTimeMs = 40, ViewPosition = "PA", PatientOrientation = "L\\F" },
        };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddProjectionTags(dataset, preset, context);
        }
    }

    /// <summary>
    /// Ultrasound Image IOD, single-frame grayscale B-mode: MONOCHROME2 with 8 bits allocated and stored. Distances in a viewer
    /// come from the calibrated region in Sequence of Ultrasound Regions, since US images have no Pixel Spacing.
    /// </summary>
    public sealed class UsProfile : ModalityProfile
    {
        private const ushort RegionSpatialFormat2D = 1;
        private const ushort RegionDataTypeTissue = 1;
        private const ushort PhysicalUnitsCentimeters = 3;

        public override string Code => "US";
        public override string DisplayName => "US";
        public override DicomUID SopClass => DicomUID.UltrasoundImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8 };
        public override int DefaultBitDepth => 8;
        public override int DefaultRows => 600;
        public override int DefaultColumns => 800;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(15);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Abdomen", StudyDescription = "US Abdomen", SeriesDescription = "Abdomen", ProtocolName = "Abdomen", BodyPart = "ABDOMEN", UsApplication = "ABDOMINAL", TransducerData = "C5-1", TransducerType = "CURVED LINEAR", ScanDepthCm = 16, DefaultImages = 8 },
            new ExamPreset { Name = "Thyroid", StudyDescription = "US Thyroid", SeriesDescription = "Thyroid", ProtocolName = "Thyroid", BodyPart = "THYROID", UsApplication = "THYROID", TransducerData = "L12-5", TransducerType = "LINEAR", ScanDepthCm = 4, DefaultImages = 6 },
            new ExamPreset { Name = "Obstetric", StudyDescription = "US Obstetric", SeriesDescription = "Obstetric", ProtocolName = "OB", BodyPart = "UTERUS", UsApplication = "OB", TransducerData = "C5-1", TransducerType = "CURVED LINEAR", ScanDepthCm = 14, DefaultImages = 10 },
        };

        /// <summary>Value 4 is 0001: 2D imaging only.</summary>
        public override string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY", preset.UsApplication, "0001" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            dataset.AddOrUpdate(DicomTag.PatientOrientation, new string[0]);
            dataset.AddOrUpdate(DicomTag.TransducerData, preset.TransducerData);
            dataset.AddOrUpdate(DicomTag.TransducerType, preset.TransducerType);
            dataset.AddOrUpdate(DicomTag.ProcessingFunction, "B-MODE");
            dataset.AddOrUpdate(DicomTag.DepthOfScanField, ((int)(preset.ScanDepthCm * 10)).ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.MechanicalIndex, "1.1");
            dataset.AddOrUpdate(DicomTag.SoftTissueThermalIndex, "0.4");

            var centimetersPerPixel = preset.ScanDepthCm / context.Rows;
            dataset.AddOrUpdate(new DicomSequence(DicomTag.SequenceOfUltrasoundRegions, new DicomDataset
            {
                { DicomTag.RegionSpatialFormat, RegionSpatialFormat2D },
                { DicomTag.RegionDataType, RegionDataTypeTissue },
                { DicomTag.RegionFlags, 0u },
                { DicomTag.RegionLocationMinX0, 0u },
                { DicomTag.RegionLocationMinY0, 0u },
                { DicomTag.RegionLocationMaxX1, (uint)(context.Columns - 1) },
                { DicomTag.RegionLocationMaxY1, (uint)(context.Rows - 1) },
                { DicomTag.PhysicalUnitsXDirection, PhysicalUnitsCentimeters },
                { DicomTag.PhysicalUnitsYDirection, PhysicalUnitsCentimeters },
                { DicomTag.PhysicalDeltaX, centimetersPerPixel },
                { DicomTag.PhysicalDeltaY, centimetersPerPixel },
            }));
        }
    }

    /// <summary>Digital X-Ray Image IOD, For Presentation.</summary>
    public sealed class DxProfile : ModalityProfile
    {
        public override string Code => "DX";
        public override string DisplayName => "DX";
        public override DicomUID SopClass => DicomUID.DigitalXRayImageStorageForPresentation;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8, 12, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 1024;
        public override int DefaultColumns => 1024;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(30);
        public override double? RescaleIntercept => 0;
        public override string RescaleType => "US";

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Chest PA", StudyDescription = "Chest X-ray", SeriesDescription = "Chest PA", ProtocolName = "Chest PA", BodyPart = "CHEST", FieldOfViewMm = 430, Kvp = 125, TubeCurrentMa = 250, ExposureTimeMs = 10, ViewPosition = "PA", PatientOrientation = "L\\F" },
            new ExamPreset { Name = "Pelvis", StudyDescription = "Pelvis X-ray", SeriesDescription = "Pelvis AP", ProtocolName = "Pelvis AP", BodyPart = "PELVIS", FieldOfViewMm = 430, Kvp = 80, TubeCurrentMa = 320, ExposureTimeMs = 80, ViewPosition = "AP", PatientOrientation = "L\\F" },
        };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddProjectionTags(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.PresentationIntentType, "FOR PRESENTATION");
            dataset.AddOrUpdate(DicomTag.PresentationLUTShape, "IDENTITY");
            dataset.AddOrUpdate(DicomTag.PixelIntensityRelationship, "LIN");
            dataset.AddOrUpdate(DicomTag.PixelIntensityRelationshipSign, (short)1);
            dataset.AddOrUpdate(DicomTag.DetectorType, "DIRECT");
            dataset.AddOrUpdate(DicomTag.ImageLaterality, preset.ImageLaterality);
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AnatomicRegionSequence));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AcquisitionContextSequence));
        }
    }
}
