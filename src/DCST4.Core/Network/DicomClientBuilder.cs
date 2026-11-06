using FellowOakDicom.Network.Client;
using FellowOakDicom.Network.Tls;
using Serilog;
using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace DCST4.Core.Network
{
    /// <summary>Creates configured fo-dicom clients. Carried over from DMWLT4.</summary>
    public static class DicomClientBuilder
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(DicomClientBuilder));

        public static IDicomClient Create(DicomEndpoint endpoint, NetworkOptions options)
        {
            var client = endpoint.UseTls
                ? DicomClientFactory.Create(endpoint.Host, endpoint.Port, CreateTlsInitiator(endpoint), endpoint.CallingAe, endpoint.CalledAe)
                : DicomClientFactory.Create(endpoint.Host, endpoint.Port, false, endpoint.CallingAe, endpoint.CalledAe);

            client.ServiceOptions.LogDimseDatasets = options.LogDimseDatasets;
            client.ServiceOptions.LogDataPDUs = options.LogDataPdus;

            if (options.ConnectionTimeoutSeconds > 0) client.ClientOptions.ConnectionTimeoutInMs = options.ConnectionTimeoutSeconds * 1000;
            if (options.AssociationTimeoutSeconds > 0) client.ClientOptions.AssociationRequestTimeoutInMs = options.AssociationTimeoutSeconds * 1000;
            // The response timeout is handled by TimeoutWatch, see there why fo-dicom's RequestTimeout is not used.
            client.ServiceOptions.RequestTimeout = null;

            return client;
        }

        private static ITlsInitiator CreateTlsInitiator(DicomEndpoint endpoint)
        {
            var initiator = new DefaultTlsInitiator
            {
                IgnoreSslPolicyErrors = endpoint.TlsIgnoreCertErrors,
            };

            if (!string.IsNullOrWhiteSpace(endpoint.TlsClientCertThumbprint))
            {
                var cert = FindCertificate(endpoint.TlsClientCertThumbprint);
                if (cert == null)
                {
                    throw new InvalidOperationException(
                        $"The client certificate with thumbprint {endpoint.TlsClientCertThumbprint} was not found in the current user's personal certificate store. Select another one in TLS options.");
                }
                initiator.Certificates = new X509CertificateCollection { cert };
                Logger.Information("Using TLS client certificate {subject} ({thumbprint})", cert.Subject, cert.Thumbprint);
            }

            return initiator;
        }

        /// <summary>Looks up a certificate by thumbprint in CurrentUser\My. Returns null if not found.</summary>
        public static X509Certificate2 FindCertificate(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint)) return null;
            var clean = new string(thumbprint.Where(Uri.IsHexDigit).ToArray());
            var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, clean, false);
                return found.Count > 0 ? found[0] : null;
            }
            finally
            {
                store.Close();
            }
        }
    }

    public static class NetworkErrors
    {
        /// <summary>
        /// Returns the message of the exception that actually caused the error. fo-dicom wraps e.g. a refused
        /// connection in an AggregateException whose own message is only "One or more errors occurred".
        /// </summary>
        public static string GetUserMessage(Exception ex)
        {
            while (true)
            {
                if (ex is AggregateException agg)
                {
                    var flat = agg.Flatten();
                    if (flat.InnerExceptions.Count == 1)
                    {
                        ex = flat.InnerExceptions[0];
                        continue;
                    }
                    if (flat.InnerExceptions.Count > 1)
                    {
                        return string.Join("\n", flat.InnerExceptions.Select(GetUserMessage).Distinct());
                    }
                }
                return ex.Message;
            }
        }
    }
}
