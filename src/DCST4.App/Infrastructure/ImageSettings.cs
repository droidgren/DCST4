using DCST4.Core.Pipeline;
using DCST4.Properties;
using System.Globalization;

namespace DCST4.Infrastructure
{
    /// <summary>The realism level and lossy compression settings, as the Settings dialog saves them.</summary>
    internal static class ImageSettings
    {
        /// <summary>Null when every encoder is at its default. A saved value that cannot be read counts as the default.</summary>
        public static LossyCompressionSettings Lossy(Settings settings)
        {
            var lossy = new LossyCompressionSettings
            {
                JpegQuality = settings.JpegQuality > 0 ? settings.JpegQuality : (int?)null,
                Jpeg2000Ratio = settings.Jpeg2000Ratio > 0 ? settings.Jpeg2000Ratio : (int?)null,
                JpegLsNear = settings.JpegLsNear > 0 ? settings.JpegLsNear : (int?)null,
                H264Mbps = ParseMbps(settings.H264Mbps),
            };
            return lossy.IsDefault ? null : lossy;
        }

        /// <summary>A number with a dot or a comma. Null for an empty or unreadable value.</summary>
        public static double? ParseMbps(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null;
        }

        public static string FormatMbps(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
