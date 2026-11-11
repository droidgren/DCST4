using DCST4.Core.Network;
using FellowOakDicom;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>Certificates that DCST4 makes for a test and throws away afterwards. None is ever put in a certificate store.</summary>
    internal static class TestCertificates
    {
        /// <summary>
        /// A client certificate from a certificate authority that was made a moment ago and that nobody trusts.
        /// Windows needs the private key in a key container to use it in a handshake, so one exists in the user's profile
        /// until the certificate is disposed. Dispose it.
        /// </summary>
        public static X509Certificate2 CreateFromUnknownAuthority()
        {
            var now = DateTimeOffset.UtcNow;
            using (var authorityKey = RSA.Create(2048))
            using (var clientKey = RSA.Create(2048))
            {
                var authorityRequest = new CertificateRequest("CN=DCST4 test authority that nobody trusts", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                using (var authority = authorityRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2)))
                {
                    var clientRequest = new CertificateRequest("CN=DCST4 test client", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    clientRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                    clientRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
                    // Client authentication.
                    clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));

                    var serial = new byte[8];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(serial);
                    serial[0] &= 0x7F;

                    using (var signed = clientRequest.Create(authority, now.AddDays(-1), now.AddDays(1), serial))
                    using (var withKey = signed.CopyWithPrivateKey(clientKey))
                    {
                        const string password = "dcst4";
                        return new X509Certificate2(withKey.Export(X509ContentType.Pfx, password), password);
                    }
                }
            }
        }
    }

    /// <summary>Common ground of the TLS tests. Each is INDETERMINATE when the profile does not use TLS.</summary>
    public abstract class TlsTest : ITestCase
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public TestCategory Category => TestCategory.Limits;
        public abstract string Description { get; }
        public string Group => TestGroups.Tls;
        public bool StoresObjects => false;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            if (!context.Profile.Endpoint.UseTls)
            {
                return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = "The profile does not use TLS, so there is nothing to test." };
            }
            return await RunAsync(context, token).ConfigureAwait(false);
        }

        protected abstract Task<TestResult> RunAsync(TestContext context, CancellationToken token);

        /// <summary>TLS 1.2 with a 256 bit cipher, in the words of the result.</summary>
        internal static string Describe(TlsSessionInfo session) =>
            $"{session.Protocol}, cipher {session.CipherAlgorithm} with {Fixtures.Int(session.CipherStrength)} bits";

        /// <summary>The client certificate of the profile, or null if it names none. Throws InvalidOperationException if it is not in the store.</summary>
        internal static X509Certificate2 ProfileCertificate(DicomEndpoint endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint.TlsClientCertThumbprint)) return null;
            return DicomClientBuilder.FindCertificate(endpoint.TlsClientCertThumbprint)
                ?? throw new InvalidOperationException($"The client certificate with thumbprint {endpoint.TlsClientCertThumbprint} was not found in the current user's personal certificate store.");
        }
    }

    /// <summary>An association over TLS with the profile's own settings, and which TLS version and cipher it got.</summary>
    public sealed class TlsAssociationTest : TlsTest
    {
        public override string Id => "tls-association";
        public override string Name => "TLS: association with the profile's settings";
        public override string Description => "Opens an association over TLS with the profile's own settings and records the TLS version and cipher. INDETERMINATE when the profile does not use TLS. Stores nothing.";

        protected override async Task<TestResult> RunAsync(TestContext context, CancellationToken token)
        {
            var endpoint = context.Profile.Endpoint;
            var target = RawSupport.Target(context);
            var timeline = new AssociationTimeline();
            RecordingTlsInitiator recorder = null;

            var echo = await EchoService.EchoAsync(endpoint, context.Profile.Network, token, timeline, null, inner => recorder = new RecordingTlsInitiator(inner)).ConfigureAwait(false);

            var result = new TestResult
            {
                NegotiatedContexts = timeline.Negotiation,
                Timeline = timeline.Events,
                Limits = new LimitsResult { Tls = recorder?.Session },
            };
            if (echo.StatusCode.HasValue) Fixtures.AddStatus(result, "C-ECHO", echo.StatusCode.Value);
            if (echo.Failure != ConnectivityFailure.None) result.Details["failure"] = echo.Failure.ToString();

            var session = recorder?.Session;
            var tls = session != null && session.HandshakeCompleted ? Describe(session) : null;
            if (tls != null)
            {
                result.Details["tlsProtocol"] = session.Protocol;
                result.Details["tlsCipher"] = session.CipherAlgorithm;
            }

            if (echo.Outcome == ConnectivityOutcome.Cancelled) Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
            else if (echo.Outcome == ConnectivityOutcome.Success) Fixtures.Set(result, TestResultClass.PASS, $"{target} accepted an association over {tls} and answered C-ECHO.");
            else if (echo.Failure == ConnectivityFailure.LocalConfiguration) Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {echo.Message}");
            else if (echo.Failure == ConnectivityFailure.Timeout) Fixtures.Set(result, TestResultClass.TIMEOUT, $"The TLS association to {target} timed out: {echo.Message}");
            else if (tls == null) Fixtures.Set(result, TestResultClass.FAIL, $"The TLS handshake with {target} failed: {echo.Message}" + ServerProblem(session));
            else Fixtures.Set(result, TestResultClass.FAIL, $"The TLS session with {target} was set up over {tls}, but the association or the C-ECHO failed: {echo.Message}");
            return result;
        }

        private static string ServerProblem(TlsSessionInfo session) =>
            session?.PolicyErrors == null || session.PolicyErrors == "None" ? string.Empty : $" The server certificate did not pass: {session.PolicyErrors}.";
    }

    /// <summary>
    /// Checks the server's own certificate strictly against the host name in the profile, also where the profile accepts
    /// certificate errors. This is where a hostname mismatch, an unknown authority or an expired server certificate shows.
    /// </summary>
    public sealed class TlsServerCertificateTest : TlsTest
    {
        public override string Id => "tls-server-certificate";
        public override string Name => "TLS: the server certificate matches the host name and is trusted";
        public override string Description => "Checks the destination's certificate strictly against the host name, also when the profile accepts certificate errors: name mismatch, unknown authority, expiry. Stores nothing.";

        protected override async Task<TestResult> RunAsync(TestContext context, CancellationToken token)
        {
            var endpoint = context.Profile.Endpoint;
            var target = RawSupport.Target(context);
            var result = new TestResult { Limits = new LimitsResult() };
            var timeline = new AssociationTimeline();

            var tls = new TestTlsInitiator { AcceptAnyServerCertificate = false };
            result.Limits.Tls = tls.Session;
            try
            {
                tls.ClientCertificate = ProfileCertificate(endpoint);
            }
            catch (InvalidOperationException ex)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {ex.Message}");
                return result;
            }

            var open = await RawSupport.OpenAsync(context, timeline, null, token, tls).ConfigureAwait(false);
            open.Raw?.Dispose();
            var session = tls.Session;

            if (open.Cancelled)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
                return result;
            }

            if (session.PolicyErrors != null && session.PolicyErrors != "None")
            {
                var problems = new System.Collections.Generic.List<string>();
                if (session.PolicyErrors.Contains("RemoteCertificateNameMismatch")) problems.Add($"its name does not match the host name {endpoint.Host} (hostname mismatch)");
                if (session.PolicyErrors.Contains("RemoteCertificateChainErrors")) problems.Add("its chain is not trusted on this computer" + (string.IsNullOrEmpty(session.ChainStatus) ? string.Empty : $" ({session.ChainStatus})"));
                if (session.PolicyErrors.Contains("RemoteCertificateNotAvailable")) problems.Add("the server presented none");

                result.Details["finding"] = "server-certificate-not-valid";
                result.Details["policyErrors"] = session.PolicyErrors;
                if (!string.IsNullOrEmpty(session.ChainStatus)) result.Details["chainStatus"] = session.ChainStatus;
                Fixtures.Set(result, TestResultClass.FAIL,
                    $"Finding: the certificate of {target} ({session.ServerSubject}) does not pass a strict check: {string.Join(", and ", problems)}."
                    + (endpoint.TlsIgnoreCertErrors ? " The profile accepts certificate errors, so DCST4 connects all the same." : string.Empty));
                return result;
            }

            if (open.Raw == null)
            {
                if (open.Failure == ConnectivityFailure.Timeout) Fixtures.Set(result, TestResultClass.TIMEOUT, $"The TLS handshake with {target} timed out: {open.Message}");
                else Fixtures.Set(result, TestResultClass.FAIL, $"The TLS handshake with {target} failed before its certificate could be checked: {open.Message}");
                return result;
            }

            result.Details["tlsProtocol"] = session.Protocol;
            Fixtures.Set(result, TestResultClass.PASS,
                $"The certificate of {target} ({session.ServerSubject}, issued by {session.ServerIssuer}) matches the host name {endpoint.Host}, is trusted on this computer and is within its validity period. Session over {Describe(session)}.");
            return result;
        }
    }

    /// <summary>Presents a client certificate that the system under test should refuse.</summary>
    public abstract class TlsBadClientCertificateTest : TlsTest
    {
        /// <summary>The certificate as it reads after "a client certificate ".</summary>
        protected abstract string What { get; }
        protected abstract string Finding { get; }

        /// <summary>The certificate to present and whether to dispose it afterwards, or a result if there is none to present.</summary>
        private protected abstract Tuple<X509Certificate2, bool> Certificate(TestContext context, string target, TestResult result);

        protected override async Task<TestResult> RunAsync(TestContext context, CancellationToken token)
        {
            var endpoint = context.Profile.Endpoint;
            var target = RawSupport.Target(context);
            var result = new TestResult { Limits = new LimitsResult() };
            var timelines = new TimelineCollector();
            RawAssociation raw = null;
            X509Certificate2 certificate = null;
            var dispose = false;

            try
            {
                // Without this, a system that refuses everyone would look as if it had checked the certificate.
                var baseline = await BaselineAsync(context, timelines, token).ConfigureAwait(false);
                if (baseline != null)
                {
                    result.Result = baseline.Item1;
                    result.Explanation = baseline.Item2;
                    return result;
                }

                context.Report($"Presenting a client certificate {What}, and waiting for the system to refuse or accept it");
                var chosen = Certificate(context, target, result);
                if (chosen == null) return result;
                certificate = chosen.Item1;
                dispose = chosen.Item2;
                result.Limits.ClientCertificate = What;

                var tls = new TestTlsInitiator { ClientCertificate = certificate, AcceptAnyServerCertificate = endpoint.TlsIgnoreCertErrors };
                result.Limits.Tls = tls.Session;
                var session = tls.Session;

                var timeline = timelines.Next();
                var open = await RawSupport.OpenAsync(context, timeline, timelines.Clock, token, tls).ConfigureAwait(false);
                raw = open.Raw;
                if (raw == null)
                {
                    timelines.Collect(timeline);
                    if (open.Cancelled)
                    {
                        Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
                    }
                    else if (open.Failure == ConnectivityFailure.Timeout)
                    {
                        Fixtures.Set(result, TestResultClass.TIMEOUT, $"The TLS handshake with {target} timed out when DCST4 presented a client certificate {What}: {open.Message}");
                    }
                    else if (!endpoint.TlsIgnoreCertErrors && session.PolicyErrors != null && session.PolicyErrors != "None")
                    {
                        Fixtures.Set(result, TestResultClass.INDETERMINATE,
                            $"DCST4 itself did not accept the server certificate of {target} ({session.PolicyErrors}), so the handshake never got as far as the client certificate.");
                    }
                    else
                    {
                        result.Details["refusedAt"] = "tls-handshake";
                        Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} refused the TLS handshake when DCST4 presented a client certificate {What}: {open.Message}");
                    }
                    return result;
                }

                using (raw.CloseOn(token))
                {
                    var outcome = await raw.AssociateAsync(RawSupport.EchoContexts(), RawSupport.OurMaxPdu, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    result.Details["tlsProtocol"] = session.Protocol;

                    if (outcome.Accepted)
                    {
                        result.NegotiatedContexts = raw.Negotiation();
                        await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);

                        if (!session.ClientCertificateRequested && !session.MutuallyAuthenticated)
                        {
                            Fixtures.Set(result, TestResultClass.NOT_SUPPORTED,
                                $"{target} did not ask for a client certificate in the TLS handshake, so it does not check them and the certificate {What} was never looked at.");
                        }
                        else
                        {
                            result.Details["finding"] = Finding;
                            Fixtures.Set(result, TestResultClass.FAIL, $"Finding: {target} accepted a TLS session and an association from a client certificate {What}.");
                        }
                    }
                    else if (outcome.TimedOut)
                    {
                        Fixtures.Set(result, TestResultClass.TIMEOUT, $"{target} completed the TLS handshake with a client certificate {What} and then did not answer the A-ASSOCIATE-RQ.");
                    }
                    else if (outcome.Rejection != null)
                    {
                        result.Details["refusedAt"] = "association";
                        RawSupport.RecordRejection(result, outcome.Rejection);
                        Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE,
                            $"{target} completed the TLS handshake with a client certificate {What} and then rejected the association: {outcome.Rejection}.");
                    }
                    else
                    {
                        // TLS 1.3 tells the client about a refused certificate only after the handshake, on the first read.
                        result.Details["refusedAt"] = "after-handshake";
                        Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE,
                            $"{target} refused a client certificate {What}: it {outcome.Ending.Describe()} right after the TLS handshake, instead of answering the A-ASSOCIATE-RQ.");
                    }
                }
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
                return result;
            }
            catch (Exception ex) when (RawSupport.IsNetworkFailure(ex))
            {
                if (token.IsCancellationRequested) Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
                else Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} refused a client certificate {What}: the connection failed right after the TLS handshake ({NetworkErrors.GetUserMessage(ex)}).");
                return result;
            }
            finally
            {
                if (raw != null)
                {
                    await RawSupport.CleanUpAsync(new[] { raw }, "Cancelled").ConfigureAwait(false);
                    timelines.Collect(raw.Timeline);
                }
                result.Timeline = timelines.Events;
                if (dispose) certificate?.Dispose();
            }
        }

        /// <summary>An association with the profile's own settings. Null if it worked, otherwise the result of the test.</summary>
        private static async Task<Tuple<TestResultClass, string>> BaselineAsync(TestContext context, TimelineCollector timelines, CancellationToken token)
        {
            var target = RawSupport.Target(context);
            var timeline = timelines.Next();
            var echo = await EchoService.EchoAsync(context.Profile.Endpoint, context.Profile.Network, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);

            if (timeline.Accepted) return null;
            if (echo.Outcome == ConnectivityOutcome.Cancelled) return Tuple.Create(TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
            if (echo.Failure == ConnectivityFailure.LocalConfiguration)
            {
                return Tuple.Create(TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {echo.Message}");
            }
            return Tuple.Create(TestResultClass.INDETERMINATE,
                $"{target} did not accept a TLS association with the profile's own settings, so a refusal of another certificate would prove nothing: {echo.Message}");
        }
    }

    public sealed class TlsWrongAuthorityTest : TlsBadClientCertificateTest
    {
        public override string Id => "tls-client-cert-wrong-ca";
        public override string Name => "TLS: client certificate from an unknown authority";
        public override string Description => "Presents a client certificate from an authority DCST4 makes up for the test. The destination should refuse it. Stores nothing.";
        protected override string What => "from a certificate authority that DCST4 made up for this test";
        protected override string Finding => "accepted-client-certificate-from-unknown-ca";

        private protected override Tuple<X509Certificate2, bool> Certificate(TestContext context, string target, TestResult result)
        {
            try
            {
                return Tuple.Create(TestCertificates.CreateFromUnknownAuthority(), true);
            }
            catch (CryptographicException ex)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not make a certificate for the test, which is a local problem and not a fault of {target}: {ex.Message}");
                return null;
            }
        }
    }

    public sealed class TlsExpiredCertificateTest : TlsBadClientCertificateTest
    {
        public override string Id => "tls-client-cert-expired";
        public override string Name => "TLS: expired client certificate";
        public override string Description => "Presents an expired client certificate that you have installed, named by its thumbprint on this tab. The destination should refuse it. NOT_SUPPORTED when no thumbprint is set. Stores nothing.";
        protected override string What => "that has expired";
        protected override string Finding => "accepted-expired-client-certificate";

        private protected override Tuple<X509Certificate2, bool> Certificate(TestContext context, string target, TestResult result)
        {
            var certificate = context.Session.Options.ExpiredClientCertificate;
            var thumbprint = context.Profile.Endpoint.TlsExpiredClientCertThumbprint;

            if (certificate == null && string.IsNullOrWhiteSpace(thumbprint))
            {
                Fixtures.Set(result, TestResultClass.NOT_SUPPORTED,
                    "The profile names no expired client certificate, so this was not tested. DCST4 cannot make one itself: it has to come from an authority that the system under test trusts, "
                    + "or the refusal would say nothing about the expiry. Install one in the current user's personal certificate store and put its thumbprint in tlsExpiredClientCertThumbprint.");
                return null;
            }

            if (certificate == null)
            {
                certificate = DicomClientBuilder.FindCertificate(thumbprint);
                if (certificate == null)
                {
                    Fixtures.Set(result, TestResultClass.TOOL_ERROR,
                        $"The expired client certificate with thumbprint {thumbprint} was not found in the current user's personal certificate store. This is a local problem and not a fault of {target}.");
                    return null;
                }
            }

            if (!certificate.HasPrivateKey)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"The expired client certificate {certificate.Subject} has no private key on this computer, so it cannot be presented. This is a local problem and not a fault of {target}.");
                return null;
            }
            if (certificate.NotAfter > DateTime.Now)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"The certificate {certificate.Subject} named as expired is still valid, so the test would prove nothing. This is a local problem and not a fault of {target}.");
                return null;
            }

            result.Details["expiredCertificateSubject"] = certificate.Subject;
            return Tuple.Create(certificate, false);
        }
    }
}
