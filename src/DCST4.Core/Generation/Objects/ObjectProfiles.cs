using DCST4.Core.Generation.Modalities;
using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using System;
using System.Globalization;
using System.Text;

namespace DCST4.Core.Generation.Objects
{
    /// <summary>What a non-image object needs to know to fill in its content.</summary>
    public sealed class ObjectContext
    {
        public ExamRequest Request { get; set; }
        public SeriesContext Series { get; set; }
        public int InstanceIndex { get; set; }
        public DateTime ContentTime { get; set; }
        /// <summary>The image it refers to. Null for an object that refers to none.</summary>
        public ReferencedImage Source { get; set; }
        /// <summary>Text added to free-text values, which holds å, ä and ö in a character set variant. Empty otherwise.</summary>
        public string TextSuffix { get; set; }
    }

    /// <summary>An object without Pixel Data, such as a report or a document. Comes after the shared header from <see cref="DicomImageBuilder"/>.</summary>
    public abstract class ObjectProfile
    {
        public abstract string Code { get; }
        public abstract string DisplayName { get; }
        public abstract DicomUID SopClass { get; }
        public abstract ExamPreset Preset { get; }
        /// <summary>Refers to an image, so that one is generated first.</summary>
        public virtual bool NeedsSourceImage => false;

        public abstract void AddContent(DicomDataset dataset, ObjectContext context);

        public DicomDataset Build(ObjectContext context)
        {
            var dataset = DicomImageBuilder.BuildHeader(context.Request, context.Series, SopClass, Code, context.InstanceIndex, context.ContentTime);
            AddContent(dataset, context);
            return dataset;
        }

        protected static DicomDataset CodeItem(string codingScheme, string codeValue, string codeMeaning) => new DicomDataset
        {
            { DicomTag.CodeValue, codeValue },
            { DicomTag.CodingSchemeDesignator, codingScheme },
            { DicomTag.CodeMeaning, codeMeaning },
        };

        protected static DicomSequence ConceptName(string codingScheme, string codeValue, string codeMeaning) =>
            new DicomSequence(DicomTag.ConceptNameCodeSequence, CodeItem(codingScheme, codeValue, codeMeaning));

        /// <summary>The source image, as a Referenced SOP Sequence item.</summary>
        protected static DicomDataset SopReference(ReferencedImage source) => new DicomDataset
        {
            { DicomTag.ReferencedSOPClassUID, source.SopClassUid },
            { DicomTag.ReferencedSOPInstanceUID, source.SopInstanceUid },
        };

        /// <summary>The Hierarchical SOP Instance Reference Macro for one image, as the evidence sequences need it.</summary>
        protected static DicomDataset EvidenceItem(ReferencedImage source) => new DicomDataset
        {
            { DicomTag.StudyInstanceUID, source.StudyInstanceUid },
            new DicomSequence(DicomTag.ReferencedSeriesSequence, new DicomDataset
            {
                { DicomTag.SeriesInstanceUID, source.SeriesInstanceUid },
                new DicomSequence(DicomTag.ReferencedSOPSequence, SopReference(source)),
            }),
        };

        protected static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Basic Text SR IOD: a title with text items in nested containers.</summary>
    public sealed class BasicTextSrProfile : ObjectProfile
    {
        public override string Code => "SR";
        public override string DisplayName => "Basic Text SR";
        public override DicomUID SopClass => DicomUID.BasicTextSRStorage;
        public override ExamPreset Preset { get; } = new ExamPreset { Name = "Report", StudyDescription = "Test report", SeriesDescription = "Report", ProtocolName = "Report", BodyPart = string.Empty };

        public override void AddContent(DicomDataset dataset, ObjectContext context)
        {
            dataset.AddOrUpdate(DicomTag.CompletionFlag, "COMPLETE");
            dataset.AddOrUpdate(DicomTag.VerificationFlag, "UNVERIFIED");
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ReferencedPerformedProcedureStepSequence));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.PerformedProcedureCodeSequence));

            dataset.AddOrUpdate(DicomTag.ValueType, "CONTAINER");
            dataset.AddOrUpdate(ConceptName("LN", "18748-4", "Diagnostic Imaging Report"));
            dataset.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");

            var findings = new DicomDataset
            {
                { DicomTag.RelationshipType, "CONTAINS" },
                { DicomTag.ValueType, "TEXT" },
                ConceptName("DCM", "121071", "Finding"),
            };
            findings.Add(DicomTag.TextValue, "DCST4 test report, no real patient." + context.TextSuffix);

            var section = new DicomDataset
            {
                { DicomTag.RelationshipType, "CONTAINS" },
                { DicomTag.ValueType, "CONTAINER" },
                ConceptName("DCM", "121070", "Findings"),
                { DicomTag.ContinuityOfContent, "SEPARATE" },
                new DicomSequence(DicomTag.ContentSequence, findings),
            };
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ContentSequence, section));
        }
    }

    /// <summary>Key Object Selection Document IOD: template 2010, pointing at the source image.</summary>
    public sealed class KeyObjectProfile : ObjectProfile
    {
        public override string Code => "KO";
        public override string DisplayName => "Key Object Selection";
        public override DicomUID SopClass => DicomUID.KeyObjectSelectionDocumentStorage;
        public override bool NeedsSourceImage => true;
        public override ExamPreset Preset { get; } = new ExamPreset { Name = "Key images", StudyDescription = "Key images", SeriesDescription = "Key images", ProtocolName = "Key images", BodyPart = string.Empty };

        public override void AddContent(DicomDataset dataset, ObjectContext context)
        {
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ReferencedPerformedProcedureStepSequence));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence, EvidenceItem(context.Source)));

            dataset.AddOrUpdate(DicomTag.ValueType, "CONTAINER");
            dataset.AddOrUpdate(ConceptName("DCM", "113000", "Of Interest"));
            dataset.AddOrUpdate(DicomTag.ContinuityOfContent, "SEPARATE");
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ContentTemplateSequence, new DicomDataset
            {
                { DicomTag.MappingResource, "DCMR" },
                { DicomTag.TemplateIdentifier, "2010" },
            }));

            var description = new DicomDataset
            {
                { DicomTag.RelationshipType, "CONTAINS" },
                { DicomTag.ValueType, "TEXT" },
                ConceptName("DCM", "113012", "Key Object Description"),
            };
            description.Add(DicomTag.TextValue, "DCST4 test selection." + context.TextSuffix);

            var image = new DicomDataset
            {
                { DicomTag.RelationshipType, "CONTAINS" },
                { DicomTag.ValueType, "IMAGE" },
                new DicomSequence(DicomTag.ReferencedSOPSequence, SopReference(context.Source)),
            };

            dataset.AddOrUpdate(new DicomSequence(DicomTag.ContentSequence, description, image));
        }
    }

    /// <summary>Grayscale Softcopy Presentation State IOD: a text annotation on the source image.</summary>
    public sealed class PresentationStateProfile : ObjectProfile
    {
        private const string LayerName = "DCST4";

        public override string Code => "PR";
        public override string DisplayName => "Grayscale Softcopy Presentation State";
        public override DicomUID SopClass => DicomUID.GrayscaleSoftcopyPresentationStateStorage;
        public override bool NeedsSourceImage => true;
        public override ExamPreset Preset { get; } = new ExamPreset { Name = "Annotation", StudyDescription = "Annotated image", SeriesDescription = "Annotation", ProtocolName = "Annotation", BodyPart = string.Empty };

        public override void AddContent(DicomDataset dataset, ObjectContext context)
        {
            var source = context.Source;
            dataset.Remove(DicomTag.ContentDate, DicomTag.ContentTime);
            dataset.AddOrUpdate(DicomTag.Laterality, new string[0]);
            dataset.AddOrUpdate(DicomTag.PresentationCreationDate, DicomImageBuilder.Da(context.ContentTime));
            dataset.AddOrUpdate(DicomTag.PresentationCreationTime, DicomImageBuilder.Tm(context.ContentTime));
            dataset.AddOrUpdate(DicomTag.ContentLabel, "DCST4_TEST");
            dataset.AddOrUpdate(DicomTag.ContentDescription, "DCST4 test annotation");
            dataset.AddOrUpdate(DicomTag.ContentCreatorName, "DCST4");

            dataset.AddOrUpdate(new DicomSequence(DicomTag.ReferencedSeriesSequence, new DicomDataset
            {
                { DicomTag.SeriesInstanceUID, source.SeriesInstanceUid },
                new DicomSequence(DicomTag.ReferencedImageSequence, SopReference(source)),
            }));

            dataset.AddOrUpdate(new DicomSequence(DicomTag.DisplayedAreaSelectionSequence, new DicomDataset
            {
                { DicomTag.DisplayedAreaTopLeftHandCorner, 1, 1 },
                { DicomTag.DisplayedAreaBottomRightHandCorner, source.Columns, source.Rows },
                { DicomTag.PresentationSizeMode, "SCALE TO FIT" },
                { DicomTag.PresentationPixelAspectRatio, 1, 1 },
            }));

            dataset.AddOrUpdate(new DicomSequence(DicomTag.GraphicLayerSequence, new DicomDataset
            {
                { DicomTag.GraphicLayer, LayerName },
                { DicomTag.GraphicLayerOrder, 1 },
            }));

            var text = new DicomDataset
            {
                { DicomTag.UnformattedTextValue, "DCST4 TEST" + context.TextSuffix },
                { DicomTag.AnchorPointAnnotationUnits, "PIXEL" },
                { DicomTag.AnchorPoint, 10f, 10f },
                { DicomTag.AnchorPointVisibility, "N" },
            };
            dataset.AddOrUpdate(new DicomSequence(DicomTag.GraphicAnnotationSequence, new DicomDataset
            {
                { DicomTag.GraphicLayer, LayerName },
                new DicomSequence(DicomTag.ReferencedImageSequence, SopReference(source)),
                new DicomSequence(DicomTag.TextObjectSequence, text),
            }));

            dataset.AddOrUpdate(DicomTag.PresentationLUTShape, "IDENTITY");
            dataset.AddOrUpdate(DicomTag.ImageRotation, (ushort)0);
            dataset.AddOrUpdate(DicomTag.ImageHorizontalFlip, "N");
        }
    }

    /// <summary>Encapsulated PDF IOD. The PDF is written here, without dates or a document ID, so the same input gives the same bytes.</summary>
    public sealed class EncapsulatedPdfProfile : ObjectProfile
    {
        public const string DocumentTitle = "DCST4 test document";

        public override string Code => "DOC";
        public override string DisplayName => "Encapsulated PDF";
        public override DicomUID SopClass => DicomUID.EncapsulatedPDFStorage;
        public override ExamPreset Preset { get; } = new ExamPreset { Name = "Document", StudyDescription = "Test document", SeriesDescription = "Document", ProtocolName = "Document", BodyPart = string.Empty };

        public override void AddContent(DicomDataset dataset, ObjectContext context)
        {
            var pdf = BuildPdf(context.InstanceIndex + 1);

            dataset.AddOrUpdate(DicomTag.ConversionType, "WSD");
            dataset.AddOrUpdate(DicomTag.BurnedInAnnotation, "NO");
            dataset.AddOrUpdate(DicomTag.AcquisitionDateTime, DicomImageBuilder.Dt(context.ContentTime));
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ConceptNameCodeSequence));
            dataset.AddOrUpdate(DicomTag.DocumentTitle, DocumentTitle + context.TextSuffix);
            dataset.AddOrUpdate(DicomTag.MIMETypeOfEncapsulatedDocument, "application/pdf");
            dataset.AddOrUpdate(DicomTag.EncapsulatedDocumentLength, (uint)pdf.Length);
            dataset.AddOrUpdate(new DicomOtherByte(DicomTag.EncapsulatedDocument, new MemoryByteBuffer(pdf)));
        }

        /// <summary>A one-page PDF. Latin-1 text only, in the standard Helvetica font, which a PDF reader needs no embedded font for.</summary>
        internal static byte[] BuildPdf(int pageNumber)
        {
            var stream = "BT /F1 18 Tf 72 720 Td (TEST DOCUMENT) Tj ET\n"
                + "BT /F1 12 Tf 72 690 Td (Generated by DCST4. Not a clinical document.) Tj ET\n"
                + "BT /F1 12 Tf 72 672 Td (Instance " + Int(pageNumber) + ") Tj ET\n";

            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
                "<< /Length " + Int(stream.Length) + " >>\nstream\n" + stream + "endstream",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            };

            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new int[objects.Length];
            for (var i = 0; i < objects.Length; i++)
            {
                offsets[i] = pdf.Length;
                pdf.Append(Int(i + 1)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
            }

            var startXref = pdf.Length;
            pdf.Append("xref\n0 ").Append(Int(objects.Length + 1)).Append("\n0000000000 65535 f \n");
            foreach (var offset in offsets) pdf.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            pdf.Append("trailer\n<< /Size ").Append(Int(objects.Length + 1)).Append(" /Root 1 0 R >>\nstartxref\n").Append(Int(startXref)).Append("\n%%EOF\n");

            // DICOM values have even length, and a line break after %%EOF is harmless to a PDF reader.
            if (pdf.Length % 2 != 0) pdf.Append('\n');
            return Encoding.GetEncoding("ISO-8859-1").GetBytes(pdf.ToString());
        }
    }

    /// <summary>RT Structure Set IOD: one closed contour on the source image.</summary>
    public sealed class RtStructureSetProfile : ObjectProfile
    {
        private const int RoiNumber = 1;

        public override string Code => "RTSTRUCT";
        public override string DisplayName => "RT Structure Set";
        public override DicomUID SopClass => DicomUID.RTStructureSetStorage;
        public override bool NeedsSourceImage => true;
        public override ExamPreset Preset { get; } = new ExamPreset { Name = "Structure set", StudyDescription = "Test structure set", SeriesDescription = "Structures", ProtocolName = "Structures", BodyPart = string.Empty };

        public override void AddContent(DicomDataset dataset, ObjectContext context)
        {
            var source = context.Source;
            var contourImage = SopReference(source);

            dataset.AddOrUpdate(DicomTag.OperatorsName, string.Empty);
            dataset.AddOrUpdate(DicomTag.StructureSetLabel, "DCST4 TEST");
            dataset.AddOrUpdate(DicomTag.StructureSetName, "DCST4 test" + context.TextSuffix);
            dataset.Remove(DicomTag.ContentDate, DicomTag.ContentTime);
            dataset.AddOrUpdate(DicomTag.StructureSetDate, DicomImageBuilder.Da(context.ContentTime));
            dataset.AddOrUpdate(DicomTag.StructureSetTime, DicomImageBuilder.Tm(context.ContentTime));

            dataset.AddOrUpdate(new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence, new DicomDataset
            {
                { DicomTag.FrameOfReferenceUID, source.FrameOfReferenceUid },
                new DicomSequence(DicomTag.RTReferencedStudySequence, new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, DicomUID.DetachedStudyManagementRETIRED.UID },
                    { DicomTag.ReferencedSOPInstanceUID, source.StudyInstanceUid },
                    new DicomSequence(DicomTag.RTReferencedSeriesSequence, new DicomDataset
                    {
                        { DicomTag.SeriesInstanceUID, source.SeriesInstanceUid },
                        new DicomSequence(DicomTag.ContourImageSequence, contourImage),
                    }),
                }),
            }));

            dataset.AddOrUpdate(new DicomSequence(DicomTag.StructureSetROISequence, new DicomDataset
            {
                { DicomTag.ROINumber, Int(RoiNumber) },
                { DicomTag.ReferencedFrameOfReferenceUID, source.FrameOfReferenceUid },
                { DicomTag.ROIName, "DCST4 TEST" },
                { DicomTag.ROIGenerationAlgorithm, "MANUAL" },
            }));

            // A square in the plane of the source image.
            var contourData = new[] { "-10", "-10", "0", "10", "-10", "0", "10", "10", "0", "-10", "10", "0" };
            dataset.AddOrUpdate(new DicomSequence(DicomTag.ROIContourSequence, new DicomDataset
            {
                { DicomTag.ReferencedROINumber, Int(RoiNumber) },
                { DicomTag.ROIDisplayColor, "255", "0", "0" },
                new DicomSequence(DicomTag.ContourSequence, new DicomDataset
                {
                    { DicomTag.ContourGeometricType, "CLOSED_PLANAR" },
                    { DicomTag.NumberOfContourPoints, "4" },
                    { DicomTag.ContourData, contourData },
                    new DicomSequence(DicomTag.ContourImageSequence, contourImage),
                }),
            }));

            dataset.AddOrUpdate(new DicomSequence(DicomTag.RTROIObservationsSequence, new DicomDataset
            {
                { DicomTag.ObservationNumber, Int(RoiNumber) },
                { DicomTag.ReferencedROINumber, Int(RoiNumber) },
                { DicomTag.RTROIInterpretedType, "ORGAN" },
                { DicomTag.ROIInterpreter, string.Empty },
            }));
        }
    }
}
