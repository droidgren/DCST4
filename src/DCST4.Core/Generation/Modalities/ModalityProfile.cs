using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DCST4.Core.Generation.Modalities
{
    /// <summary>Slice orientation of a stack. Row and column direction in the patient coordinate system.</summary>
    public enum SliceOrientation { Axial, Sagittal, Coronal }

    /// <summary>One exam type of a modality. Only the fields relevant for that modality are used.</summary>
    public sealed class ExamPreset
    {
        public string Name { get; set; }
        public string StudyDescription { get; set; }
        public string SeriesDescription { get; set; }
        public string ProtocolName { get; set; }
        public string BodyPart { get; set; }
        public string PatientPosition { get; set; } = "HFS";
        public double FieldOfViewMm { get; set; } = 300;
        public double SliceThicknessMm { get; set; } = 3;
        public SliceOrientation Orientation { get; set; } = SliceOrientation.Axial;
        public int DefaultImages { get; set; } = 1;

        // CT, CR, DX, XA, RF and MG exposure
        public double Kvp { get; set; }
        public int TubeCurrentMa { get; set; }
        public int ExposureTimeMs { get; set; }
        public string ConvolutionKernel { get; set; }

        // MR
        public string ScanningSequence { get; set; }
        public string SequenceVariant { get; set; }
        public string ScanOptions { get; set; }
        public string MrAcquisitionType { get; set; }
        public double RepetitionTimeMs { get; set; }
        public double EchoTimeMs { get; set; }
        public int EchoTrainLength { get; set; }
        public double FlipAngle { get; set; }
        public double MagneticFieldStrengthT { get; set; } = 1.5;

        // CR, DX and MG
        public string ViewPosition { get; set; }
        public string PatientOrientation { get; set; }
        public string ImageLaterality { get; set; } = "U";

        // US
        /// <summary>Image Type value 3, the clinical application.</summary>
        public string UsApplication { get; set; }
        public string TransducerData { get; set; }
        public string TransducerType { get; set; }
        public double ScanDepthCm { get; set; }

        // XA, RF and MG
        public double PositionerPrimaryAngle { get; set; }
        public double PositionerSecondaryAngle { get; set; }

        // XA and RF
        /// <summary>GR for a radiographic exposure, SC for fluoroscopy.</summary>
        public string RadiationSetting { get; set; }

        // PT and NM
        public string Radiopharmaceutical { get; set; }
        public double RadionuclideHalfLifeSeconds { get; set; }
        public double RadionuclideDoseBq { get; set; }
        public int FrameDurationMs { get; set; }

        // NM
        public string EnergyWindowName { get; set; }
        public double EnergyWindowLowerKev { get; set; }
        public double EnergyWindowUpperKev { get; set; }

        public override string ToString() => Name;
    }

    /// <summary>Position of one image within the exam, plus the matrix, for the modality-specific tags.</summary>
    public sealed class ImageContext
    {
        public int SeriesIndex { get; set; }
        public int ImageIndex { get; set; }
        public int ImagesInSeries { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int BitsStored { get; set; }
    }

    /// <summary>
    /// The tags and pixel rules of one modality. To add a modality: subclass this, then add it to <see cref="ModalityCatalog"/>.
    /// </summary>
    public abstract class ModalityProfile
    {
        public abstract string Code { get; }
        public abstract string DisplayName { get; }
        public abstract DicomUID SopClass { get; }
        /// <summary>The Bits Stored values this modality's IOD allows.</summary>
        public abstract IReadOnlyList<int> BitDepths { get; }
        public abstract int DefaultBitDepth { get; }
        public abstract IReadOnlyList<ExamPreset> Presets { get; }
        /// <summary>Simulated time between images, used for Content Time.</summary>
        public abstract TimeSpan InstanceInterval { get; }
        public virtual int DefaultRows => 512;
        public virtual int DefaultColumns => 512;

        /// <summary>Largest stored pixel value used at this bit depth.</summary>
        public virtual int MaxStoredValue(int bitsStored) => (1 << bitsStored) - 1;

        /// <summary>Modality LUT. Null intercept means the image has no rescale tags.</summary>
        public virtual double? RescaleIntercept => null;
        public virtual string RescaleType => null;

        /// <summary>A stack in the patient coordinate system: the images get Frame of Reference and Patient Position.</summary>
        public virtual bool UsesFrameOfReference => false;

        public virtual string[] ImageType(ExamPreset preset) => new[] { "ORIGINAL", "PRIMARY" };

        public abstract void AddModalityTags(DicomDataset dataset, ExamPreset preset, ImageContext context);

        public override string ToString() => DisplayName;

        internal static string Ds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        /// <summary>Frame of Reference, Image Plane and related tags shared by CT, MR and PT stacks.</summary>
        protected static void AddSliceGeometry(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            double[] row, column;
            switch (preset.Orientation)
            {
                case SliceOrientation.Sagittal: row = new[] { 0.0, 1, 0 }; column = new[] { 0.0, 0, -1 }; break;
                case SliceOrientation.Coronal: row = new[] { 1.0, 0, 0 }; column = new[] { 0.0, 0, -1 }; break;
                default: row = new[] { 1.0, 0, 0 }; column = new[] { 0.0, 1, 0 }; break;
            }
            var normal = new[]
            {
                row[1] * column[2] - row[2] * column[1],
                row[2] * column[0] - row[0] * column[2],
                row[0] * column[1] - row[1] * column[0],
            };

            var spacing = preset.FieldOfViewMm / Math.Max(context.Rows, context.Columns);
            var sliceOffset = (context.ImageIndex - (context.ImagesInSeries - 1) / 2.0) * preset.SliceThicknessMm;

            var position = new double[3];
            for (var i = 0; i < 3; i++)
            {
                position[i] = -row[i] * context.Columns * spacing / 2 - column[i] * context.Rows * spacing / 2 + normal[i] * sliceOffset;
            }

            dataset.AddOrUpdate(DicomTag.PositionReferenceIndicator, string.Empty);
            dataset.AddOrUpdate(DicomTag.ImageOrientationPatient, row[0], row[1], row[2], column[0], column[1], column[2]);
            dataset.AddOrUpdate(DicomTag.ImagePositionPatient, Ds(position[0]), Ds(position[1]), Ds(position[2]));
            dataset.AddOrUpdate(DicomTag.PixelSpacing, Ds(spacing), Ds(spacing));
            dataset.AddOrUpdate(DicomTag.SliceThickness, Ds(preset.SliceThicknessMm));
            dataset.AddOrUpdate(DicomTag.SpacingBetweenSlices, Ds(preset.SliceThicknessMm));
            dataset.AddOrUpdate(DicomTag.SliceLocation, Ds(sliceOffset));
        }

        /// <summary>Imager Pixel Spacing and exposure tags shared by CR, DX and MG.</summary>
        protected static void AddProjectionTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            var spacing = preset.FieldOfViewMm / Math.Max(context.Rows, context.Columns);
            dataset.AddOrUpdate(DicomTag.ImagerPixelSpacing, Ds(spacing), Ds(spacing));
            dataset.AddOrUpdate(DicomTag.ViewPosition, preset.ViewPosition ?? string.Empty);
            dataset.AddOrUpdate(DicomTag.PatientOrientation, (preset.PatientOrientation ?? "L\\F").Split('\\'));
            dataset.AddOrUpdate(DicomTag.KVP, Ds(preset.Kvp));
            dataset.AddOrUpdate(DicomTag.ExposureTime, preset.ExposureTimeMs.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.XRayTubeCurrent, preset.TubeCurrentMa.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The DX modules shared by DX and MG, For Presentation. Each adds its own Anatomic Region Sequence.</summary>
        protected static void AddDigitalXRayTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            AddProjectionTags(dataset, preset, context);
            dataset.AddOrUpdate(DicomTag.PresentationIntentType, "FOR PRESENTATION");
            dataset.AddOrUpdate(DicomTag.PresentationLUTShape, "IDENTITY");
            dataset.AddOrUpdate(DicomTag.PixelIntensityRelationship, "LIN");
            dataset.AddOrUpdate(DicomTag.PixelIntensityRelationshipSign, (short)1);
            dataset.AddOrUpdate(DicomTag.DetectorType, "DIRECT");
            dataset.AddOrUpdate(DicomTag.ImageLaterality, preset.ImageLaterality);
            dataset.AddOrUpdate(new DicomSequence(DicomTag.AcquisitionContextSequence));
        }

        /// <summary>X-Ray Image, X-Ray Acquisition and positioner tags shared by XA and RF.</summary>
        protected static void AddFluoroscopyTags(DicomDataset dataset, ExamPreset preset, ImageContext context)
        {
            var spacing = preset.FieldOfViewMm / Math.Max(context.Rows, context.Columns);
            dataset.AddOrUpdate(DicomTag.ImagerPixelSpacing, Ds(spacing), Ds(spacing));
            dataset.AddOrUpdate(DicomTag.PatientOrientation, new string[0]);
            dataset.AddOrUpdate(DicomTag.PixelIntensityRelationship, "LIN");
            dataset.AddOrUpdate(DicomTag.RadiationSetting, preset.RadiationSetting);
            dataset.AddOrUpdate(DicomTag.KVP, Ds(preset.Kvp));
            dataset.AddOrUpdate(DicomTag.XRayTubeCurrent, preset.TubeCurrentMa.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.ExposureTime, preset.ExposureTimeMs.ToString(CultureInfo.InvariantCulture));
            var milliampereSeconds = (int)Math.Round(preset.TubeCurrentMa * preset.ExposureTimeMs / 1000.0);
            dataset.AddOrUpdate(DicomTag.Exposure, milliampereSeconds.ToString(CultureInfo.InvariantCulture));
            dataset.AddOrUpdate(DicomTag.PositionerPrimaryAngle, Ds(preset.PositionerPrimaryAngle));
            dataset.AddOrUpdate(DicomTag.PositionerSecondaryAngle, Ds(preset.PositionerSecondaryAngle));
        }

        /// <summary>The tracer, and the Type 2 patient orientation sequences of the NM/PET Patient Orientation module, shared by PT and NM.</summary>
        protected static void AddIsotopeTags(DicomDataset dataset, ExamPreset preset, double? positronFraction = null)
        {
            var tracer = new DicomDataset
            {
                { DicomTag.Radiopharmaceutical, preset.Radiopharmaceutical },
                { DicomTag.RadionuclideTotalDose, Ds(preset.RadionuclideDoseBq) },
                { DicomTag.RadionuclideHalfLife, Ds(preset.RadionuclideHalfLifeSeconds) },
            };
            if (positronFraction != null) tracer.Add(DicomTag.RadionuclidePositronFraction, Ds(positronFraction.Value));
            tracer.Add(new DicomSequence(DicomTag.RadionuclideCodeSequence));

            dataset.AddOrUpdate(new DicomSequence(DicomTag.RadiopharmaceuticalInformationSequence, tracer));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.PatientOrientationCodeSequence));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.PatientGantryRelationshipCodeSequence));
            dataset.AddOrUpdate(DicomTag.ActualFrameDuration, preset.FrameDurationMs.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>One item of a code sequence.</summary>
        protected static DicomDataset CodeItem(string codingScheme, string codeValue, string codeMeaning) => new DicomDataset
        {
            { DicomTag.CodeValue, codeValue },
            { DicomTag.CodingSchemeDesignator, codingScheme },
            { DicomTag.CodeMeaning, codeMeaning },
        };
    }
}
