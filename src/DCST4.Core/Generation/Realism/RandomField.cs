using System;
using System.Text;
using System.Threading.Tasks;

namespace DCST4.Core.Generation.Realism
{
    /// <summary>Seeds that are the same on every computer and in every run, unlike <see cref="string.GetHashCode"/>.</summary>
    internal static class StableSeed
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;
        private const ulong Golden = 0x9E3779B97F4A7C15UL;

        /// <summary>FNV-1a over the UTF-8 bytes, then mixed.</summary>
        public static ulong Of(string text)
        {
            var hash = FnvOffset;
            foreach (var b in Encoding.UTF8.GetBytes(text ?? string.Empty))
            {
                hash ^= b;
                hash *= FnvPrime;
            }
            return Finalize(hash);
        }

        public static ulong Mix(ulong seed, long value) => Finalize(seed ^ Finalize((ulong)value * Golden + Golden));

        /// <summary>The output step of SplitMix64.</summary>
        public static ulong Finalize(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>SplitMix64: small, fast, and good enough for image noise. Each row of a field has its own, so threads cannot change the result.</summary>
    internal struct SplitMix64
    {
        private ulong _state;
        private bool _hasSpare;
        private double _spare;

        public SplitMix64(ulong seed)
        {
            _state = seed;
            _hasSpare = false;
            _spare = 0;
        }

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            return StableSeed.Finalize(_state);
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Standard normal, by the polar method.</summary>
        public double NextGaussian()
        {
            if (_hasSpare)
            {
                _hasSpare = false;
                return _spare;
            }
            double u, v, s;
            do
            {
                u = 2 * NextDouble() - 1;
                v = 2 * NextDouble() - 1;
                s = u * u + v * v;
            }
            while (s >= 1 || s == 0);
            var factor = Math.Sqrt(-2 * Math.Log(s) / s);
            _spare = v * factor;
            _hasSpare = true;
            return u * factor;
        }

        /// <summary>Knuth's method for few counts, the normal approximation above that.</summary>
        public int NextPoisson(double mean)
        {
            if (mean <= 0) return 0;
            if (mean >= 30) return Math.Max(0, (int)Math.Round(mean + Math.Sqrt(mean) * NextGaussian()));
            var limit = Math.Exp(-mean);
            var count = 0;
            var product = NextDouble();
            while (product > limit)
            {
                count++;
                product *= NextDouble();
            }
            return count;
        }
    }

    /// <summary>Fields of random values the size of an image, the same for the same seed whatever the number of threads.</summary>
    internal static class RandomField
    {
        /// <summary>Three box passes come close to a Gaussian filter.</summary>
        private const int BlurPasses = 3;
        /// <summary>Columns blurred together, so that the vertical pass reads memory row by row.</summary>
        private const int ColumnBlock = 64;

        /// <summary>Standard normal values, correlated by a blur of the given radii and scaled back to a variance of 1.</summary>
        public static float[] Gaussian(int width, int height, ulong seed, int radiusX, int radiusY)
        {
            var field = new float[width * height];
            Parallel.For(0, height, y =>
            {
                var random = new SplitMix64(StableSeed.Mix(seed, y));
                var row = y * width;
                for (var x = 0; x < width; x++) field[row + x] = (float)random.NextGaussian();
            });

            if (radiusX <= 0 && radiusY <= 0) return field;
            Blur(field, width, height, radiusX, radiusY);
            var scale = (float)(1 / Math.Sqrt(VarianceLeft(radiusX) * VarianceLeft(radiusY)));
            Parallel.For(0, height, y =>
            {
                var row = y * width;
                for (var x = 0; x < width; x++) field[row + x] *= scale;
            });
            return field;
        }

        /// <summary>Three box passes along rows and three along columns, with the edge pixels repeated beyond the edge.</summary>
        public static void Blur(float[] field, int width, int height, int radiusX, int radiusY)
        {
            if (radiusX > 0)
            {
                Parallel.For(0, height, () => new float[width], (y, state, scratch) =>
                {
                    for (var pass = 0; pass < BlurPasses; pass++) BlurRow(field, y * width, width, radiusX, scratch);
                    return scratch;
                }, scratch => { });
            }
            if (radiusY > 0)
            {
                var blocks = (width + ColumnBlock - 1) / ColumnBlock;
                Parallel.For(0, blocks, block =>
                {
                    var first = block * ColumnBlock;
                    var count = Math.Min(ColumnBlock, width - first);
                    var copy = new float[height * count];
                    for (var pass = 0; pass < BlurPasses; pass++) BlurColumns(field, width, height, first, count, radiusY, copy);
                });
            }
        }

        private static void BlurRow(float[] field, int start, int width, int radius, float[] scratch)
        {
            Array.Copy(field, start, scratch, 0, width);
            var window = 2 * radius + 1;
            double sum = 0;
            for (var i = -radius; i <= radius; i++) sum += scratch[Clamp(i, width)];
            for (var x = 0; x < width; x++)
            {
                field[start + x] = (float)(sum / window);
                sum += scratch[Clamp(x + radius + 1, width)] - scratch[Clamp(x - radius, width)];
            }
        }

        private static void BlurColumns(float[] field, int width, int height, int first, int count, int radius, float[] copy)
        {
            for (var y = 0; y < height; y++) Array.Copy(field, y * width + first, copy, y * count, count);
            var window = 2 * radius + 1;
            var sums = new double[count];
            for (var i = -radius; i <= radius; i++)
            {
                var row = Clamp(i, height) * count;
                for (var c = 0; c < count; c++) sums[c] += copy[row + c];
            }
            for (var y = 0; y < height; y++)
            {
                var target = y * width + first;
                var add = Clamp(y + radius + 1, height) * count;
                var remove = Clamp(y - radius, height) * count;
                for (var c = 0; c < count; c++)
                {
                    field[target + c] = (float)(sums[c] / window);
                    sums[c] += copy[add + c] - copy[remove + c];
                }
            }
        }

        private static int Clamp(int index, int length) => index < 0 ? 0 : index >= length ? length - 1 : index;

        /// <summary>The variance left of white noise after the passes along one axis: the sum of the squared weights of the combined filter.</summary>
        internal static double VarianceLeft(int radius)
        {
            if (radius <= 0) return 1;
            var box = 2 * radius + 1;
            var kernel = new double[] { 1 };
            for (var pass = 0; pass < BlurPasses; pass++)
            {
                var next = new double[kernel.Length + box - 1];
                for (var i = 0; i < kernel.Length; i++)
                {
                    for (var j = 0; j < box; j++) next[i + j] += kernel[i] / box;
                }
                kernel = next;
            }
            var sum = 0.0;
            foreach (var weight in kernel) sum += weight * weight;
            return sum;
        }
    }
}
