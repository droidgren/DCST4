using System;
using System.Threading.Tasks;

namespace DCST4.Core.Generation.Realism
{
    /// <summary>
    /// A frame that compresses like a real one: the modality's shape, tissue texture under it, and the modality's noise, at the
    /// full bit depth. The overlays (the TEST IMAGE watermark, the text box, the gray bar, the colour patches and the frame
    /// number) are drawn on top without noise, as burned-in annotations are on real equipment. Pixels the overlays cover
    /// completely are the same as in the flat pattern.
    /// </summary>
    internal static class RealisticComposer
    {
        /// <summary>The mean of the Rayleigh envelope of two standard normal values, √(π/2).</summary>
        private const double RayleighMean = 1.2533141373155003;
        /// <summary>Radiograph and quantum noise: the electronic floor, as a fraction of full scale.</summary>
        private const double ElectronicFloor = 0.003;
        /// <summary>Far enough along the texture's depth that two multi-frame images of a series do not look alike.</summary>
        private const double InstanceDepthGap = 7.31;
        /// <summary>The HU of air, a little above the lowest stored value so that noise is not cut off there.</summary>
        private const double AirHu = -1000;
        private const double CtStoredOffset = 1024;

        /// <summary><paramref name="overlay"/> is the overlays as 32-bit BGRA, row by row with no padding.</summary>
        public static RenderedPixels Render(PixelRenderSpec spec, byte[] overlay)
        {
            var realism = spec.Realism;
            var profile = realism.Profile;
            var factor = RealismLevels.NoiseFactor(realism.Level);
            var width = spec.Columns;
            var height = spec.Rows;

            var seriesSeed = StableSeed.Of("realism/" + realism.SeriesSeed);
            var imageSeed = StableSeed.Mix(seriesSeed, realism.Instance + 1);
            var frameSeed = StableSeed.Mix(imageSeed, -(realism.Frame + 1L));
            var multiFrame = realism.Frames > 1;

            var depth = multiFrame ? realism.Instance * InstanceDepthGap + realism.Frame * profile.TextureStep : realism.Instance * profile.TextureStep;
            var texture = TissueTexture.Create(width, height, seriesSeed, depth);

            var signal = new float[width * height];
            var inside = new bool[width * height];
            DrawShape(spec, profile, texture, signal, inside);
            texture = null;

            Func<int, float[]> noise = index =>
            {
                if (multiFrame && profile.TemporalPersistence > 0)
                {
                    // Part of the noise is the image's own and stays from frame to frame, the rest is new in each frame.
                    var kept = RandomField.Gaussian(width, height, StableSeed.Mix(imageSeed, 100 + index), profile.CorrelationX, profile.CorrelationY);
                    var fresh = RandomField.Gaussian(width, height, StableSeed.Mix(frameSeed, 100 + index), profile.CorrelationX, profile.CorrelationY);
                    var a = (float)Math.Sqrt(profile.TemporalPersistence);
                    var b = (float)Math.Sqrt(1 - profile.TemporalPersistence);
                    for (var i = 0; i < kept.Length; i++) kept[i] = a * kept[i] + b * fresh[i];
                    return kept;
                }
                return RandomField.Gaussian(width, height, StableSeed.Mix(multiFrame ? frameSeed : imageSeed, 100 + index), profile.CorrelationX, profile.CorrelationY);
            };

            AddNoise(spec, profile, factor, signal, inside, noise, multiFrame ? frameSeed : imageSeed);
            return Compose(spec, signal, inside, overlay);
        }

        // ---------------------------------------------------------------- shapes

        private static void DrawShape(PixelRenderSpec spec, RealismProfile profile, float[] texture, float[] signal, bool[] inside)
        {
            var width = spec.Columns;
            var height = spec.Rows;
            var size = Math.Min(width, height);
            var centerX = width / 2.0;
            var centerY = height / 2.0;

            // The disc of the flat pattern, kept as an organ: its size still follows the position in the stack.
            var position = spec.ImagesInSeries > 1 ? 2.0 * spec.ImageIndex / (spec.ImagesInSeries - 1) - 1.0 : 0.0;
            var discRadius = size * 0.32 * Math.Sqrt(Math.Max(0.02, 1 - position * position));
            var maxStored = spec.MaxStoredValue;

            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var t = texture[i];
                    var dx = x + 0.5 - centerX;
                    var dy = y + 0.5 - centerY;
                    var disc = Edge(discRadius - Math.Sqrt(dx * dx + dy * dy));
                    double value;
                    var isInside = true;

                    switch (profile.Shape)
                    {
                        case ImageShape.UltrasoundFan:
                        {
                            var apexY = -0.06 * height;
                            var ay = y + 0.5 - apexY;
                            var radius = Math.Sqrt(dx * dx + ay * ay);
                            var angle = Math.Atan2(dx, ay);
                            var nearest = 0.1 * height;
                            var farthest = 1.04 * height;
                            isInside = radius >= nearest && radius <= farthest && Math.Abs(angle) <= 0.62;
                            var tissueDepth = (radius - nearest) / (farthest - nearest);
                            var tissue = (0.34 + 0.16 * t) * (1 - 0.45 * tissueDepth);
                            // A cyst is dark: it gives no echoes.
                            value = Mix(tissue, 0.07 + 0.03 * t, disc);
                            break;
                        }
                        case ImageShape.CtFieldOfView:
                        {
                            var fov = size / 2.0;
                            isInside = dx * dx + dy * dy <= fov * fov;
                            var body = Ellipse(dx, dy, 0.86 * fov, 0.68 * fov, out var fromEdge);
                            var tissueHu = fromEdge < 0.07 * fov ? -90 + 15 * t : 45 + 25 * t;
                            var hu = Mix(AirHu, tissueHu, body);
                            hu = Mix(hu, 130 + 15 * t, disc * body);
                            value = (hu + CtStoredOffset) / maxStored;
                            break;
                        }
                        case ImageShape.Body:
                        {
                            var body = Ellipse(dx, dy, 0.42 * width, 0.40 * height, out var fromEdge);
                            var edge = 0.05 * size;
                            if (profile.Noise == NoiseModel.Poisson)
                            {
                                // Uptake: little in the body, much in the organ.
                                value = Mix(Mix(0.002, 0.10 + 0.03 * t, body), 0.65 + 0.1 * t, disc * body);
                            }
                            else
                            {
                                var tissue = fromEdge < edge ? 0.65 + 0.05 * t : 0.32 + 0.10 * t;
                                value = Mix(Mix(0, tissue, body), 0.5 + 0.08 * t, disc * body);
                            }
                            break;
                        }
                        case ImageShape.Radiograph:
                        {
                            var collimated = x < 0.04 * width || x >= 0.96 * width || y < 0.04 * height || y >= 0.96 * height;
                            var body = Ellipse(dx, dy, 0.36 * width, 0.47 * height, out _);
                            value = collimated ? 0.9 : Mix(Mix(0.07, 0.42 + 0.14 * t, body), 0.72 + 0.06 * t, disc * body);
                            break;
                        }
                        case ImageShape.Breast:
                        {
                            var bx = x + 0.5;
                            var body = Ellipse(bx, dy, 0.70 * width, 0.46 * height, out var fromEdge);
                            isInside = body > 0;
                            var tissue = fromEdge < 0.01 * size ? 0.55 : 0.38 + 0.22 * t;
                            value = Mix(Mix(0, tissue, body), 0.72 + 0.05 * t, disc * body);
                            break;
                        }
                        default:
                        {
                            var field = 0.48 * size;
                            isInside = dx * dx + dy * dy <= field * field;
                            // Contrast medium makes the organ darker.
                            value = Mix(0.58 + 0.14 * t, 0.28 + 0.05 * t, disc);
                            break;
                        }
                    }

                    inside[i] = isInside;
                    signal[i] = isInside ? (float)Clamp01(value) : 0f;
                }
            });
        }

        /// <summary>1 inside the ellipse and 0 outside, with an edge one pixel wide. <paramref name="fromEdge"/> is about the distance inwards, in pixels.</summary>
        private static double Ellipse(double dx, double dy, double a, double b, out double fromEdge)
        {
            var scaled = Math.Sqrt(dx * dx / (a * a) + dy * dy / (b * b));
            fromEdge = (1 - scaled) * Math.Min(a, b);
            return Edge(fromEdge);
        }

        private static double Edge(double inwards) => inwards >= 0.5 ? 1 : inwards <= -0.5 ? 0 : inwards + 0.5;

        private static double Mix(double outside, double inside, double weight) => outside + (inside - outside) * weight;

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

        // ---------------------------------------------------------------- noise

        private static void AddNoise(PixelRenderSpec spec, RealismProfile profile, double factor, float[] signal, bool[] inside, Func<int, float[]> noise, ulong countSeed)
        {
            var width = spec.Columns;
            var height = spec.Rows;

            switch (profile.Noise)
            {
                case NoiseModel.Speckle:
                {
                    var real = noise(0);
                    var imaginary = noise(1);
                    var keep = profile.Strength * factor;
                    Parallel.For(0, height, y =>
                    {
                        for (var i = y * width; i < (y + 1) * width; i++)
                        {
                            if (!inside[i]) continue;
                            var speckle = Math.Sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]) / RayleighMean;
                            signal[i] = (float)(signal[i] * Math.Max(0, 1 + keep * (speckle - 1)));
                        }
                    });
                    break;
                }
                case NoiseModel.Quantum:
                {
                    var field = noise(0);
                    Parallel.For(0, height, y =>
                    {
                        for (var i = y * width; i < (y + 1) * width; i++)
                        {
                            if (!inside[i]) continue;
                            var sigma = factor * (ElectronicFloor + profile.Strength * Math.Sqrt(signal[i]));
                            signal[i] = (float)(signal[i] + sigma * field[i]);
                        }
                    });
                    break;
                }
                case NoiseModel.CtGaussian:
                {
                    var field = noise(0);
                    var sigma = (float)(factor * profile.Strength / spec.MaxStoredValue);
                    Parallel.For(0, height, y =>
                    {
                        for (var i = y * width; i < (y + 1) * width; i++)
                        {
                            if (inside[i]) signal[i] += sigma * field[i];
                        }
                    });
                    break;
                }
                case NoiseModel.Rician:
                {
                    var real = noise(0);
                    var imaginary = noise(1);
                    var sigma = factor * profile.Strength;
                    Parallel.For(0, height, y =>
                    {
                        for (var i = y * width; i < (y + 1) * width; i++)
                        {
                            if (!inside[i]) continue;
                            var re = signal[i] + sigma * real[i];
                            var im = sigma * imaginary[i];
                            signal[i] = (float)Math.Sqrt(re * re + im * im);
                        }
                    });
                    break;
                }
                case NoiseModel.Poisson:
                {
                    var counts = profile.Strength / (factor * factor);
                    Parallel.For(0, height, y =>
                    {
                        var random = new SplitMix64(StableSeed.Mix(countSeed, 1000000 + y));
                        for (var i = y * width; i < (y + 1) * width; i++)
                        {
                            if (inside[i]) signal[i] = (float)(random.NextPoisson(signal[i] * counts) / counts);
                        }
                    });
                    // The reconstruction filter smooths the counts.
                    RandomField.Blur(signal, width, height, profile.CorrelationX, profile.CorrelationY);
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- overlays and samples

        private static RenderedPixels Compose(PixelRenderSpec spec, float[] signal, bool[] inside, byte[] overlay)
        {
            var width = spec.Columns;
            var height = spec.Rows;
            var max = spec.MaxStoredValue;
            var samples = spec.SamplesPerPixel > 1 ? 3 : 1;
            var bytesPerSample = samples == 1 && spec.BitsAllocated > 8 ? 2 : 1;
            var data = new byte[width * height * samples * bytesPerSample];
            var rowMin = new int[height];
            var rowMax = new int[height];

            Parallel.For(0, height, y =>
            {
                var low = int.MaxValue;
                var high = int.MinValue;
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var o = i * 4;
                    var alpha = overlay[o + 3];
                    for (var channel = 0; channel < samples; channel++)
                    {
                        // BGRA: red is at +2. A grayscale image takes the green channel, as the flat pattern does.
                        int level = samples == 1 ? overlay[o + 1] : overlay[o + 2 - channel];
                        int value;
                        if (alpha == byte.MaxValue)
                        {
                            value = (level * max + 127) / 255;
                        }
                        else
                        {
                            var a = alpha / 255.0;
                            var under = inside[i] ? Clamp01(signal[i]) : 0;
                            value = (int)((under * (1 - a) + level / 255.0 * a) * max + 0.5);
                            if (value > max) value = max;
                        }
                        if (value < low) low = value;
                        if (value > high) high = value;

                        var target = (i * samples + channel) * bytesPerSample;
                        data[target] = (byte)value;
                        if (bytesPerSample == 2) data[target + 1] = (byte)(value >> 8);
                    }
                }
                rowMin[y] = low;
                rowMax[y] = high;
            });

            var min = int.MaxValue;
            var maxValue = int.MinValue;
            for (var y = 0; y < height; y++)
            {
                min = Math.Min(min, rowMin[y]);
                maxValue = Math.Max(maxValue, rowMax[y]);
            }
            return new RenderedPixels { Data = data, MinValue = samples > 1 ? 0 : min, MaxValue = samples > 1 ? byte.MaxValue : maxValue };
        }
    }
}
