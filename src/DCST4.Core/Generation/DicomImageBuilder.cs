using DCST4.Core.Generation.Modalities;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using System;
using System.Globalization;
using System.Reflection;

namespace DCST4.Core.Generation
{
    /// <summary>Builds one complete DICOM image from an exam and its position in that exam.</summary>
    public static class DicomImageBuilder
    {
        public const string TestImageComment = ProductInfo.ProjectUrl;
        private const string Manufacturer = "DCST4";
        private const string ManufacturerModel = "DCST4 Test Image Generator";

        private static readonly string SoftwareVersion = typeof(DicomImageBuilder).GetTypeInfo().Assembly.GetName().Version.ToString(3);

        public static DicomDataset Build(ExamRequest request, SeriesContext series, int imageIndex, DateTime contentTime)
        {
            var id = request.Identity;
            var modality = request.Modality;
            var preset = request.Preset;
            var instanceNumber = imageIndex + 1;

            var dataset = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);

            // Specific Character Set must be added before any text elements so that they are encoded with it.
            dataset.Add(DicomTag.SpecificCharacterSet, request.CharacterSet);

            dataset.Add(DicomTag.SOPClassUID, modality.SopClass);
            dataset.Add(DicomTag.SOPInstanceUID, id.SopInstanceUid ?? IdentifierGenerator.Uid());
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
            dataset.Add(DicomTag.Modality, modality.Code);
            dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
            dataset.Add(DicomTag.SeriesNumber, (series.SeriesIndex + 1).ToString(CultureInfo.InvariantCulture));
            dataset.Add(DicomTag.SeriesDate, Da(series.SeriesTime));
            dataset.Add(DicomTag.SeriesTime, Tm(series.SeriesTime));
            dataset.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);
            dataset.Add(DicomTag.BodyPartExamined, preset.BodyPart ?? string.Empty);
            dataset.Add(DicomTag.ProtocolName, preset.ProtocolName ?? string.Empty);
            if (modality.UsesFrameOfReference)
            {
                dataset.Add(DicomTag.PatientPosition, preset.PatientPosition);
                dataset.Add(DicomTag.FrameOfReferenceUID, series.FrameOfReferenceUid);
            }

            // Equipment
            dataset.Add(DicomTag.Manufacturer, Manufacturer);
            dataset.Add(DicomTag.ManufacturerModelName, ManufacturerModel);
            dataset.Add(DicomTag.InstitutionName, id.InstitutionName ?? string.Empty);
            dataset.Add(DicomTag.StationName, id.StationName ?? string.Empty);
            dataset.Add(DicomTag.DeviceSerialNumber, "DCST4-0001");
            dataset.Add(DicomTag.SoftwareVersions, SoftwareVersion);

            // Image
            dataset.Add(DicomTag.InstanceNumber, instanceNumber.ToString(CultureInfo.InvariantCulture));
            dataset.Add(DicomTag.AcquisitionNumber, (series.SeriesIndex + 1).ToString(CultureInfo.InvariantCulture));
            dataset.Add(DicomTag.ContentDate, Da(contentTime));
            dataset.Add(DicomTag.ContentTime, Tm(contentTime));
            dataset.Add(DicomTag.AcquisitionDate, Da(contentTime));
            dataset.Add(DicomTag.AcquisitionTime, Tm(contentTime));
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
            };
            modality.AddModalityTags(dataset, preset, context);

            // After the program's own tags, so that a custom tag can replace one such as Manufacturer. The rules keep it from the pixel description.
            foreach (var additional in id.AdditionalTags) AdditionalTagWriter.Write(dataset, additional);

            AddPixels(dataset, request, imageIndex);
            return dataset;
        }

        private static void AddPixels(DicomDataset dataset, ExamRequest request, int imageIndex)
        {
            var modality = request.Modality;

            // Read here, after the additional tags, since one of those can replace a value that is shown.
            var lines = BurnedInText.Lines(dataset, request.BurnedInTags);

            // Only the text can say who the patient is: the watermark and the other graphics are the same for everybody.
            dataset.Add(DicomTag.BurnedInAnnotation, lines.Count > 0 ? "YES" : "NO");

            var pixels = PixelRenderer.Render(new PixelRenderSpec
            {
                Rows = request.Rows,
                Columns = request.Columns,
                BitsAllocated = request.BitsAllocated,
                MaxStoredValue = modality.MaxStoredValue(request.BitsStored),
                ImageIndex = imageIndex,
                ImagesInSeries = request.ImagesPerSeries,
                Lines = lines,
            });

            // The window comes from the pixels actually stored, or a viewer could open the image too dark to see it.
            var intercept = modality.RescaleIntercept ?? 0;
            var low = pixels.MinValue + intercept;
            var high = pixels.MaxValue + intercept;
            dataset.Add(DicomTag.WindowCenter, ModalityProfile.Ds((low + high + 1) / 2));
            dataset.Add(DicomTag.WindowWidth, ModalityProfile.Ds(Math.Max(1, high - low + 1)));
            dataset.Add(DicomTag.WindowCenterWidthExplanation, "FULL RANGE");

            if (modality.RescaleIntercept != null)
            {
                dataset.Add(DicomTag.RescaleIntercept, ModalityProfile.Ds(modality.RescaleIntercept.Value));
                dataset.Add(DicomTag.RescaleSlope, "1");
                dataset.Add(DicomTag.RescaleType, modality.RescaleType);
            }

            dataset.AddOrUpdate(DicomTag.BitsAllocated, (ushort)request.BitsAllocated);
            var pixelData = DicomPixelData.Create(dataset, true);
            pixelData.BitsStored = (ushort)request.BitsStored;
            pixelData.HighBit = (ushort)(request.BitsStored - 1);
            pixelData.SamplesPerPixel = 1;
            pixelData.PixelRepresentation = PixelRepresentation.Unsigned;
            pixelData.Width = (ushort)request.Columns;
            pixelData.Height = (ushort)request.Rows;
            pixelData.PhotometricInterpretation = PhotometricInterpretation.Monochrome2;
            pixelData.AddFrame(new MemoryByteBuffer(pixels.Data));
        }

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

        private static string Da(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        private static string Tm(DateTime value) => value.ToString("HHmmss.ffffff", CultureInfo.InvariantCulture);
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
