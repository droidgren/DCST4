using System;
using System.Linq;

namespace DCST4.Core.Generation.Realism
{
    /// <summary>
    /// How much the image looks like one from a real modality. Off draws the flat test pattern, as earlier versions did.
    /// The other levels add tissue texture, the modality's shape and its noise; they differ only in how strong the noise is.
    /// </summary>
    public enum RealismLevel
    {
        Off,
        Low,
        Normal,
        High,
    }

    public static class RealismLevels
    {
        /// <summary>What the noise of a profile is multiplied by.</summary>
        public static double NoiseFactor(RealismLevel level)
        {
            switch (level)
            {
                case RealismLevel.Low: return 0.5;
                case RealismLevel.Normal: return 1.0;
                case RealismLevel.High: return 2.0;
                default: return 0;
            }
        }

        /// <summary>By name, case ignored. False if it is none of them.</summary>
        public static bool TryParse(string text, out RealismLevel level)
        {
            level = RealismLevel.Off;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Enum.TryParse(text.Trim(), true, out level) && Enum.IsDefined(typeof(RealismLevel), level);
        }

        /// <summary>The level a setting names, or Off if it names none.</summary>
        public static RealismLevel Parse(string text) => TryParse(text, out var level) ? level : RealismLevel.Off;

        public static string Names => string.Join(", ", Enum.GetNames(typeof(RealismLevel)).Select(n => n.ToLowerInvariant()));
    }

    /// <summary>The kind of noise a modality's detector or reconstruction leaves in the image.</summary>
    public enum NoiseModel
    {
        /// <summary>Ultrasound: the envelope of a complex Gaussian field, which is Rayleigh distributed, multiplied with the echo.</summary>
        Speckle,
        /// <summary>Projection X-ray: photon noise, which grows with the square root of the signal, over a small electronic floor.</summary>
        Quantum,
        /// <summary>CT: additive Gaussian noise in HU, correlated by the reconstruction kernel.</summary>
        CtGaussian,
        /// <summary>MR: the magnitude of the signal plus complex Gaussian noise, so air shows a Rayleigh floor.</summary>
        Rician,
        /// <summary>PET and NM: counting noise on few counts, smoothed by the reconstruction.</summary>
        Poisson,
    }

    /// <summary>The outline of what the modality images.</summary>
    public enum ImageShape
    {
        /// <summary>A sector from an apex above the top edge. Outside is black and noise-free.</summary>
        UltrasoundFan,
        /// <summary>A body in air, inside the circular reconstruction field of view. Outside the circle is noise-free.</summary>
        CtFieldOfView,
        /// <summary>A body in air.</summary>
        Body,
        /// <summary>A body on a dark direct-exposure background, with bright collimation borders.</summary>
        Radiograph,
        /// <summary>A breast against the chest wall at the left edge, on black.</summary>
        Breast,
        /// <summary>A circular detector or image-intensifier field. Outside is black and noise-free.</summary>
        DetectorCircle,
    }

    /// <summary>The realism model of one modality. The noise strength is for the Normal level.</summary>
    public sealed class RealismProfile
    {
        public NoiseModel Noise { get; private set; }
        public ImageShape Shape { get; private set; }
        /// <summary>
        /// Speckle: how much of fully developed speckle is kept. Quantum: the coefficient of the square root of the signal.
        /// CT: the standard deviation in HU. Rician: the standard deviation as a fraction of full scale. Poisson: the counts at full scale.
        /// </summary>
        public double Strength { get; private set; }
        /// <summary>The radius of the box filter that correlates the noise, in pixels, along a row and along a column.</summary>
        public int CorrelationX { get; private set; }
        public int CorrelationY { get; private set; }
        /// <summary>How much of the noise stays the same from one frame to the next, 0 to 1. Only frames of one multi-frame image are related.</summary>
        public double TemporalPersistence { get; private set; }
        /// <summary>How far the tissue texture moves along the stack between neighbouring frames or images, in cells of the coarsest texture.</summary>
        public double TextureStep { get; private set; }
        /// <summary>One line for the Help and the Settings dialog.</summary>
        public string Description { get; private set; }
        /// <summary>The window the image opens in, after the Modality LUT. Null for the full range of the stored values.</summary>
        public double? WindowCenter { get; private set; }
        public double? WindowWidth { get; private set; }
        public string WindowExplanation { get; private set; }

        /// <summary><paramref name="textureStep"/> is small for a cine loop, where the frames are moments, and larger for a volume, where they are slices.</summary>
        public static RealismProfile Ultrasound(double temporalPersistence, double textureStep) => new RealismProfile
        {
            Noise = NoiseModel.Speckle,
            Shape = ImageShape.UltrasoundFan,
            Strength = 0.55,
            CorrelationX = 2,
            CorrelationY = 1,
            TemporalPersistence = temporalPersistence,
            TextureStep = textureStep,
            Description = "Speckle (Rayleigh, grain wider than tall) in a sector, with depth attenuation",
        };

        public static RealismProfile Radiography() => new RealismProfile
        {
            Noise = NoiseModel.Quantum,
            Shape = ImageShape.Radiograph,
            Strength = 0.025,
            CorrelationX = 1,
            CorrelationY = 1,
            TextureStep = 0.25,
            Description = "Quantum noise that grows with the signal, collimation borders",
        };

        public static RealismProfile Mammography() => new RealismProfile
        {
            Noise = NoiseModel.Quantum,
            Shape = ImageShape.Breast,
            Strength = 0.02,
            CorrelationX = 1,
            CorrelationY = 1,
            TextureStep = 0.25,
            Description = "Quantum noise, breast outline against the chest wall",
        };

        public static RealismProfile Fluoroscopy(double temporalPersistence) => new RealismProfile
        {
            Noise = NoiseModel.Quantum,
            Shape = ImageShape.DetectorCircle,
            Strength = 0.035,
            CorrelationX = 1,
            CorrelationY = 1,
            TemporalPersistence = temporalPersistence,
            TextureStep = 0.03,
            Description = "Quantum noise in a circular detector field, mostly new in each frame",
        };

        /// <summary>A sharp kernel, such as B70 or B60, gives more and finer noise than a smooth one such as B31 or H31.</summary>
        public static RealismProfile Ct(string convolutionKernel)
        {
            var sharp = KernelNumber(convolutionKernel) >= 50;
            return new RealismProfile
            {
                Noise = NoiseModel.CtGaussian,
                Shape = ImageShape.CtFieldOfView,
                Strength = sharp ? 25 : 12,
                CorrelationX = sharp ? 0 : 1,
                CorrelationY = sharp ? 0 : 1,
                TextureStep = 0.25,
                Description = "Correlated Gaussian noise in HU set by the kernel, body in a circular field of view",
                WindowCenter = 40,
                WindowWidth = 400,
                WindowExplanation = "SOFT TISSUE",
            };
        }

        public static RealismProfile Mr() => new RealismProfile
        {
            Noise = NoiseModel.Rician,
            Shape = ImageShape.Body,
            Strength = 0.018,
            TextureStep = 0.25,
            Description = "Rician noise, with a Rayleigh floor in the air around the body",
        };

        public static RealismProfile NuclearMedicine() => new RealismProfile
        {
            Noise = NoiseModel.Poisson,
            Shape = ImageShape.Body,
            Strength = 80,
            CorrelationX = 1,
            CorrelationY = 1,
            TextureStep = 0.25,
            Description = "Poisson counting noise on few counts, smoothed as a reconstruction would",
        };

        /// <summary>The number in a kernel name such as B70f or H31s. 0 if there is none.</summary>
        internal static int KernelNumber(string kernel)
        {
            if (string.IsNullOrEmpty(kernel)) return 0;
            var digits = new string(kernel.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var number) ? number : 0;
        }
    }

    /// <summary>What the renderer needs for a realistic frame. Null in <see cref="PixelRenderSpec.Realism"/> draws the flat pattern.</summary>
    public sealed class RealismSpec
    {
        public RealismProfile Profile { get; set; }
        public RealismLevel Level { get; set; }
        /// <summary>The series, so that the texture is shared by its images and changes slowly along the stack.</summary>
        public string SeriesSeed { get; set; }
        /// <summary>The image in the series, 0 for the first. Each image has its own noise.</summary>
        public int Instance { get; set; }
        /// <summary>The frame in the image, 0 for the first.</summary>
        public int Frame { get; set; }
        /// <summary>The frames in the image. Frames are related only within one multi-frame image.</summary>
        public int Frames { get; set; } = 1;
    }
}
