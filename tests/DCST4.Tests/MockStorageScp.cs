using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    /// <summary>One presentation context as it came in the A-ASSOCIATE-RQ.</summary>
    public sealed class ReceivedContext
    {
        public int Id;
        public string AbstractSyntaxUid;
        public List<string> TransferSyntaxUids;
        public bool? ScuRole;
        public bool? ScpRole;
    }

    /// <summary>One A-ASSOCIATE-RQ and how the association ended.</summary>
    public sealed class ReceivedAssociation
    {
        public int Number;
        public string CallingAe;
        public string CalledAe;
        public List<ReceivedContext> Contexts;
        public List<string> ExtendedNegotiationSopClasses;
        public volatile bool Released;
        public volatile bool AbortedByPeer;
    }

    /// <summary>How the mock answers one presentation context.</summary>
    public sealed class ScpAnswer
    {
        public DicomPresentationContextResult Result;
        /// <summary>Accept with this syntax instead of the proposed one.</summary>
        public DicomTransferSyntax AcceptAs;
        /// <summary>Leave the context out of the A-ASSOCIATE-AC.</summary>
        public bool LeaveUnanswered;

        public static readonly ScpAnswer Accept = new ScpAnswer { Result = DicomPresentationContextResult.Accept };
        public static readonly ScpAnswer NotAnswered = new ScpAnswer { LeaveUnanswered = true };
        public static ScpAnswer Reject(DicomPresentationContextResult result) => new ScpAnswer { Result = result };
        public static ScpAnswer AcceptOther(DicomTransferSyntax syntax) => new ScpAnswer { Result = DicomPresentationContextResult.Accept, AcceptAs = syntax };
    }

    /// <summary>What the mock answers per SOP class and transfer syntax. The most specific entry wins.</summary>
    public sealed class ScpContextTable
    {
        private readonly Dictionary<string, ScpAnswer> _cells = new Dictionary<string, ScpAnswer>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScpAnswer> _sopClasses = new Dictionary<string, ScpAnswer>(StringComparer.Ordinal);

        public ScpAnswer Default = ScpAnswer.Accept;

        public void Set(DicomUID sopClass, DicomTransferSyntax syntax, ScpAnswer answer) => _cells[sopClass.UID + "|" + syntax.UID.UID] = answer;
        public void Set(DicomUID sopClass, ScpAnswer answer) => _sopClasses[sopClass.UID] = answer;

        public ScpAnswer Lookup(string sopClassUid, string syntaxUid)
        {
            if (_cells.TryGetValue(sopClassUid + "|" + syntaxUid, out var cell)) return cell;
            return _sopClasses.TryGetValue(sopClassUid, out var sop) ? sop : Default;
        }
    }

    /// <summary>The fields of an A-ASSOCIATE-RJ.</summary>
    public sealed class ScpRejection
    {
        public DicomRejectResult Result = DicomRejectResult.Permanent;
        public DicomRejectSource Source = DicomRejectSource.ServiceUser;
        public DicomRejectReason Reason = DicomRejectReason.CalledAENotRecognized;
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
        public static int EchoDelayMs;
        /// <summary>Answer C-ECHO with A-ABORT instead of a response.</summary>
        public static bool AbortOnEcho;
        /// <summary>Status of the C-ECHO response. Null means Success.</summary>
        public static DicomStatus EchoStatus;

        /// <summary>Answers per SOP class and syntax. When set, it replaces <see cref="AcceptedSyntaxes"/>.</summary>
        public static ScpContextTable ContextTable;
        /// <summary>Decides from the request and its number, counted from 1, whether to reject. Null from the function means carry on.</summary>
        public static Func<DicomAssociation, int, ScpRejection> RejectWhen;
        /// <summary>Answer this association, counted from 1, with A-ABORT instead of A-ASSOCIATE-AC.</summary>
        public static int AbortAtAssociation;
        /// <summary>Answer the C-ECHO of this association, counted from 1, with A-ABORT.</summary>
        public static int AbortEchoOfAssociation;
        /// <summary>Do not answer the A-ASSOCIATE-RQ at all.</summary>
        public static bool HangOnAssociation;
        /// <summary>The max PDU length to announce, or 0 for the default.</summary>
        public static uint MaxPduLength;
        /// <summary>The role reply per SOP class: null to echo what was proposed, or SCU and SCP. Both null means no reply.</summary>
        public static Func<string, Tuple<bool?, bool?>> RoleReply;
        /// <summary>The application information to answer per SOP class, or null to leave that item unanswered.</summary>
        public static Func<string, byte[]> ExtendedReply;

        public static int AssociationCount;
        public static int EchoCount;
        public static int StoreCount;
        public static readonly ConcurrentQueue<ReceivedImage> Received = new ConcurrentQueue<ReceivedImage>();
        public static readonly ConcurrentQueue<string> Proposed = new ConcurrentQueue<string>();
        public static readonly ConcurrentQueue<ReceivedAssociation> Associations = new ConcurrentQueue<ReceivedAssociation>();

        public sealed class Tag { public DicomTag Value; }

        public static void Reset()
        {
            AcceptedSyntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1 };
            RejectAssociation = false;
            StatusFor = null;
            OffendingElement = null;
            ResponseDelayMs = 0;
            HangFromImage = -1;
            EchoDelayMs = 0;
            AbortOnEcho = false;
            EchoStatus = null;
            ContextTable = null;
            RejectWhen = null;
            AbortAtAssociation = 0;
            AbortEchoOfAssociation = 0;
            HangOnAssociation = false;
            MaxPduLength = 0;
            RoleReply = null;
            ExtendedReply = null;
            AssociationCount = 0;
            EchoCount = 0;
            StoreCount = 0;
            while (Received.TryDequeue(out _)) { }
            while (Proposed.TryDequeue(out _)) { }
            while (Associations.TryDequeue(out _)) { }
        }
    }

    public sealed class MockStorageScp : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        private static readonly uint DefaultMaxPdu = new DicomServiceOptions().MaxPDULength;
        private static readonly MethodInfo SetUserRole = typeof(DicomPresentationContext).GetProperty(nameof(DicomPresentationContext.UserRole)).GetSetMethod(true);
        private static readonly MethodInfo SetProviderRole = typeof(DicomPresentationContext).GetProperty(nameof(DicomPresentationContext.ProviderRole)).GetSetMethod(true);

        private string _calledAe;
        private string _callingAe;
        private ReceivedAssociation _received;

        public MockStorageScp(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies deps)
            : base(stream, fallbackEncoding, log, deps) { }

        public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            var number = Interlocked.Increment(ref Scp.AssociationCount);
            _calledAe = association.CalledAE;
            _callingAe = association.CallingAE;

            _received = new ReceivedAssociation
            {
                Number = number,
                CallingAe = association.CallingAE,
                CalledAe = association.CalledAE,
                Contexts = association.PresentationContexts.Select(c => new ReceivedContext
                {
                    Id = c.ID,
                    AbstractSyntaxUid = c.AbstractSyntax.UID,
                    TransferSyntaxUids = c.GetTransferSyntaxes().Select(t => t.UID.UID).ToList(),
                    ScuRole = c.UserRole,
                    ScpRole = c.ProviderRole,
                }).ToList(),
                ExtendedNegotiationSopClasses = association.ExtendedNegotiations.Select(e => e.SopClassUid.UID).ToList(),
            };
            Scp.Associations.Enqueue(_received);

            if (Scp.HangOnAssociation)
            {
                await Task.Delay(5000);
                return;
            }

            if (Scp.AbortAtAssociation == number)
            {
                await SendAbortAsync(DicomAbortSource.ServiceProvider, DicomAbortReason.NotSpecified);
                return;
            }

            if (Scp.RejectAssociation)
            {
                await SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser, DicomRejectReason.CalledAENotRecognized);
                return;
            }

            var rejection = Scp.RejectWhen?.Invoke(association, number);
            if (rejection != null)
            {
                await SendAssociationRejectAsync(rejection.Result, rejection.Source, rejection.Reason);
                return;
            }

            Options.MaxPDULength = Scp.MaxPduLength > 0 ? Scp.MaxPduLength : DefaultMaxPdu;

            var unanswered = new List<DicomPresentationContext>();
            foreach (var context in association.PresentationContexts)
            {
                Scp.Proposed.Enqueue(string.Join(",", context.GetTransferSyntaxes().Select(t => t.UID.UID)));
                if (Scp.ContextTable == null) context.AcceptTransferSyntaxes(Scp.AcceptedSyntaxes);
                else if (ApplyTable(context)) unanswered.Add(context);
                ApplyRoleReply(context);
            }
            foreach (var context in unanswered) association.PresentationContexts.Remove(context);

            if (Scp.ExtendedReply != null)
            {
                foreach (var negotiation in association.ExtendedNegotiations)
                {
                    var reply = Scp.ExtendedReply(negotiation.SopClassUid.UID);
                    if (reply != null) negotiation.AcceptApplicationInfo(new DicomServiceApplicationInfo(reply));
                }
            }

            await SendAssociationAcceptAsync(association);
        }

        /// <summary>Returns true if the context is to be left out of the answer.</summary>
        private static bool ApplyTable(DicomPresentationContext context)
        {
            var proposed = context.GetTransferSyntaxes().First();
            var answer = Scp.ContextTable.Lookup(context.AbstractSyntax.UID, proposed.UID.UID);
            if (answer.LeaveUnanswered) return true;

            if (answer.Result == DicomPresentationContextResult.Accept) context.SetResult(DicomPresentationContextResult.Accept, answer.AcceptAs ?? proposed);
            else context.SetResult(answer.Result);
            return false;
        }

        private static void ApplyRoleReply(DicomPresentationContext context)
        {
            if (Scp.RoleReply == null || (!context.UserRole.HasValue && !context.ProviderRole.HasValue)) return;
            var reply = Scp.RoleReply(context.AbstractSyntax.UID);
            if (reply == null) return;
            SetUserRole.Invoke(context, new object[] { reply.Item1 });
            SetProviderRole.Invoke(context, new object[] { reply.Item2 });
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            if (_received != null) _received.Released = true;
            return SendAssociationReleaseResponseAsync();
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
        {
            if (_received != null) _received.AbortedByPeer = true;
        }

        public void OnConnectionClosed(Exception exception) { }

        public async Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
        {
            Interlocked.Increment(ref Scp.EchoCount);
            if (Scp.AbortOnEcho || (_received != null && Scp.AbortEchoOfAssociation == _received.Number))
            {
                await SendAbortAsync(DicomAbortSource.ServiceProvider, DicomAbortReason.UnexpectedPDU);
                // A response must not follow the abort. By the time this one goes out, the connection is gone.
                await Task.Delay(3000);
                return new DicomCEchoResponse(request, DicomStatus.Success);
            }

            if (Scp.EchoDelayMs > 0) await Task.Delay(Scp.EchoDelayMs);
            return new DicomCEchoResponse(request, Scp.EchoStatus ?? DicomStatus.Success);
        }

        public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            var index = Interlocked.Increment(ref Scp.StoreCount) - 1;
            Scp.Received.Enqueue(new ReceivedImage
            {
                Dataset = request.Dataset.Clone(),
                Syntax = request.Dataset.InternalTransferSyntax,
                Frame = request.Dataset.Contains(DicomTag.PixelData) ? DicomPixelData.Create(request.Dataset).GetFrame(0).Data.ToArray() : null,
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
