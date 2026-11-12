using DCST4.Core.Generation.Modalities;
using DCST4.Core.Generation.Objects;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Generation.Datasets
{
    /// <summary>
    /// A test-suite dataset: reproducible from a seed, and described by a manifest as it is produced.
    /// Each object comes out in its final transfer syntax, so a run must use <see cref="TestDatasetRequest.TransferSyntax"/>.
    /// </summary>
    public sealed class TestDatasetGenerator : IImageSource
    {
        private const string FileMetaSourceAe = "DCST4";
        private const int MinPatientAge = 18;
        private const int MaxPatientAge = 90;
        private const int SeriesGapSeconds = 30;
        private static readonly TimeSpan ObjectInterval = TimeSpan.FromSeconds(1);

        private readonly TestDatasetRequest _request;
        private readonly DatasetProfile _profile;
        private readonly TestPersonnummerRegistry _registry;
        private readonly DeterministicUidSource _uids;
        private readonly string _context;
        private readonly string _textSuffix;
        private readonly bool _unavailable;

        public TestDatasetGenerator(TestDatasetRequest request)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));
            var problems = request.Validate();
            if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems), nameof(request));

            _profile = request.Profile;
            _registry = request.Registry ?? TestPersonnummerRegistry.Default;
            _context = Context(request);
            _uids = new DeterministicUidSource(request.UidRoot, _context);
            _textSuffix = TextSuffix(request.CharacterSet);

            Manifest = NewManifest();
            _unavailable = RecordIfUnavailable();
        }

        public DatasetManifest Manifest { get; private set; }

        /// <summary>The transfer syntax every object is in.</summary>
        public DicomTransferSyntax TransferSyntax => _request.TransferSyntax;

        /// <summary>True when the encoder cannot produce this combination. Nothing is generated, and the manifest says why.</summary>
        public bool IsUnavailable => _unavailable;

        public int ExpectedCount
        {
            get
            {
                if (_unavailable) return 0;
                var objects = _request.SeriesPerStudy * _request.InstancesPerSeries + (_profile.NeedsSourceImage ? 1 : 0);
                return _request.Patients * _request.StudiesPerPatient * objects;
            }
        }

        public IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken)
        {
            Manifest.Instances.Clear();
            if (_unavailable) yield break;

            var runSeries = 0;
            for (var patientIndex = 0; patientIndex < _request.Patients; patientIndex++)
            {
                var patient = DrawPatient(patientIndex);

                for (var studyIndex = 0; studyIndex < _request.StudiesPerPatient; studyIndex++)
                {
                    var scope = "patient/" + Int(patientIndex) + "/study/" + Int(studyIndex);
                    var uids = _uids.Within(scope);
                    var identity = StudyIdentity(patient, patientIndex, studyIndex, scope, uids);

                    ReferencedImage source = null;
                    if (_profile.NeedsSourceImage)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var image = MakeSourceImage(identity, uids.Within("source-image"));
                        source = Reference(image.Dataset);
                        yield return Finish(image, ++runSeries, 1, ManifestInstance.RoleSourceImage);
                    }

                    var firstSeries = runSeries;
                    runSeries += _request.SeriesPerStudy;

                    var objects = _profile.IsImage
                        ? Images(identity, uids, source, firstSeries, cancellationToken)
                        : Objects(identity, uids, source, firstSeries, cancellationToken);
                    foreach (var finished in objects) yield return finished;
                }
            }
        }

        private IEnumerable<SourceImage> Images(ExamIdentity identity, IUidSource uids, ReferencedImage source, int firstSeries, CancellationToken cancellationToken)
        {
            var request = new ExamRequest
            {
                Modality = _profile.Image,
                Preset = _profile.Preset,
                Identity = identity,
                SeriesCount = _request.SeriesPerStudy,
                ImagesPerSeries = _request.InstancesPerSeries,
                Rows = _request.ResolvedRows,
                Columns = _request.ResolvedColumns,
                BitsStored = _request.ResolvedBits,
                CharacterSet = _request.CharacterSetTag,
                FramesPerImage = _request.ResolvedFrames,
                Uids = uids,
                SeriesNumberOffset = source != null ? 1 : 0,
                SourceImage = source,
                PrivatePayloadBytes = _request.PrivatePayloadBytes,
            };

            foreach (var image in new ExamGenerator(request, () => _request.ReferenceDate).GetImages(cancellationToken))
            {
                yield return Finish(image.File, firstSeries + image.SeriesNumber, image.InstanceNumber, ManifestInstance.RoleTarget);
            }
        }

        private IEnumerable<SourceImage> Objects(ExamIdentity identity, IUidSource uids, ReferencedImage source, int firstSeries, CancellationToken cancellationToken)
        {
            var request = new ExamRequest
            {
                Preset = _profile.Preset,
                Identity = identity,
                CharacterSet = _request.CharacterSetTag,
                Uids = uids,
                SeriesNumberOffset = source != null ? 1 : 0,
            };

            for (var seriesIndex = 0; seriesIndex < _request.SeriesPerStudy; seriesIndex++)
            {
                var seriesTime = identity.StudyDateTime + TimeSpan.FromSeconds(SeriesGapSeconds * seriesIndex);
                var series = new SeriesContext
                {
                    SeriesIndex = seriesIndex,
                    SeriesInstanceUid = uids.Uid("series/" + Int(seriesIndex)),
                    SeriesDescription = identity.SeriesDescription,
                    StudyTime = identity.StudyDateTime,
                    SeriesTime = seriesTime,
                };

                for (var instanceIndex = 0; instanceIndex < _request.InstancesPerSeries; instanceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dataset = _profile.Object.Build(new ObjectContext
                    {
                        Request = request,
                        Series = series,
                        InstanceIndex = instanceIndex,
                        ContentTime = seriesTime + TimeSpan.FromTicks(ObjectInterval.Ticks * instanceIndex),
                        Source = source,
                        TextSuffix = _textSuffix,
                    });
                    yield return Finish(new DicomFile(dataset), firstSeries + seriesIndex + 1, instanceIndex + 1, ManifestInstance.RoleTarget);
                }
            }
        }

        /// <summary>One small CT image for the object to refer to. It is in the same study and has Series Number 1.</summary>
        private DicomFile MakeSourceImage(ExamIdentity identity, IUidSource uids)
        {
            var ct = ModalityCatalog.Find("CT");
            var sourceIdentity = identity.WithSeriesDescription("Referenced image" + _textSuffix);
            var request = new ExamRequest
            {
                Modality = ct,
                Preset = ct.Presets[0],
                Identity = sourceIdentity,
                Rows = _request.ResolvedRows,
                Columns = _request.ResolvedColumns,
                BitsStored = ct.DefaultBitDepth,
                CharacterSet = _request.CharacterSetTag,
                Uids = uids,
            };
            return new ExamGenerator(request, () => _request.ReferenceDate).GetImages(CancellationToken.None).Single().File;
        }

        private static ReferencedImage Reference(DicomDataset dataset) => new ReferencedImage
        {
            SopClassUid = dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
            SopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
            SeriesInstanceUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID),
            StudyInstanceUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
            FrameOfReferenceUid = dataset.GetSingleValueOrDefault<string>(DicomTag.FrameOfReferenceUID, null),
            Rows = dataset.GetSingleValue<ushort>(DicomTag.Rows),
            Columns = dataset.GetSingleValue<ushort>(DicomTag.Columns),
        };

        /// <summary>Puts an object in its final form and records it. The frames are hashed here, before any encoder has touched them.</summary>
        private SourceImage Finish(DicomFile file, int runSeries, int instanceNumber, string role)
        {
            if (role == ManifestInstance.RoleTarget && _request.EdgeCases != EdgeCases.None)
            {
                EdgeCaseVariants.Apply(file.Dataset, _request.EdgeCases, _textSuffix);
            }

            var frameHashes = ManifestHashes.FrameHashes(file.Dataset);
            var finished = TransferSyntaxConverter.Convert(file, _request.TransferSyntax);

            // fo-dicom puts the computer's name here. The file meta header of a converted file is new, so this comes last.
            finished.FileMetaInfo.SourceApplicationEntityTitle = FileMetaSourceAe;

            var dataset = finished.Dataset;
            Manifest.Instances.Add(new ManifestInstance
            {
                Role = role,
                SopClassUid = dataset.GetSingleValue<string>(DicomTag.SOPClassUID),
                SopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID),
                StudyInstanceUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
                SeriesInstanceUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID),
                TransferSyntaxUid = dataset.InternalTransferSyntax.UID.UID,
                FileSha256 = ManifestHashes.FileHash(finished),
                FrameSha256 = frameHashes,
                NormalizedAttributesSha256 = NormalizedAttributes.Hash(dataset),
            });

            return new SourceImage(finished, runSeries, instanceNumber);
        }

        // ---------------------------------------------------------------- identity

        private sealed class Patient
        {
            public string Name;
            public string Id;
            public string BirthDate;
            public string Sex;
        }

        /// <summary>Follows from the seed and position only, so that the profiles of one seed share their patients.</summary>
        private Patient DrawPatient(int index)
        {
            var scope = "seed/" + Int(_request.Seed) + "/patient/" + Int(index);
            var ct = ModalityCatalog.Find("CT");
            var drawn = new IdentityGenerator(_registry, SeededValues.Random(scope), () => _request.ReferenceDate, new DeterministicUidSource(null, scope))
                .Generate(null, ct, ct.Presets[0], MinPatientAge, MaxPatientAge);

            return new Patient
            {
                Id = drawn.PatientId,
                BirthDate = drawn.PatientBirthDate,
                Sex = drawn.PatientSex,
                Name = PatientName(drawn, scope),
            };
        }

        private string PatientName(ExamIdentity drawn, string scope)
        {
            var random = SeededValues.Random(scope + "/name");
            var withLetters = _request.CharacterSet != CharacterSetVariant.Default;
            string name;
            if (_request.PatientNaming == TestPatientNaming.MarkedTestName)
            {
                name = TestPatientNames.Marked(random) + (withLetters ? " " + TestPatientNames.SwedishLetters : string.Empty);
            }
            else if (withLetters)
            {
                name = SwedishNames.PickWithSwedishCharacters(random, drawn.PatientSex) + "^" + TestPatientNames.SwedishLetters;
            }
            else
            {
                name = drawn.PatientName;
            }
            return _request.CharacterSet == CharacterSetVariant.EmptyAscii ? SwedishNames.ToAscii(name) : name;
        }

        private ExamIdentity StudyIdentity(Patient patient, int patientIndex, int studyIndex, string scope, IUidSource uids)
        {
            var preset = _profile.Preset;
            var ct = ModalityCatalog.Find("CT");
            var overrides = new IdentityOverrides
            {
                PatientName = patient.Name,
                PatientId = patient.Id,
                PatientBirthDate = patient.BirthDate,
                PatientSex = patient.Sex,
                StudyDateTime = _request.ReferenceDate.AddDays(-studyIndex).AddMinutes(-patientIndex),
                StudyDescription = (preset.StudyDescription ?? string.Empty) + _textSuffix,
                SeriesDescription = (preset.SeriesDescription ?? string.Empty) + _textSuffix,
                StationName = "DCST4-" + (_profile.Image?.Code ?? _profile.Object.Code),
                AutoValuesInBasicAscii = _request.CharacterSet == CharacterSetVariant.EmptyAscii,
            };

            return new IdentityGenerator(_registry, SeededValues.Random(_context + "/" + scope), () => _request.ReferenceDate, uids)
                .Generate(overrides, ct, preset);
        }

        private static string TextSuffix(CharacterSetVariant variant)
        {
            switch (variant)
            {
                case CharacterSetVariant.EmptyAscii: return " " + SwedishNames.ToAscii(TestPatientNames.SwedishLetters);
                case CharacterSetVariant.Latin1Swedish:
                case CharacterSetVariant.Utf8Swedish: return " " + TestPatientNames.SwedishLetters;
                default: return string.Empty;
            }
        }

        /// <summary>Everything that makes this dataset differ from another, since no two may share a SOP Instance UID.</summary>
        private static string Context(TestDatasetRequest request) => string.Join("|", new[]
        {
            "seed=" + Int(request.Seed),
            "profile=" + request.Profile.Id,
            "reference=" + request.ReferenceDate.ToString("O", CultureInfo.InvariantCulture),
            "naming=" + request.PatientNaming,
            "matrix=" + Int(request.ResolvedRows) + "x" + Int(request.ResolvedColumns),
            "bits=" + Int(request.ResolvedBits),
            "frames=" + Int(request.ResolvedFrames),
            "syntax=" + request.TransferSyntax.UID.UID,
            "charset=" + request.CharacterSet,
            "edge=" + (int)request.EdgeCases + (request.PrivatePayloadBytes > 0 ? "/payload=" + request.PrivatePayloadBytes.ToString(CultureInfo.InvariantCulture) : string.Empty),
            "counts=" + Int(request.Patients) + "/" + Int(request.StudiesPerPatient) + "/" + Int(request.SeriesPerStudy) + "/" + Int(request.InstancesPerSeries),
        });

        // ---------------------------------------------------------------- manifest

        private DatasetManifest NewManifest() => new DatasetManifest
        {
            Tools = ManifestTools.Current(),
            RegistrySource = _registry.SourceDescription,
            Request = new ManifestRequest
            {
                ProfileId = _profile.Id,
                Seed = _request.Seed,
                UidRoot = string.IsNullOrWhiteSpace(_request.UidRoot) ? DeterministicUidSource.UuidRoot : _request.UidRoot.Trim(),
                ReferenceDate = _request.ReferenceDate.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                PatientNaming = _request.PatientNaming.ToString(),
                Patients = _request.Patients,
                StudiesPerPatient = _request.StudiesPerPatient,
                SeriesPerStudy = _request.SeriesPerStudy,
                InstancesPerSeries = _request.InstancesPerSeries,
                Rows = _profile.IsImage ? _request.ResolvedRows : 0,
                Columns = _profile.IsImage ? _request.ResolvedColumns : 0,
                BitsStored = _request.ResolvedBits,
                Frames = _request.ResolvedFrames,
                TransferSyntaxUid = _request.TransferSyntax.UID.UID,
                CharacterSetVariant = _request.CharacterSet.ToString(),
                EdgeCases = _request.EdgeCases.ToString(),
            },
        };

        /// <summary>Tried once before anything is generated, so that an encoder that cannot do this is a report and not a failed run.</summary>
        private bool RecordIfUnavailable()
        {
            if (!_request.TransferSyntax.IsEncapsulated) return false;

            var choice = CompressionCatalog.For(_request.TransferSyntax);
            var samples = _profile.Image.SamplesPerPixel;
            var probe = choice != null
                ? CompressionCatalog.Probe(choice, _request.ResolvedBits, samples)
                : new CodecProbeResult { Reason = "This transfer syntax is not one the datasets can be generated in." };
            if (probe.Available) return false;

            Manifest.Unavailable.Add(new ManifestUnavailable
            {
                ProfileId = _profile.Id,
                TransferSyntaxUid = _request.TransferSyntax.UID.UID,
                BitsStored = _request.ResolvedBits,
                SamplesPerPixel = samples,
                Reason = probe.Reason,
            });
            return true;
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
