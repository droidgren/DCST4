using FellowOakDicom;
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
                    services.AddFellowOakDicom().AddTranscoderManager<PureTranscoderManager>();
                    registerServices?.Invoke(services);
                })
                .Build();
        }
    }
}
