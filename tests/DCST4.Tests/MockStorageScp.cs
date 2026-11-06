using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>What the mock SCP received for one image.</summary>
    public sealed class ReceivedImage
    {
        public DicomDataset Dataset;
        public DicomTransferSyntax Syntax;
        public byte[] Frame;
        public string CalledAe;
        public string CallingAe;
    }

    /// <summary>Behaviour and records of the mock SCP. Static because fo-dicom creates the service instances itself.</summary>
    public static class Scp
    {
        public static DicomTransferSyntax[] AcceptedSyntaxes;
        public static bool RejectAssociation;
        /// <summary>Status for the n-th image, counted from 0. Null means Success.</summary>
        public static Func<int, DicomStatus> StatusFor;
        public static Tag OffendingElement;
        public static int ResponseDelayMs;
        public static int HangFromImage = -1;

        public static int AssociationCount;
        public static int EchoCount;
        public static int StoreCount;
        public static readonly ConcurrentQueue<ReceivedImage> Received = new ConcurrentQueue<ReceivedImage>();
        public static readonly ConcurrentQueue<string> Proposed = new ConcurrentQueue<string>();

        public sealed class Tag { public DicomTag Value; }

        public static void Reset()
        {
            AcceptedSyntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1 };
            RejectAssociation = false;
            StatusFor = null;
            OffendingElement = null;
            ResponseDelayMs = 0;
            HangFromImage = -1;
            AssociationCount = 0;
            EchoCount = 0;
            StoreCount = 0;
            while (Received.TryDequeue(out _)) { }
            while (Proposed.TryDequeue(out _)) { }
        }
    }

    public sealed class MockStorageScp : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        private string _calledAe;
        private string _callingAe;

        public MockStorageScp(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies deps)
            : base(stream, fallbackEncoding, log, deps) { }

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            Interlocked.Increment(ref Scp.AssociationCount);
            _calledAe = association.CalledAE;
            _callingAe = association.CallingAE;

            if (Scp.RejectAssociation)
            {
                return SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser, DicomRejectReason.CalledAENotRecognized);
            }

            foreach (var context in association.PresentationContexts)
            {
                Scp.Proposed.Enqueue(string.Join(",", context.GetTransferSyntaxes().Select(t => t.UID.UID)));
                context.AcceptTransferSyntaxes(Scp.AcceptedSyntaxes);
            }
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
        public void OnConnectionClosed(Exception exception) { }

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            Interlocked.Increment(ref Scp.EchoCount);
            return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
        }

        public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            var index = Interlocked.Increment(ref Scp.StoreCount) - 1;
            Scp.Received.Enqueue(new ReceivedImage
            {
                Dataset = request.Dataset.Clone(),
                Syntax = request.Dataset.InternalTransferSyntax,
                Frame = DicomPixelData.Create(request.Dataset).GetFrame(0).Data.ToArray(),
                CalledAe = _calledAe,
                CallingAe = _callingAe,
            });

            if (Scp.HangFromImage >= 0 && index >= Scp.HangFromImage) await Task.Delay(TimeSpan.FromSeconds(30));
            if (Scp.ResponseDelayMs > 0) await Task.Delay(Scp.ResponseDelayMs);

            var status = Scp.StatusFor?.Invoke(index) ?? DicomStatus.Success;
            var response = new DicomCStoreResponse(request, status);
            if (Scp.OffendingElement != null && status != DicomStatus.Success) response.Command.AddOrUpdate(DicomTag.OffendingElement, Scp.OffendingElement.Value);
            return response;
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e) => Task.CompletedTask;
    }
}
