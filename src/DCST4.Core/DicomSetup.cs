using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.PureCodecs;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace DCST4.Core
{
    public static class DicomSetup
    {
        /// <summary>Global fo-dicom setup. Call once at startup. PureCodecs supplies the JPEG Lossless encoder, which fo-dicom itself does not have.</summary>
        public static void Initialize(Action<IServiceCollection> registerServices = null)
        {
            new DicomSetupBuilder()
                .RegisterServices(services =>
                {
                    // RawImageManager renders to bytes without WPF or WinForms, for the image preview.
                    services.AddFellowOakDicom().AddTranscoderManager<PureTranscoderManager>().AddImageManager<RawImageManager>();
                    registerServices?.Invoke(services);
                })
                .Build();
        }
    }
}
