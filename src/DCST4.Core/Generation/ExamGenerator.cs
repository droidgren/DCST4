using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DCST4.Core.Generation
{
    /// <summary>Generates all images of one exam, one at a time. Knows nothing about sending.</summary>
    public sealed class ExamGenerator : IImageSource
    {
        private static readonly TimeSpan SeriesGap = TimeSpan.FromSeconds(30);

        private readonly ExamRequest _request;
        private readonly Func<DateTime> _clock;

        public ExamGenerator(ExamRequest request, Func<DateTime> clock = null)
        {
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _clock = clock ?? (() => DateTime.Now);
        }

        public int ExpectedCount => _request.TotalImages;

        public IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken)
        {
            var request = _request;
            var startTime = ExamStart();

            for (var seriesIndex = 0; seriesIndex < request.SeriesCount; seriesIndex++)
            {
                var seriesTime = startTime + TimeSpan.FromTicks((SeriesGap + SeriesDuration).Ticks * seriesIndex);
                var series = new SeriesContext
                {
                    SeriesIndex = seriesIndex,
                    SeriesInstanceUid = request.Identity.SeriesInstanceUid ?? IdentifierGenerator.Uid(),
                    FrameOfReferenceUid = IdentifierGenerator.Uid(),
                    SeriesDescription = SeriesDescription(seriesIndex),
                    StudyTime = startTime,
                    SeriesTime = seriesTime,
                };

                for (var imageIndex = 0; imageIndex < request.ImagesPerSeries; imageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var contentTime = seriesTime + TimeSpan.FromTicks(request.Modality.InstanceInterval.Ticks * imageIndex);
                    var dataset = DicomImageBuilder.Build(request, series, imageIndex, contentTime);
                    yield return new SourceImage(new DicomFile(dataset), seriesIndex + 1, imageIndex + 1);
                }
            }
        }

        private TimeSpan SeriesDuration => TimeSpan.FromTicks(_request.Modality.InstanceInterval.Ticks * Math.Max(0, _request.ImagesPerSeries - 1));

        /// <summary>
        /// Auto: the exam ends now, as if the images had just been acquired, so no timestamp lies in the future.
        /// Manual: the exam starts at the entered time.
        /// </summary>
        private DateTime ExamStart()
        {
            var identity = _request.Identity;
            if (!identity.StudyDateTimeIsAuto) return identity.StudyDateTime;
            var duration = TimeSpan.FromTicks(SeriesGap.Ticks * (_request.SeriesCount - 1) + SeriesDuration.Ticks * _request.SeriesCount);
            return _clock() - duration;
        }

        private string SeriesDescription(int seriesIndex)
        {
            var description = _request.Identity.SeriesDescription;
            if (_request.SeriesCount <= 1 || string.IsNullOrEmpty(description)) return description;
            var suffix = " #" + (seriesIndex + 1);
            return description.Length + suffix.Length <= 64 ? description + suffix : description;
        }
    }
}
