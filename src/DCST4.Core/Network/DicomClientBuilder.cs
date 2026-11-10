using FellowOakDicom.Network.Client;
using FellowOakDicom.Network.Tls;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace DCST4.Core.Network
{
    /// <summary>Creates configured fo-dicom clients. Carried over from DMWLT4.</summary>
    public static class DicomClientBuilder
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(DicomClientBuilder));

        /// <param name="wrapTls">Given the TLS initiator of the endpoint, returns the one to use. For recording what the handshake came to.</param>
        public static IDicomClient Create(DicomEndpoint endpoint, NetworkOptions options, Func<ITlsInitiator, ITlsInitiator> wrapTls = null)
        {
            var client = endpoint.UseTls
                ? DicomClientFactory.Create(endpoint.Host, endpoint.Port, Wrap(CreateTlsInitiator(endpoint), wrapTls), endpoint.CallingAe, endpoint.CalledAe)
                : DicomClientFactory.Create(endpoint.Host, endpoint.Port, false, endpoint.CallingAe, endpoint.CalledAe);

            client.ServiceOptions.LogDimseDatasets = options.LogDimseDatasets;
            client.ServiceOptions.LogDataPDUs = options.LogDataPdus;

            if (options.ConnectionTimeoutSeconds > 0) client.ClientOptions.ConnectionTimeoutInMs = options.ConnectionTimeoutSeconds * 1000;
            if (options.AssociationTimeoutSeconds > 0) client.ClientOptions.AssociationRequestTimeoutInMs = options.AssociationTimeoutSeconds * 1000;
            // The response timeout is handled by TimeoutWatch, see there why fo-dicom's RequestTimeout is not used.
            client.ServiceOptions.RequestTimeout = null;

            return client;
        }

        private static ITlsInitiator Wrap(ITlsInitiator initiator, Func<ITlsInitiator, ITlsInitiator> wrapTls) => wrapTls == null ? initiator : wrapTls(initiator);

        /// <summary>The TLS setup of an endpoint: whether certificate errors are accepted, and the client certificate by thumbprint.</summary>
        internal static DefaultTlsInitiator CreateTlsInitiator(DicomEndpoint endpoint)
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

    /// <summary>What a TLS handshake came to, as far as the client can see it.</summary>
    public sealed class TlsSessionInfo
    {
        public bool HandshakeCompleted { get; set; }
        /// <summary>For example Tls12 or Tls13.</summary>
        public string Protocol { get; set; }
        public string CipherAlgorithm { get; set; }
        public int CipherStrength { get; set; }
        public string KeyExchangeAlgorithm { get; set; }
        public string HashAlgorithm { get; set; }
        /// <summary>Both sides presented a certificate that the other accepted.</summary>
        public bool MutuallyAuthenticated { get; set; }
        /// <summary>Whether the server asked for a client certificate during the handshake.</summary>
        public bool ClientCertificateRequested { get; set; }
        public string ServerSubject { get; set; }
        public string ServerIssuer { get; set; }
        public DateTime? ServerNotBeforeUtc { get; set; }
        public DateTime? ServerNotAfterUtc { get; set; }
        /// <summary>What .NET found wrong with the server certificate, for example RemoteCertificateNameMismatch. "None" if nothing.</summary>
        public string PolicyErrors { get; set; }
        /// <summary>The status of the certificate chain, for example UntrustedRoot or NotTimeValid. Empty if it is in order.</summary>
        public string ChainStatus { get; set; }

        internal void ReadServerCertificate(X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            PolicyErrors = errors.ToString();
            if (chain != null) ChainStatus = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()).Distinct());
            if (certificate == null) return;

            var server = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
            ServerSubject = server.Subject;
            ServerIssuer = server.Issuer;
            ServerNotBeforeUtc = server.NotBefore.ToUniversalTime();
            ServerNotAfterUtc = server.NotAfter.ToUniversalTime();
        }

        internal void ReadSession(SslStream stream)
        {
            HandshakeCompleted = true;
            Protocol = stream.SslProtocol.ToString();
            CipherAlgorithm = stream.CipherAlgorithm.ToString();
            CipherStrength = stream.CipherStrength;
            KeyExchangeAlgorithm = stream.KeyExchangeAlgorithm.ToString();
            HashAlgorithm = stream.HashAlgorithm.ToString();
            MutuallyAuthenticated = stream.IsMutuallyAuthenticated;
        }
    }

    /// <summary>
    /// Passes the handshake on to the initiator the endpoint would use anyway, and reads the TLS version and the rest from
    /// the resulting SslStream. This is how a test sees what an ordinary connection of DCST4 negotiates.
    /// </summary>
    internal sealed class RecordingTlsInitiator : ITlsInitiator
    {
        private readonly ITlsInitiator _inner;

        public RecordingTlsInitiator(ITlsInitiator inner)
        {
            _inner = inner;
            if (inner is DefaultTlsInitiator standard && standard.CertificateValidationCallback == null)
            {
                // The same verdict as without the callback, which is only there to see what was found.
                standard.CertificateValidationCallback = (sender, certificate, chain, errors) =>
                {
                    Session.ReadServerCertificate(certificate, chain, errors);
                    return errors == SslPolicyErrors.None || standard.IgnoreSslPolicyErrors;
                };
            }
        }

        public TlsSessionInfo Session { get; } = new TlsSessionInfo();

        public Stream InitiateTls(Stream plainStream, string remoteAddress, int remotePort)
        {
            var stream = _inner.InitiateTls(plainStream, remoteAddress, remotePort);
            if (stream is SslStream ssl) Session.ReadSession(ssl);
            return stream;
        }
    }

    /// <summary>
    /// A handshake under a test's control: which client certificate is presented, and whether the server certificate is
    /// checked. The certificate is handed over by a selection callback, because .NET otherwise leaves out a certificate
    /// whose issuer the server did not list, and the tests are about exactly such certificates.
    /// </summary>
    internal sealed class TestTlsInitiator : ITlsInitiator
    {
        /// <summary>What an ordinary connection of DCST4 allows.</summary>
        private static readonly SslProtocols DefaultProtocols = new DefaultTlsInitiator().Protocols;

        /// <summary>Presented if the server asks for one. Null presents none.</summary>
        public X509Certificate2 ClientCertificate { get; set; }
        /// <summary>Accept the server certificate whatever is wrong with it. What was wrong is still recorded.</summary>
        public bool AcceptAnyServerCertificate { get; set; }
        public TlsSessionInfo Session { get; } = new TlsSessionInfo();

        public Stream InitiateTls(Stream plainStream, string remoteAddress, int remotePort)
        {
            var ssl = new SslStream(plainStream, false,
                (sender, certificate, chain, errors) =>
                {
                    Session.ReadServerCertificate(certificate, chain, errors);
                    return errors == SslPolicyErrors.None || AcceptAnyServerCertificate;
                },
                (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                {
                    // Called first without a server certificate, and again once the server has asked for a client certificate.
                    if (remoteCertificate != null) Session.ClientCertificateRequested = true;
                    return ClientCertificate;
                });

            var certificates = new X509CertificateCollection();
            if (ClientCertificate != null) certificates.Add(ClientCertificate);
            ssl.AuthenticateAsClient(remoteAddress, certificates, DefaultProtocols, false);
            Session.ReadSession(ssl);
            return ssl;
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
