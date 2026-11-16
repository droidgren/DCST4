using System;
using System.Threading.Tasks;

namespace DCST4.Core.Generation.Realism
{
    /// <summary>
    /// Smooth variation at several scales, the way soft tissue is never one flat gray. Value noise: random values on a coarse
    /// grid, blended smoothly, with each finer octave half as strong (about 1/f). The grids have a third axis along the
    /// stack, so that neighbouring slices or frames look alike.
    /// </summary>
    internal static class TissueTexture
    {
        private const int Octaves = 5;
        private const double SmallestCell = 3;

        /// <summary>Values between -1 and 1. <paramref name="depth"/> is the position along the stack, in cells of the coarsest grid.</summary>
        public static float[] Create(int width, int height, ulong seed, double depth)
        {
            var texture = new float[width * height];
            var coarsest = Math.Max(8.0, Math.Min(width, height) / 3.0);
            var total = 0.0;

            for (var octave = 0; octave < Octaves; octave++)
            {
                var cell = coarsest / (1 << octave);
                if (cell < SmallestCell) break;
                var amplitude = 1.0 / (1 << octave);
                total += amplitude;
                AddOctave(texture, width, height, StableSeed.Mix(seed, octave), cell, depth * (1 << octave), (float)amplitude);
            }

            // Interpolated uniform values seldom reach their extremes, so the sum is stretched to use most of -1..1.
            var scale = (float)(1.8 / total);
            Parallel.For(0, height, y =>
            {
                var row = y * width;
                for (var x = 0; x < width; x++) texture[row + x] = Math.Max(-1f, Math.Min(1f, texture[row + x] * scale));
            });
            return texture;
        }

        private static void AddOctave(float[] texture, int width, int height, ulong seed, double cell, double depth, float amplitude)
        {
            var gridWidth = (int)Math.Ceiling(width / cell) + 2;
            var gridHeight = (int)Math.Ceiling(height / cell) + 2;
            var layer = (long)Math.Floor(depth);
            var between = Fade(depth - layer);

            var grid = new float[gridWidth * gridHeight];
            for (var gy = 0; gy < gridHeight; gy++)
            {
                for (var gx = 0; gx < gridWidth; gx++)
                {
                    var near = Value(seed, gx, gy, layer);
                    var far = Value(seed, gx, gy, layer + 1);
                    grid[gy * gridWidth + gx] = (float)(near + (far - near) * between);
                }
            }

            // Where each column falls between grid points is the same on every row.
            var columnCell = new int[width];
            var columnWeight = new float[width];
            for (var x = 0; x < width; x++)
            {
                var position = x / cell;
                columnCell[x] = (int)position;
                columnWeight[x] = (float)Fade(position - columnCell[x]);
            }

            Parallel.For(0, height, y =>
            {
                var position = y / cell;
                var gy = (int)position;
                var wy = (float)Fade(position - gy);
                var top = gy * gridWidth;
                var bottom = top + gridWidth;
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var gx = columnCell[x];
                    var wx = columnWeight[x];
                    var upper = grid[top + gx] + (grid[top + gx + 1] - grid[top + gx]) * wx;
                    var lower = grid[bottom + gx] + (grid[bottom + gx + 1] - grid[bottom + gx]) * wx;
                    texture[row + x] += amplitude * (upper + (lower - upper) * wy);
                }
            });
        }

        /// <summary>Uniform in -1..1 for one grid point.</summary>
        private static double Value(ulong seed, int x, int y, long layer)
        {
            var hash = StableSeed.Mix(StableSeed.Mix(StableSeed.Mix(seed, x), y), layer);
            return (hash >> 11) * (2.0 / (1UL << 53)) - 1;
        }

        /// <summary>Smoothstep, so that the grid does not show as creases.</summary>
        private static double Fade(double t) => t * t * (3 - 2 * t);
    }
}
