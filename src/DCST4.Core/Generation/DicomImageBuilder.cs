using DCST4.Core.Generation.Modalities;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace DCST4.Core.Generation
{
    /// <summary>Builds one complete DICOM image from an exam and its position in that exam.</summary>
    public static class DicomImageBuilder
    {
        public const string TestImageComment = ProductInfo.ProjectUrl;
        private const string Manufacturer = "DCST4";
        private const string ManufacturerModel = "DCST4 Test Image Generator";

        /// <summary>A pixel at or above this level is set in a binary mask. Low enough for the watermark to show in the mask.</summary>
        private const int BinaryMaskThreshold = 60;
        /// <summary>The gray levels of the disc that PixelRenderer draws for the slice marker.</summary>
        private const int BinaryMaskDiscMin = 110;
        private const int BinaryMaskDiscMax = 130;
        private const int BitsPerByte = 8;

        private static readonly string SoftwareVersion = typeof(DicomImageBuilder).GetTypeInfo().Assembly.GetName().Version.ToString(3);

        public static DicomDataset Build(ExamRequest request, SeriesContext series, int imageIndex, DateTime contentTime)
        {
            var id = request.Identity;
            var modality = request.Modality;
            var preset = request.Preset;

            var dataset = BuildHeader(request, series, modality.SopClass, modality.Code, imageIndex, contentTime);

            dataset.Add(DicomTag.BodyPartExamined, preset.BodyPart ?? string.Empty);
            dataset.Add(DicomTag.ProtocolName, preset.ProtocolName ?? string.Empty);
            if (modality.UsesFrameOfReference)
            {
                dataset.Add(DicomTag.PatientPosition, preset.PatientPosition);
                dataset.Add(DicomTag.FrameOfReferenceUID, series.FrameOfReferenceUid);
            }

            // Image
            dataset.Add(DicomTag.AcquisitionNumber, (series.SeriesIndex + 1 + request.SeriesNumberOffset).ToString(CultureInfo.InvariantCulture));
            if (!modality.UsesFunctionalGroups)
            {
                dataset.Add(DicomTag.AcquisitionDate, Da(contentTime));
                dataset.Add(DicomTag.AcquisitionTime, Tm(contentTime));
            }
            dataset.Add(DicomTag.ImageType, modality.ImageType(preset));
            dataset.Add(DicomTag.ImageComments, TestImageComment);
            dataset.Add(DicomTag.LossyImageCompression, "00");

            var context = new ImageContext
            {
                SeriesIndex = series.SeriesIndex,
                ImageIndex = imageIndex,
                ImagesInSeries = request.ImagesPerSeries,
                Rows = request.Rows,
                Columns = request.Columns,
                BitsStored = request.BitsStored,
                Frames = request.FramesPerImage,
                Uids = request.Uids,
                FrameOfReferenceUid = series.FrameOfReferenceUid,
                SourceImage = request.SourceImage,
                ContentTime = contentTime,
            };
            modality.AddModalityTags(dataset, preset, context);

            // After the program's own tags, so that a custom tag can replace one such as Manufacturer. The rules keep it from the pixel description.
            foreach (var additional in id.AdditionalTags) AdditionalTagWriter.Write(dataset, additional);

            AddPixels(dataset, request, imageIndex);
            return dataset;
        }

        /// <summary>The patient, study, series and equipment attributes, which every object has whether it holds an image or not.</summary>
        internal static DicomDataset BuildHeader(ExamRequest request, SeriesContext series, DicomUID sopClass, string modalityCode, int instanceIndex, DateTime contentTime)
        {
            var id = request.Identity;
            var instanceNumber = instanceIndex + 1;
            var seriesNumber = series.SeriesIndex + 1 + request.SeriesNumberOffset;

            var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);

            // Specific Character Set must be added before any text elements so that they are encoded with it.
            dataset.Add(DicomTag.SpecificCharacterSet, request.CharacterSet);

            dataset.Add(DicomTag.SOPClassUID, sopClass);
            dataset.Add(DicomTag.SOPInstanceUID, id.SopInstanceUid ?? request.Uids.Uid("sop/" + series.SeriesIndex.ToString(CultureInfo.InvariantCulture) + "/" + instanceIndex.ToString(CultureInfo.InvariantCulture)));
            dataset.Add(DicomTag.InstanceCreationDate, Da(contentTime));
            dataset.Add(DicomTag.InstanceCreationTime, Tm(contentTime));

            // Patient
            dataset.Add(DicomTag.PatientName, id.PatientName ?? string.Empty);
            dataset.Add(DicomTag.PatientID, id.PatientId ?? string.Empty);
            dataset.Add(DicomTag.PatientBirthDate, id.PatientBirthDate ?? string.Empty);
            dataset.Add(DicomTag.PatientSex, id.PatientSex ?? string.Empty);
            var patientAge = PatientAge(id.PatientBirthDate, id.StudyDateTime);
            if (patientAge != null) dataset.Add(DicomTag.PatientAge, patientAge);

            // Study
            dataset.Add(DicomTag.StudyInstanceUID, id.StudyInstanceUid);
            dataset.Add(DicomTag.StudyDate, Da(series.StudyTime));
            dataset.Add(DicomTag.StudyTime, Tm(series.StudyTime));
            dataset.Add(DicomTag.ReferringPhysicianName, id.ReferringPhysicianName ?? string.Empty);
            dataset.Add(DicomTag.StudyID, id.StudyId ?? string.Empty);
            dataset.Add(DicomTag.AccessionNumber, id.AccessionNumber ?? string.Empty);
            dataset.Add(DicomTag.StudyDescription, id.StudyDescription ?? string.Empty);

            // Series
            dataset.Add(DicomTag.Modality, modalityCode);
            dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
            dataset.Add(DicomTag.SeriesNumber, seriesNumber.ToString(CultureInfo.InvariantCulture));
            dataset.Add(DicomTag.SeriesDate, Da(series.SeriesTime));
            dataset.Add(DicomTag.SeriesTime, Tm(series.SeriesTime));
            dataset.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);

            // Equipment
            dataset.Add(DicomTag.Manufacturer, Manufacturer);
            dataset.Add(DicomTag.ManufacturerModelName, ManufacturerModel);
            dataset.Add(DicomTag.InstitutionName, id.InstitutionName ?? string.Empty);
            dataset.Add(DicomTag.StationName, id.StationName ?? string.Empty);
            dataset.Add(DicomTag.DeviceSerialNumber, "DCST4-0001");
            dataset.Add(DicomTag.SoftwareVersions, SoftwareVersion);

            // Instance
            dataset.Add(DicomTag.InstanceNumber, instanceNumber.ToString(CultureInfo.InvariantCulture));
            dataset.Add(DicomTag.ContentDate, Da(contentTime));
            dataset.Add(DicomTag.ContentTime, Tm(contentTime));

            return dataset;
        }

        private static void AddPixels(DicomDataset dataset, ExamRequest request, int imageIndex)
        {
            var modality = request.Modality;

            // Read here, after the additional tags, since one of those can replace a value that is shown.
            var lines = BurnedInText.Lines(dataset, request.BurnedInTags);

            // Only the text can say who the patient is: the watermark and the other graphics are the same for everybody.
            dataset.Add(DicomTag.BurnedInAnnotation, lines.Count > 0 ? "YES" : "NO");

            var first = RenderFrame(request, imageIndex, 0, lines);
            if (modality.IsBinaryMask)
            {
                AddBinaryMask(dataset, request, imageIndex, lines, first);
                return;
            }

            if (modality.SamplesPerPixel == 1) AddDisplayTags(dataset, modality, first);

            dataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)request.BitsAllocated);
            var pixelData = DicomPixelData.Create(dataset, true);
            pixelData.BitsStored = (ushort)request.BitsStored;
            pixelData.HighBit = (ushort)(request.BitsStored - 1);
            pixelData.SamplesPerPixel = (ushort)modality.SamplesPerPixel;
            pixelData.PixelRepresentation = PixelRepresentation.Unsigned;
            pixelData.Width = (ushort)request.Columns;
            pixelData.Height = (ushort)request.Rows;
            pixelData.PhotometricInterpretation = modality.SamplesPerPixel == 1 ? PhotometricInterpretation.Monochrome2 : PhotometricInterpretation.Rgb;
            if (modality.SamplesPerPixel > 1) pixelData.PlanarConfiguration = PlanarConfiguration.Interleaved;

            if (modality.RendersFramesOnDemand)
            {
                var frameSize = first.Data.Length;
                dataset.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData,
                    new RenderedFramesBuffer(request.FramesPerImage, frameSize, frame => RenderFrame(request, imageIndex, frame, lines).Data)));
                dataset.AddOrUpdate(DicomTag.NumberOfFrames, request.FramesPerImage);
                return;
            }

            pixelData.AddFrame(new MemoryByteBuffer(first.Data));
            for (var frame = 1; frame < request.FramesPerImage; frame++)
            {
                pixelData.AddFrame(new MemoryByteBuffer(RenderFrame(request, imageIndex, frame, lines).Data));
            }
        }

        /// <summary>A multi-frame image scrolls through its frames, as a single-frame stack does through its images.</summary>
        private static RenderedPixels RenderFrame(ExamRequest request, int imageIndex, int frameIndex, IReadOnlyList<string> lines)
        {
            var multiFrame = request.FramesPerImage > 1;
            return PixelRenderer.Render(new PixelRenderSpec
            {
                Rows = request.Rows,
                Columns = request.Columns,
                BitsAllocated = request.BitsAllocated,
                MaxStoredValue = request.Modality.MaxStoredValue(request.BitsStored),
                SamplesPerPixel = request.Modality.SamplesPerPixel,
                ImageIndex = multiFrame ? frameIndex : imageIndex,
                ImagesInSeries = multiFrame ? request.FramesPerImage : request.ImagesPerSeries,
                FrameNumber = multiFrame ? frameIndex + 1 : 0,
                Lines = lines,
            });
        }

        private static void AddDisplayTags(DicomDataset dataset, ModalityProfile modality, RenderedPixels pixels)
        {
            // The window comes from the pixels actually stored, or a viewer could open the image too dark to see it.
            // Every frame holds the same gray bar, so the first frame's range holds for all of them.
            var intercept = modality.RescaleIntercept ?? 0;
            var low = pixels.MinValue + intercept;
            var high = pixels.MaxValue + intercept;

            var window = new DicomDataset
            {
                { DicomTag.WindowCenter, ModalityProfile.Ds((low + high + 1) / 2) },
                { DicomTag.WindowWidth, ModalityProfile.Ds(Math.Max(1, high - low + 1)) },
                { DicomTag.WindowCenterWidthExplanation, "FULL RANGE" },
            };

            DicomDataset rescale = null;
            if (modality.RescaleIntercept != null)
            {
                rescale = new DicomDataset
                {
                    { DicomTag.RescaleIntercept, ModalityProfile.Ds(modality.RescaleIntercept.Value) },
                    { DicomTag.RescaleSlope, "1" },
                    { DicomTag.RescaleType, modality.RescaleType },
                };
            }

            if (modality.UsesFunctionalGroups)
            {
                var shared = dataset.GetSequence(DicomTag.SharedFunctionalGroupsSequence).Items[0];
                shared.AddOrUpdate(new DicomSequence(DicomTag.FrameVOILUTSequence, window));
                if (rescale != null) shared.AddOrUpdate(new DicomSequence(DicomTag.PixelValueTransformationSequence, rescale));
                return;
            }

            dataset.Add(window);
            if (rescale != null) dataset.Add(rescale);
        }

        /// <summary>One bit per pixel, packed with the first pixel in the lowest bit, continuing from one frame to the next (PS3.5 8.1.1).</summary>
        private static void AddBinaryMask(DicomDataset dataset, ExamRequest request, int imageIndex, IReadOnlyList<string> lines, RenderedPixels first)
        {
            var frames = request.FramesPerImage;
            var pixelsPerFrame = request.Rows * request.Columns;
            var packed = new byte[(pixelsPerFrame * frames + BitsPerByte - 1) / BitsPerByte];

            for (var frame = 0; frame < frames; frame++)
            {
                var rendered = frame == 0 ? first : RenderFrame(request, imageIndex, frame, lines);
                for (var pixel = 0; pixel < pixelsPerFrame; pixel++)
                {
                    if (!IsInBinaryMask(rendered.Data[pixel])) continue;
                    var bit = frame * pixelsPerFrame + pixel;
                    packed[bit / BitsPerByte] |= (byte)(1 << (bit % BitsPerByte));
                }
            }

            dataset.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
            dataset.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
            dataset.AddOrUpdate(DicomTag.Rows, (ushort)request.Rows);
            dataset.AddOrUpdate(DicomTag.Columns, (ushort)request.Columns);
            dataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)1);
            dataset.AddOrUpdate(DicomTag.BitsStored, (ushort)1);
            dataset.AddOrUpdate(DicomTag.HighBit, (ushort)0);
            dataset.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
            dataset.AddOrUpdate(DicomTag.NumberOfFrames, frames);
            dataset.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer(packed)));
        }

        /// <summary>The disc is left out, or the part of the watermark that crosses it would disappear into a solid area.</summary>
        private static bool IsInBinaryMask(byte level) =>
            level >= BinaryMaskThreshold && (level < BinaryMaskDiscMin || level > BinaryMaskDiscMax);

        /// <summary>Patient's Age (AS) at the study date, or null if the birth date is unknown or later than the study.</summary>
        public static string PatientAge(string birthDate, DateTime studyDate)
        {
            if (string.IsNullOrEmpty(birthDate)) return null;
            if (!DateTime.TryParseExact(birthDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var born)) return null;
            if (born.Date > studyDate.Date) return null;

            var years = TestPersonnummerRegistry.AgeOn(born, studyDate);
            if (years >= 1) return years.ToString("000", CultureInfo.InvariantCulture) + "Y";

            var months = (studyDate.Year - born.Year) * 12 + studyDate.Month - born.Month - (studyDate.Day < born.Day ? 1 : 0);
            if (months >= 1) return months.ToString("000", CultureInfo.InvariantCulture) + "M";

            return ((int)(studyDate.Date - born.Date).TotalDays).ToString("000", CultureInfo.InvariantCulture) + "D";
        }

        internal static string Da(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        internal static string Tm(DateTime value) => value.ToString("HHmmss.ffffff", CultureInfo.InvariantCulture);
        internal static string Dt(DateTime value) => value.ToString("yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture);
    }

    /// <summary>The values shared by all images of a series.</summary>
    public sealed class SeriesContext
    {
        public int SeriesIndex { get; set; }
        public string SeriesInstanceUid { get; set; }
        public string FrameOfReferenceUid { get; set; }
        public string SeriesDescription { get; set; }
        public DateTime StudyTime { get; set; }
        public DateTime SeriesTime { get; set; }
    }
}
