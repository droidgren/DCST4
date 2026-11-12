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
            new XaProfile(), new RfProfile(), new MgProfile(), new PtProfile(), new NmProfile(),
        };

        public static ModalityProfile Find(string code) =>
            All.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The images a modality can generate in Generate &amp; Send: its ordinary one first, and second the multi-frame
        /// image where DCST4 has one. Empty for a modality that is not in <see cref="All"/>.
        /// </summary>
        public static IReadOnlyList<ImageObject> ObjectsOf(ModalityProfile modality) =>
            modality != null && Objects.TryGetValue(modality.Code, out var objects) ? objects : NoObjects;

        /// <summary>The image of this modality with that dataset profile id. Null if the modality does not generate it.</summary>
        public static ImageObject FindObject(ModalityProfile modality, string datasetProfileId) =>
            ObjectsOf(modality).FirstOrDefault(o => string.Equals(o.DatasetProfileId, datasetProfileId?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The image with that dataset profile id, whatever its modality. Null if no modality generates it.</summary>
        public static ImageObject FindObject(string datasetProfileId) =>
            All.SelectMany(ObjectsOf).FirstOrDefault(o => string.Equals(o.DatasetProfileId, datasetProfileId?.Trim(), StringComparison.OrdinalIgnoreCase));

        private static readonly IReadOnlyList<ImageObject> NoObjects = new ImageObject[0];

        private static readonly Dictionary<string, IReadOnlyList<ImageObject>> Objects = BuildObjects();

        private static Dictionary<string, IReadOnlyList<ImageObject>> BuildObjects()
        {
            // The multi-frame image a modality can also send, by modality code, with the dataset profile that makes it.
            var multiFrame = new Dictionary<string, ImageObject>(StringComparer.OrdinalIgnoreCase)
            {
                { "US", new ImageObject(new UsMultiFrameProfile(), "us-multiframe") },
                { "CT", new ImageObject(new EnhancedCtProfile(), "enhanced-ct") },
                { "MR", new ImageObject(new EnhancedMrProfile(), "enhanced-mr") },
                { "MG", new ImageObject(new BreastTomosynthesisProfile(), "tomosynthesis-large") },
            };

            var objects = new Dictionary<string, IReadOnlyList<ImageObject>>(StringComparer.OrdinalIgnoreCase);
            foreach (var modality in All)
            {
                var list = new List<ImageObject> { new ImageObject(modality, modality.Code.ToLowerInvariant()) };
                if (multiFrame.TryGetValue(modality.Code, out var more)) list.Add(more);
                objects[modality.Code] = list;
            }
            return objects;
        }
    }

    /// <summary>One entry of the Object list in Generate &amp; Send: an image a modality can generate, named after its IOD.</summary>
    public sealed class ImageObject
    {
        internal ImageObject(ModalityProfile image, string datasetProfileId)
        {
            Image = image;
            DatasetProfileId = datasetProfileId;
        }

        /// <summary>The profile that generates it.</summary>
        public ModalityProfile Image { get; }
        /// <summary>The dataset profile that makes the same object, which a Performance mix generates it by.</summary>
        public string DatasetProfileId { get; }
        public string Name => Image.ObjectName;
        public bool IsMultiFrame => Image.IsMultiFrame;
        public override string ToString() => Name;
    }

    /// <summary>CT Image IOD. Requires 16 bits allocated and 12-16 bits stored.</summary>
    public sealed class CtProfile : ModalityProfile
    {
        public override string Code => "CT";
        public override string DisplayName => "CT";
        public override string ObjectName => "CT Image";
        public override DicomUID SopClass => DicomUID.CTImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 14, 16 };
        public override int DefaultBitDepth => 16;
        public override TimeSpan InstanceInterval => TimeSpan.FromMilliseconds(100);
        public override double? RescaleIntercept => -1024;
        public override string RescaleType => "HU";
        public override bool UsesFrameOfReference => true;

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
        public override string ObjectName => "MR Image";
        public override DicomUID SopClass => DicomUID.MRImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 14, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 256;
        public override int DefaultColumns => 256;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);
        public override bool UsesFrameOfReference => true;

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Brain T1", StudyDescription = "MR Brain", SeriesDescription = "t1_se_tra", ProtocolName = "Brain T1 SE", BodyPart = "BRAIN", FieldOfViewMm = 230, SliceThicknessMm = 5, DefaultImages = 24, ScanningSequence = "SE", SequenceVariant = "SK", ScanOptions = "SAT1", MrAcquisitionType = "2D", RepetitionTimeMs = 550, EchoTimeMs = 12, EchoTrainLength = 1, FlipAngle = 90 },
            new ExamPreset { Name = "Brain T2", StudyDescription = "MR Brain", SeriesDescription = "t2_tse_tra", ProtocolName = "Brain T2 TSE", BodyPart = "BRAIN", FieldOfViewMm = 230, SliceThicknessMm = 5, DefaultImages = 24, ScanningSequence = "SE", SequenceVariant = "SK\\SP", ScanOptions = "PFP", MrAcquisitionType = "2D", RepetitionTimeMs = 4500, EchoTimeMs = 95, EchoTrainLength = 15, FlipAngle = 150 },
            new ExamPreset { Name = "Knee", Laterality = "R", StudyDescription = "MR Knee", SeriesDescription = "pd_tse_fs_sag", ProtocolName = "Knee PD FS", BodyPart = "KNEE", PatientPosition = "FFS", FieldOfViewMm = 160, SliceThicknessMm = 3, Orientation = SliceOrientation.Sagittal, DefaultImages = 28, ScanningSequence = "SE", SequenceVariant = "SK\\SP", ScanOptions = "FS", MrAcquisitionType = "2D", RepetitionTimeMs = 3200, EchoTimeMs = 35, EchoTrainLength = 7, FlipAngle = 150 },
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
        public override string ObjectName => "Computed Radiography Image";
        public override DicomUID SopClass => DicomUID.ComputedRadiographyImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8, 12, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 1024;
        public override int DefaultColumns => 1024;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(30);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Chest PA", StudyDescription = "Chest X-ray", SeriesDescription = "Chest PA", ProtocolName = "Chest PA", BodyPart = "CHEST", FieldOfViewMm = 430, Kvp = 125, TubeCurrentMa = 250, ExposureTimeMs = 10, ViewPosition = "PA", PatientOrientation = "L\\F" },
            new ExamPreset { Name = "Hand", Laterality = "R", StudyDescription = "Hand X-ray", SeriesDescription = "Hand PA", ProtocolName = "Hand PA", BodyPart = "HAND", FieldOfViewMm = 240, Kvp = 50, TubeCurrentMa = 100, ExposureTimeMs = 40, ViewPosition = "PA", PatientOrientation = "L\\F" },
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
    public class UsProfile : ModalityProfile
    {
        private const ushort RegionSpatialFormat2D = 1;
        private const ushort RegionDataTypeTissue = 1;
        private const ushort PhysicalUnitsCentimeters = 3;

        public override string Code => "US";
        public override string DisplayName => "US";
        public override string ObjectName => "Ultrasound Image";
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
        public override string ObjectName => "Digital X-Ray Image";
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
            AddDigitalXRayTags(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.PositionerType, "COLUMN");
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AnatomicRegionSequence));
        }
    }

    /// <summary>X-Ray Angiographic Image IOD, single frame. Bits Stored in the X-Ray Image module is 8, 10, 12 or 16.</summary>
    public sealed class XaProfile : ModalityProfile
    {
        public override string Code => "XA";
        public override string DisplayName => "XA";
        public override string ObjectName => "X-Ray Angiographic Image";
        public override DicomUID SopClass => DicomUID.XRayAngiographicImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8, 12, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 1024;
        public override int DefaultColumns => 1024;
        public override TimeSpan InstanceInterval => TimeSpan.FromMilliseconds(67);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Coronary", StudyDescription = "XA Coronary", SeriesDescription = "Coronary LCA", ProtocolName = "Coronary", BodyPart = "HEART", FieldOfViewMm = 200, DefaultImages = 30, RadiationSetting = "GR", Kvp = 80, TubeCurrentMa = 600, ExposureTimeMs = 7, PositionerPrimaryAngle = -30, PositionerSecondaryAngle = 20 },
            new ExamPreset { Name = "Cerebral", StudyDescription = "XA Cerebral", SeriesDescription = "Cerebral ICA", ProtocolName = "Cerebral", BodyPart = "HEAD", FieldOfViewMm = 250, DefaultImages = 20, RadiationSetting = "GR", Kvp = 90, TubeCurrentMa = 300, ExposureTimeMs = 100 },
            new ExamPreset { Name = "Peripheral", Laterality = "R", StudyDescription = "XA Peripheral", SeriesDescription = "Peripheral Leg", ProtocolName = "Peripheral", BodyPart = "LEG", FieldOfViewMm = 400, DefaultImages = 15, RadiationSetting = "GR", Kvp = 70, TubeCurrentMa = 400, ExposureTimeMs = 50 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY", "SINGLE PLANE" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddFluoroscopyTags(dataset, preset, context, positionerAngles: true);
        }
    }

    /// <summary>X-Ray Radiofluoroscopic Image IOD, single frame. The same X-Ray Image module as XA.</summary>
    public sealed class RfProfile : ModalityProfile
    {
        public override string Code => "RF";
        public override string DisplayName => "RF";
        public override string ObjectName => "X-Ray Radiofluoroscopic Image";
        public override DicomUID SopClass => DicomUID.XRayRadiofluoroscopicImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8, 12, 16 };
        public override int DefaultBitDepth => 12;
        public override int DefaultRows => 1024;
        public override int DefaultColumns => 1024;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(1);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Upper GI", StudyDescription = "RF Upper GI", SeriesDescription = "Upper GI Spot", ProtocolName = "Upper GI", BodyPart = "STOMACH", FieldOfViewMm = 400, DefaultImages = 10, RadiationSetting = "SC", Kvp = 90, TubeCurrentMa = 400, ExposureTimeMs = 100 },
            new ExamPreset { Name = "Voiding Cystogram", StudyDescription = "RF Voiding Cystogram", SeriesDescription = "VCUG Spot", ProtocolName = "VCUG", BodyPart = "BLADDER", FieldOfViewMm = 300, DefaultImages = 8, RadiationSetting = "SC", Kvp = 75, TubeCurrentMa = 200, ExposureTimeMs = 100 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY", "SINGLE PLANE" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddFluoroscopyTags(dataset, preset, context, positionerAngles: false);
        }
    }

    /// <summary>Digital Mammography X-Ray Image IOD, For Presentation. Built on the DX modules. Codes are from PS3.16 CID 4014 and CID 4031.</summary>
    public sealed class MgProfile : ModalityProfile
    {
        public override string Code => "MG";
        public override string DisplayName => "MG";
        public override string ObjectName => "Digital Mammography X-Ray Image";
        public override DicomUID SopClass => DicomUID.DigitalMammographyXRayImageStorageForPresentation;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 12, 14, 16 };
        public override int DefaultBitDepth => 14;
        public override int DefaultRows => 2294;
        public override int DefaultColumns => 1914;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(30);
        public override double? RescaleIntercept => 0;
        public override string RescaleType => "US";

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Left CC", StudyDescription = "Mammography", SeriesDescription = "L CC", ProtocolName = "Screening", BodyPart = "BREAST", FieldOfViewMm = 240, Kvp = 28, TubeCurrentMa = 100, ExposureTimeMs = 1000, ViewPosition = "CC", ImageLaterality = "L", PatientOrientation = "A\\R" },
            new ExamPreset { Name = "Right CC", StudyDescription = "Mammography", SeriesDescription = "R CC", ProtocolName = "Screening", BodyPart = "BREAST", FieldOfViewMm = 240, Kvp = 28, TubeCurrentMa = 100, ExposureTimeMs = 1000, ViewPosition = "CC", ImageLaterality = "R", PatientOrientation = "P\\L" },
            new ExamPreset { Name = "Left MLO", StudyDescription = "Mammography", SeriesDescription = "L MLO", ProtocolName = "Screening", BodyPart = "BREAST", FieldOfViewMm = 240, Kvp = 29, TubeCurrentMa = 100, ExposureTimeMs = 1200, ViewPosition = "MLO", ImageLaterality = "L", PatientOrientation = "A\\FR", PositionerPrimaryAngle = -45 },
            new ExamPreset { Name = "Right MLO", StudyDescription = "Mammography", SeriesDescription = "R MLO", ProtocolName = "Screening", BodyPart = "BREAST", FieldOfViewMm = 240, Kvp = 29, TubeCurrentMa = 100, ExposureTimeMs = 1200, ViewPosition = "MLO", ImageLaterality = "R", PatientOrientation = "P\\FL", PositionerPrimaryAngle = 45 },
        };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddDigitalXRayTags(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.PositionerType, "MAMMOGRAPHIC");
            dataset.AddOrUpdate(DicomTag.PositionerPrimaryAngle, Ds(preset.PositionerPrimaryAngle));
            dataset.AddOrUpdate(DicomTag.OrganExposed, "BREAST");
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AnatomicRegionSequence, CodeItem("SCT", "76752008", "Breast")));
            var view = preset.ViewPosition == "CC"
                ? CodeItem("SCT", "399162004", "cranio-caudal")
                : CodeItem("SCT", "399368009", "medio-lateral oblique");
            view.Add(new DicomSequence(DicomTag.ViewModifierCodeSequence));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ViewCodeSequence, view));
        }
    }

    /// <summary>Positron Emission Tomography Image IOD: a static stack of slices. The PET Image module requires 16 bits allocated and stored.</summary>
    public sealed class PtProfile : ModalityProfile
    {
        private const double F18PositronFraction = 0.9673;

        public override string Code => "PT";
        public override string DisplayName => "PT";
        public override string ObjectName => "Positron Emission Tomography Image";
        public override DicomUID SopClass => DicomUID.PositronEmissionTomographyImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 16 };
        public override int DefaultBitDepth => 16;
        public override int DefaultRows => 256;
        public override int DefaultColumns => 256;
        public override TimeSpan InstanceInterval => TimeSpan.FromMilliseconds(100);
        public override double? RescaleIntercept => 0;
        // The PET Image module has Units (0054,1001) and no Rescale Type.
        public override bool UsesFrameOfReference => true;

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Whole Body", StudyDescription = "PT Whole Body", SeriesDescription = "WB FDG AC", ProtocolName = "Whole Body FDG", BodyPart = "WHOLEBODY", PatientPosition = "FFS", FieldOfViewMm = 700, SliceThicknessMm = 3, DefaultImages = 80, Radiopharmaceutical = "Fluorodeoxyglucose", RadionuclideHalfLifeSeconds = 6586.2, RadionuclideDoseBq = 370000000, FrameDurationMs = 120000 },
            new ExamPreset { Name = "Brain", StudyDescription = "PT Brain", SeriesDescription = "Brain FDG AC", ProtocolName = "Brain FDG", BodyPart = "BRAIN", FieldOfViewMm = 300, SliceThicknessMm = 2, DefaultImages = 60, Radiopharmaceutical = "Fluorodeoxyglucose", RadionuclideHalfLifeSeconds = 6586.2, RadionuclideDoseBq = 185000000, FrameDurationMs = 600000 },
        };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddSliceGeometry(dataset, preset, context);
            AddIsotopeTags(dataset, preset, F18PositronFraction);
            // Patient Position is only allowed here without Patient Orientation Code Sequence, which is Type 2 in this IOD.
            dataset.Remove(DicomTag.PatientPosition);
            dataset.AddOrUpdate(DicomTag.Units, "BQML");
            dataset.AddOrUpdate(DicomTag.CountsSource, "EMISSION");
            dataset.AddOrUpdate(DicomTag.SeriesType, "STATIC", "IMAGE");
            dataset.AddOrUpdate(DicomTag.CorrectedImage, "DECY", "ATTN", "SCAT", "DTIM", "NORM");
            dataset.AddOrUpdate(DicomTag.DecayCorrection, "START");
            dataset.AddOrUpdate(DicomTag.DecayFactor, "1");
            dataset.AddOrUpdate(DicomTag.CollimatorType, "NONE");
            dataset.AddOrUpdate(DicomTag.ReconstructionDiameter, Ds(preset.FieldOfViewMm));
            dataset.AddOrUpdate(DicomTag.ReconstructionMethod, "OSEM");
            dataset.AddOrUpdate(DicomTag.FrameReferenceTime, "0");
            // These are US, and a run can have more images in a series than that holds.
            dataset.AddOrUpdate(DicomTag.NumberOfSlices, (ushort)Math.Min(context.ImagesInSeries, ushort.MaxValue));
            dataset.AddOrUpdate(DicomTag.ImageIndex, (ushort)Math.Min(context.ImageIndex + 1, ushort.MaxValue));
        }
    }

    /// <summary>
    /// Nuclear Medicine Image IOD: a static image with one energy window and one detector. The IOD is always multi-frame,
    /// so each image is one frame with the vectors that describe it. Bits Stored is 8 or 16.
    /// </summary>
    public sealed class NmProfile : ModalityProfile
    {
        public override bool KeepsNumberOfFrames => true;
        private const ushort OnlyWindowOrDetector = 1;
        private const string Tc99mWindowName = "TC99M";

        public override string Code => "NM";
        public override string DisplayName => "NM";
        public override string ObjectName => "Nuclear Medicine Image";
        public override DicomUID SopClass => DicomUID.NuclearMedicineImageStorage;
        public override IReadOnlyList<int> BitDepths { get; } = new[] { 8, 16 };
        public override int DefaultBitDepth => 16;
        public override int DefaultRows => 256;
        public override int DefaultColumns => 256;
        public override TimeSpan InstanceInterval => TimeSpan.FromSeconds(30);

        public override IReadOnlyList<ExamPreset> Presets { get; } = new[]
        {
            new ExamPreset { Name = "Bone", StudyDescription = "NM Bone", SeriesDescription = "Bone Static", ProtocolName = "Bone Static", BodyPart = "SPINE", FieldOfViewMm = 400, Radiopharmaceutical = "Tc-99m MDP", RadionuclideHalfLifeSeconds = 21624, RadionuclideDoseBq = 740000000, FrameDurationMs = 300000, EnergyWindowName = Tc99mWindowName, EnergyWindowLowerKev = 126, EnergyWindowUpperKev = 154 },
            new ExamPreset { Name = "Thyroid", StudyDescription = "NM Thyroid", SeriesDescription = "Thyroid Static", ProtocolName = "Thyroid Static", BodyPart = "THYROID", FieldOfViewMm = 200, Radiopharmaceutical = "Tc-99m Pertechnetate", RadionuclideHalfLifeSeconds = 21624, RadionuclideDoseBq = 185000000, FrameDurationMs = 300000, EnergyWindowName = Tc99mWindowName, EnergyWindowLowerKev = 126, EnergyWindowUpperKev = 154 },
            new ExamPreset { Name = "Lung Perfusion", StudyDescription = "NM Lung Perfusion", SeriesDescription = "Lung Perfusion Static", ProtocolName = "Lung Perfusion", BodyPart = "LUNG", FieldOfViewMm = 400, Radiopharmaceutical = "Tc-99m MAA", RadionuclideHalfLifeSeconds = 21624, RadionuclideDoseBq = 185000000, FrameDurationMs = 180000, EnergyWindowName = Tc99mWindowName, EnergyWindowLowerKev = 126, EnergyWindowUpperKev = 154 },
        };

        public override string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY", "STATIC", "EMISSION" };

        public override void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            var spacing = preset.FieldOfViewMm / Math.Max(context.Rows, context.Columns);
            dataset.AddOrUpdate(DicomTag.PatientOrientation, new string[0]);
            dataset.AddOrUpdate(DicomTag.PixelSpacing, Ds(spacing), Ds(spacing));
            dataset.AddOrUpdate(DicomTag.CountsAccumulated, "1000000");
            AddIsotopeTags(dataset, preset);

            dataset.AddOrUpdate(DicomTag.NumberOfFrames, "1");
            dataset.AddOrUpdate(DicomTag.FrameIncrementPointer, DicomTag.EnergyWindowVector, DicomTag.DetectorVector);

            dataset.AddOrUpdate(DicomTag.EnergyWindowVector, OnlyWindowOrDetector);
            dataset.AddOrUpdate(DicomTag.NumberOfEnergyWindows, OnlyWindowOrDetector);
            dataset.AddOrUpdate(new DicomSequence(DicomTag.EnergyWindowInformationSequence, new DicomDataset
            {
                new DicomSequence(DicomTag.EnergyWindowRangeSequence, new DicomDataset
                {
                    { DicomTag.EnergyWindowLowerLimit, Ds(preset.EnergyWindowLowerKev) },
                    { DicomTag.EnergyWindowUpperLimit, Ds(preset.EnergyWindowUpperKev) },
                }),
                new DicomShortString(DicomTag.EnergyWindowName, preset.EnergyWindowName),
            }));

            dataset.AddOrUpdate(DicomTag.DetectorVector, OnlyWindowOrDetector);
            dataset.AddOrUpdate(DicomTag.NumberOfDetectors, OnlyWindowOrDetector);
            var detector = new DicomDataset
            {
                { DicomTag.CollimatorType, "PARA" },
                { DicomTag.ZoomFactor, Ds(1), Ds(1) },
            };
            // Type 2 with no meaningful value on a planar image.
            detector.Add(DicomTag.FocalDistance, new string[0]);
            detector.Add(DicomTag.ZoomCenter, new string[0]);
            detector.Add(DicomTag.ImagePositionPatient, new string[0]);
            detector.Add(DicomTag.ImageOrientationPatient, new string[0]);
            dataset.AddOrUpdate(new DicomSequence(DicomTag.DetectorInformationSequence, detector));
        }
    }
}
