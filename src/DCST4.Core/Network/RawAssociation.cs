using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Tls;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    /// <summary>The fields of an A-ASSOCIATE-RJ in words. The reason numbers mean different things for each source (PS3.8 9.3.4).</summary>
    public static class RejectText
    {
        public static string Result(int value) => value == 1 ? "Permanent" : value == 2 ? "Transient" : "Unknown(" + Number(value) + ")";

        public static string Source(int value)
        {
            switch (value)
            {
                case 1: return "ServiceUser";
                case 2: return "ServiceProviderACSE";
                case 3: return "ServiceProviderPresentation";
                default: return "Unknown(" + Number(value) + ")";
            }
        }

        public static string Reason(int source, int value)
        {
            if (source == 1)
            {
                switch (value)
                {
                    case 1: return "NoReasonGiven";
                    case 2: return "ApplicationContextNotSupported";
                    case 3: return "CallingAENotRecognized";
                    case 7: return "CalledAENotRecognized";
                }
            }
            else if (source == 2)
            {
                switch (value)
                {
                    case 1: return "NoReasonGiven";
                    case 2: return "ProtocolVersionNotSupported";
                }
            }
            else if (source == 3)
            {
                switch (value)
                {
                    case 1: return "TemporaryCongestion";
                    case 2: return "LocalLimitExceeded";
                }
            }
            return "Unknown(" + Number(value) + ")";
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    internal enum RawEventKind
    {
        Pdu,
        /// <summary>The other side closed the connection in an orderly way.</summary>
        ClosedByPeer,
        /// <summary>The connection was reset or failed.</summary>
        Reset,
        /// <summary>We closed it ourselves.</summary>
        ClosedLocally,
    }

    /// <summary>One thing that arrived on a raw association, or how it ended.</summary>
    internal sealed class RawEvent
    {
        public RawEventKind Kind;
        public byte PduType;
        public byte[] Body;
        /// <summary>On the association's clock.</summary>
        public long AtMs;
        /// <summary><see cref="Stopwatch.GetTimestamp"/> when the read loop had the whole PDU, for latency measurement.</summary>
        public long AtTicks;
        public string Error;

        public bool IsEnding => Kind != RawEventKind.Pdu;

        /// <summary>As it reads in a timeline and an explanation.</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case RawEventKind.ClosedByPeer: return "closed the connection";
                case RawEventKind.Reset: return "reset the connection" + (string.IsNullOrEmpty(Error) ? string.Empty : " (" + Error + ")");
                case RawEventKind.ClosedLocally: return "the connection was closed by DCST4";
            }

            switch (PduType)
            {
                case RawAssociation.PduAbort:
                    return Body != null && Body.Length >= 4 ? $"sent A-ABORT (source {Body[2].ToString(CultureInfo.InvariantCulture)}, reason {Body[3].ToString(CultureInfo.InvariantCulture)})" : "sent A-ABORT";
                case RawAssociation.PduReleaseRq: return "sent A-RELEASE-RQ";
                case RawAssociation.PduReleaseRp: return "sent A-RELEASE-RP";
                case RawAssociation.PduAssociateRj: return "sent A-ASSOCIATE-RJ";
                case RawAssociation.PduAssociateAc: return "sent A-ASSOCIATE-AC";
                case RawAssociation.PduData: return "sent P-DATA-TF";
                default: return "sent PDU type " + PduType.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>One presentation context of a raw association.</summary>
    internal sealed class RawContext
    {
        public byte Id;
        public string AbstractSyntaxUid;
        public List<string> TransferSyntaxUids = new List<string>();
        /// <summary>The result byte of the A-ASSOCIATE-AC, or null if the context was not answered.</summary>
        public byte? Result;
        public string AcceptedTransferSyntaxUid;

        public bool Accepted => Result == 0 && AcceptedTransferSyntaxUid != null;
    }

    internal sealed class RawAssociateOutcome
    {
        public bool Accepted;
        public AssociationRejection Rejection;
        /// <summary>Set when the answer was neither an accept nor a reject: an abort or a closed connection.</summary>
        public RawEvent Ending;
        public bool TimedOut;
    }

    internal sealed class RawDimseReply
    {
        public bool TimedOut;
        /// <summary>Set when the association ended instead of a response arriving.</summary>
        public RawEvent Ending;
        public ushort CommandField;
        /// <summary>Raw (0000,0900).</summary>
        public ushort? Status;
        public string ErrorComment;
        /// <summary><see cref="Stopwatch.GetTimestamp"/> of the PDU that completed the response command.</summary>
        public long ArrivedTicks;
    }

    /// <summary>
    /// A DICOM association on a socket of our own, for the limit, fault and load tests.
    ///
    /// fo-dicom's client cannot stop in the middle of a dataset, cannot hold a TCP connection without sending an
    /// A-ASSOCIATE-RQ, does not show the size of the PDUs on the wire and closes an association when its queue is empty.
    /// These tests need all four, so this class writes and reads the PDUs itself (PS3.8 section 9.3). It knows only what
    /// those tests use: association negotiation, C-ECHO, C-STORE, release, abort and three ways of breaking off.
    /// The load tests use it because it sends bytes encoded in advance and shows when a request went on the wire.
    /// Everything else in DCST4 goes through fo-dicom.
    /// </summary>
    internal sealed class RawAssociation : IDisposable
    {
        public const byte PduAssociateRq = 1;
        public const byte PduAssociateAc = 2;
        public const byte PduAssociateRj = 3;
        public const byte PduData = 4;
        public const byte PduReleaseRq = 5;
        public const byte PduReleaseRp = 6;
        public const byte PduAbort = 7;

        /// <summary>What a P-DATA-TF PDU needs besides the data: the PDV length, the context ID and the control header.</summary>
        public const int PdvOverhead = 6;
        /// <summary>The PDU size to send with when the other side announces 0, which means no limit.</summary>
        public const uint DefaultPduLength = 16384;

        private const string ApplicationContext = "1.2.840.10008.3.1.1.1";
        /// <summary>A PDU longer than this is not read: it is a protocol error or not DICOM at all.</summary>
        private const uint LargestReadablePdu = 64 * 1024 * 1024;

        private readonly TcpClient _tcp;
        private readonly Stream _stream;
        private readonly Stopwatch _clock;
        private readonly DicomEndpoint _endpoint;
        private readonly ConcurrentQueue<RawEvent> _events = new ConcurrentQueue<RawEvent>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private volatile RawEvent _ending;
        private volatile bool _abortedByPeer;
        private volatile bool _accepted;
        private int _closed;
        private int _messageId;
        private long _largestReceived;
        /// <summary>Reused for every data PDU of the segmented <c>SendDatasetAsync</c>.</summary>
        private byte[] _sendBuffer;

        private RawAssociation(TcpClient tcp, Stream stream, DicomEndpoint endpoint, Stopwatch clock, AssociationTimeline timeline)
        {
            _tcp = tcp;
            _stream = stream;
            _endpoint = endpoint;
            _clock = clock;
            Timeline = timeline;
            ConnectedAtMs = clock.ElapsedMilliseconds;
            Task.Run(ReadLoopAsync);
        }

        public AssociationTimeline Timeline { get; }
        public List<RawContext> Contexts { get; private set; } = new List<RawContext>();
        /// <summary>What the other side announced in the A-ASSOCIATE-AC. 0 means no limit.</summary>
        public uint PeerMaxPdu { get; private set; }
        public uint OurMaxPdu { get; private set; }
        public string PeerImplementationClassUid { get; private set; }
        public string PeerImplementationVersion { get; private set; }
        /// <summary>The length field of the largest PDU sent and received, which is what the maximum length is about.</summary>
        public uint LargestPduSent { get; private set; }
        public uint LargestPduReceived => (uint)Interlocked.Read(ref _largestReceived);
        public long ConnectedAtMs { get; }
        public long NowMs => _clock.ElapsedMilliseconds;
        public bool IsClosed => Volatile.Read(ref _closed) != 0;
        /// <summary>The other side has aborted or closed, so there is no association left to abort.</summary>
        public bool EndedByPeer => _abortedByPeer || (_ending != null && _ending.Kind != RawEventKind.ClosedLocally);

        /// <summary>The PDU length to send with: what the other side takes, or <see cref="DefaultPduLength"/> if it sets no limit.</summary>
        public uint SendPduLength => PeerMaxPdu == 0 ? DefaultPduLength : PeerMaxPdu;

        /// <summary>
        /// Opens the TCP connection and, with <paramref name="tls"/>, the TLS session. Throws what the socket or the handshake
        /// throws, TimeoutException after the connection timeout, and OperationCanceledException on cancel.
        /// </summary>
        public static async Task<RawAssociation> ConnectAsync(DicomEndpoint endpoint, NetworkOptions options, ITlsInitiator tls, CancellationToken token, Stopwatch clock = null, AssociationTimeline timeline = null)
        {
            clock = clock ?? Stopwatch.StartNew();
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using (token.Register(() => SafeClose(tcp)))
                {
                    var connect = tcp.ConnectAsync(endpoint.Host, endpoint.Port);
                    var limit = options.ConnectionTimeoutSeconds > 0 ? TimeSpan.FromSeconds(options.ConnectionTimeoutSeconds) : Timeout.InfiniteTimeSpan;
                    using (var delayEnd = new CancellationTokenSource())
                    {
                        var finished = await Task.WhenAny(connect, Task.Delay(limit, delayEnd.Token)).ConfigureAwait(false);
                        delayEnd.Cancel();
                        if (finished != connect)
                        {
                            SafeClose(tcp);
                            Observe(connect);
                            throw new TimeoutException($"no connection within {options.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} s");
                        }
                    }

                    Stream stream;
                    try
                    {
                        await connect.ConfigureAwait(false);
                        // A cancel right after the connection is made closes it on another thread, and GetStream on a
                        // client that is being closed throws whatever it runs into, a NullReferenceException included.
                        stream = tcp.GetStream();
                    }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }

                    if (tls != null)
                    {
                        var plain = stream;
                        var handshakeMs = (options.AssociationTimeoutSeconds > 0 ? options.AssociationTimeoutSeconds : 30) * 1000;
                        plain.ReadTimeout = handshakeMs;
                        plain.WriteTimeout = handshakeMs;
                        try
                        {
                            stream = await Task.Run(() => tls.InitiateTls(plain, endpoint.Host, endpoint.Port)).ConfigureAwait(false);
                        }
                        catch (Exception) when (token.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(token);
                        }
                        plain.ReadTimeout = Timeout.Infinite;
                        plain.WriteTimeout = Timeout.Infinite;
                    }

                    token.ThrowIfCancellationRequested();
                    return new RawAssociation(tcp, stream, endpoint, clock, timeline);
                }
            }
            catch (Exception ex) when (token.IsCancellationRequested && !(ex is OperationCanceledException))
            {
                // A cancel closes the connection on the thread that cancels, at whatever point this one has reached, also
                // before the connection is asked for. What then fails here is the cancel, not the other side.
                SafeClose(tcp);
                throw new OperationCanceledException(token);
            }
            catch
            {
                SafeClose(tcp);
                throw;
            }
        }

        /// <summary>
        /// Ends the association when the token is cancelled: an A-ABORT if there is an association, then the connection is
        /// closed, which also ends a write that is waiting. Dispose the result when done.
        /// </summary>
        public CancellationTokenRegistration CloseOn(CancellationToken token) => token.Register(AbortNow);

        /// <summary>Best effort and without waiting: this runs on whichever thread cancels.</summary>
        private void AbortNow()
        {
            if (_accepted && !EndedByPeer && !IsClosed)
            {
                try
                {
                    // Null once the connection is closed, which a cleanup on another thread may just have done.
                    var socket = _tcp.Client;
                    if (socket != null) socket.SendTimeout = 500;
                    var pdu = new byte[] { PduAbort, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 };
                    _stream.Write(pdu, 0, pdu.Length);
                    _stream.Flush();
                    Timeline?.RecordStep(ProtocolEvent.Sent, "A-ABORT", "Cancelled");
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    // A write was in progress, or the other side is gone. The connection is closed below either way.
                }
            }
            Dispose();
        }

        // ---------------------------------------------------------------- receiving

        private async Task ReadLoopAsync()
        {
            var header = new byte[6];
            try
            {
                while (true)
                {
                    var got = await FillAsync(header, header.Length).ConfigureAwait(false);
                    if (got < header.Length)
                    {
                        Post(new RawEvent { Kind = RawEventKind.ClosedByPeer, Error = got > 0 ? "in the middle of a PDU" : null });
                        return;
                    }

                    var length = ReadUInt32(header, 2);
                    if (length > LargestReadablePdu)
                    {
                        Post(new RawEvent { Kind = RawEventKind.Reset, Error = "a PDU of " + length.ToString(CultureInfo.InvariantCulture) + " bytes was announced, which is not read" });
                        return;
                    }

                    var body = new byte[length];
                    if (await FillAsync(body, body.Length).ConfigureAwait(false) < body.Length)
                    {
                        Post(new RawEvent { Kind = RawEventKind.ClosedByPeer, Error = "in the middle of a PDU" });
                        return;
                    }

                    long seen;
                    while (length > (seen = Interlocked.Read(ref _largestReceived)))
                    {
                        if (Interlocked.CompareExchange(ref _largestReceived, length, seen) == seen) break;
                    }
                    Post(new RawEvent { Kind = RawEventKind.Pdu, PduType = header[0], Body = body });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                Post(new RawEvent { Kind = IsClosed ? RawEventKind.ClosedLocally : RawEventKind.Reset, Error = IsClosed ? null : NetworkErrors.GetUserMessage(ex) });
            }
        }

        private async Task<int> FillAsync(byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = await _stream.ReadAsync(buffer, total, count - total).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        private void Post(RawEvent entry)
        {
            entry.AtTicks = Stopwatch.GetTimestamp();
            entry.AtMs = _clock.ElapsedMilliseconds;
            if (entry.IsEnding) _ending = entry;
            if (entry.Kind == RawEventKind.Pdu && entry.PduType == PduAbort) _abortedByPeer = true;
            _events.Enqueue(entry);
            _signal.Release();
        }

        /// <summary>
        /// The next PDU, or how the connection ended. Null if nothing happened within the timeout. Once the connection has
        /// ended, every call returns that ending.
        /// </summary>
        public async Task<RawEvent> NextAsync(TimeSpan timeout, CancellationToken token)
        {
            if (_events.IsEmpty && _ending != null) return _ending;
            if (!await _signal.WaitAsync(timeout, token).ConfigureAwait(false)) return null;
            return _events.TryDequeue(out var entry) ? entry : _ending;
        }

        // ---------------------------------------------------------------- sending

        private async Task WritePduAsync(byte type, byte[] body, int count)
        {
            var pdu = new byte[6 + count];
            pdu[0] = type;
            WriteUInt32(pdu, 2, (uint)count);
            Buffer.BlockCopy(body, 0, pdu, 6, count);
            await _stream.WriteAsync(pdu, 0, pdu.Length).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
            if ((uint)count > LargestPduSent) LargestPduSent = (uint)count;
        }

        /// <summary>Sends the A-ASSOCIATE-RQ and waits for the answer.</summary>
        /// <param name="ourMaxPdu">The largest PDU we say we take.</param>
        public async Task<RawAssociateOutcome> AssociateAsync(IEnumerable<RawContext> contexts, uint ourMaxPdu, TimeSpan timeout, CancellationToken token)
        {
            Contexts = contexts.ToList();
            OurMaxPdu = ourMaxPdu;

            var body = BuildAssociateRq();
            await WritePduAsync(PduAssociateRq, body, body.Length).ConfigureAwait(false);
            Timeline?.RecordStep(ProtocolEvent.Sent, "A-ASSOCIATE-RQ", $"Calling AE {_endpoint.CallingAe}, called AE {_endpoint.CalledAe}, max PDU {ourMaxPdu.ToString(CultureInfo.InvariantCulture)}");

            var outcome = new RawAssociateOutcome();
            var answer = await NextAsync(timeout, token).ConfigureAwait(false);
            if (answer == null)
            {
                outcome.TimedOut = true;
                return outcome;
            }

            if (answer.Kind == RawEventKind.Pdu && answer.PduType == PduAssociateAc)
            {
                ParseAssociateAc(answer.Body);
                outcome.Accepted = true;
                _accepted = true;
                Timeline?.RecordAccepted(Negotiation(),
                    $"Max PDU {PeerMaxPdu.ToString(CultureInfo.InvariantCulture)}, implementation {PeerImplementationClassUid} {PeerImplementationVersion}".TrimEnd());
                return outcome;
            }

            if (answer.Kind == RawEventKind.Pdu && answer.PduType == PduAssociateRj && answer.Body.Length >= 4)
            {
                outcome.Rejection = new AssociationRejection
                {
                    Result = RejectText.Result(answer.Body[1]),
                    Source = RejectText.Source(answer.Body[2]),
                    Reason = RejectText.Reason(answer.Body[2], answer.Body[3]),
                };
                Timeline?.RecordRejected(outcome.Rejection);
                return outcome;
            }

            outcome.Ending = answer;
            RecordEnding(answer);
            return outcome;
        }

        /// <summary>The negotiation in the shape the result files use.</summary>
        public List<NegotiatedContext> Negotiation() => Contexts.Select(c => new NegotiatedContext
        {
            Id = c.Id,
            AbstractSyntaxUid = c.AbstractSyntaxUid,
            AbstractSyntaxName = DicomUID.Parse(c.AbstractSyntaxUid).Name,
            ProposedTransferSyntaxUids = c.TransferSyntaxUids.ToList(),
            Result = c.Result.HasValue ? ((DicomPresentationContextResult)c.Result.Value).ToString() : "NotAnswered",
            AcceptedTransferSyntaxUid = c.Accepted ? c.AcceptedTransferSyntaxUid : null,
        }).ToList();

        /// <summary>Records in the timeline how the other side ended the association.</summary>
        public void RecordEnding(RawEvent ending)
        {
            if (Timeline == null || ending == null) return;
            if (ending.Kind == RawEventKind.Pdu)
            {
                var type = ending.PduType == PduAbort ? "A-ABORT" : ending.PduType == PduReleaseRq ? "A-RELEASE-RQ" : "PDU-" + ending.PduType.ToString(CultureInfo.InvariantCulture);
                var detail = ending.PduType == PduAbort && ending.Body.Length >= 4
                    ? $"Source {ending.Body[2].ToString(CultureInfo.InvariantCulture)}, reason {ending.Body[3].ToString(CultureInfo.InvariantCulture)}"
                    : null;
                Timeline.RecordStep(ProtocolEvent.Received, type, detail, ending.AtMs);
            }
            else if (ending.Kind != RawEventKind.ClosedLocally)
            {
                Timeline.RecordStep(ProtocolEvent.Received, ending.Kind == RawEventKind.Reset ? "CONNECTION-RESET" : "CONNECTION-CLOSED", ending.Error, ending.AtMs);
            }
        }

        public async Task<int> SendEchoAsync(byte contextId)
        {
            var messageId = (ushort)Interlocked.Increment(ref _messageId);
            var command = BuildCommand(new SortedDictionary<ushort, byte[]>
            {
                { 0x0002, Uid(DicomUID.Verification.UID) },
                { 0x0100, UInt16(0x0030) },
                { 0x0110, UInt16(messageId) },
                { 0x0800, UInt16(0x0101) },
            });
            var index = Timeline?.RecordRequest("C-ECHO", "Verification SOP Class") ?? 0;
            await SendFragmentsAsync(contextId, command, true, SendPduLength, -1).ConfigureAwait(false);
            return index;
        }

        /// <summary>Sends the C-STORE-RQ command. The dataset follows with <see cref="SendDatasetAsync"/>.</summary>
        public async Task<int> SendStoreCommandAsync(byte contextId, string sopClassUid, string sopInstanceUid)
        {
            var messageId = (ushort)Interlocked.Increment(ref _messageId);
            var command = BuildCommand(new SortedDictionary<ushort, byte[]>
            {
                { 0x0002, Uid(sopClassUid) },
                { 0x0100, UInt16(0x0001) },
                { 0x0110, UInt16(messageId) },
                { 0x0700, UInt16(0x0000) },
                { 0x0800, UInt16(0x0001) },
                { 0x1000, Uid(sopInstanceUid) },
            });
            var index = Timeline?.RecordRequest("C-STORE", sopClassUid) ?? 0;
            await SendFragmentsAsync(contextId, command, true, SendPduLength, -1).ConfigureAwait(false);
            return index;
        }

        /// <summary>
        /// Sends the dataset in P-DATA-TF PDUs whose length field is exactly <paramref name="pduLength"/>, except the last.
        /// With <paramref name="stopAfter"/> it stops at the first PDU boundary at or after that many bytes, and returns
        /// how many bytes of the dataset went out.
        /// </summary>
        public Task<int> SendDatasetAsync(byte contextId, byte[] dataset, uint pduLength, int stopAfter = -1) =>
            SendFragmentsAsync(contextId, dataset, false, pduLength, stopAfter);

        /// <summary>
        /// Sends a dataset made of several parts, such as a header encoded per send and pixel data shared by every send, in
        /// P-DATA-TF PDUs whose length field is <paramref name="pduLength"/>, except the last. Each PDU is copied into one
        /// buffer that the association keeps and written with one call, so nothing is allocated per PDU.
        /// </summary>
        public async Task SendDatasetAsync(byte contextId, IReadOnlyList<ArraySegment<byte>> parts, uint pduLength)
        {
            var room = (int)Math.Min(pduLength, int.MaxValue / 2) - PdvOverhead;
            if (room < 1) throw new InvalidOperationException("The PDU length leaves no room for data.");

            long remaining = 0;
            foreach (var part in parts) remaining += part.Count;

            var size = 6 + PdvOverhead + (int)Math.Min(room, Math.Max(remaining, 1));
            if (_sendBuffer == null || _sendBuffer.Length < size) _sendBuffer = new byte[size];
            var buffer = _sendBuffer;

            var partIndex = 0;
            var partOffset = 0;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(room, remaining);
                var last = chunk == remaining;

                buffer[0] = PduData;
                buffer[1] = 0;
                WriteUInt32(buffer, 2, (uint)(PdvOverhead + chunk));
                WriteUInt32(buffer, 6, (uint)(chunk + 2));
                buffer[10] = contextId;
                buffer[11] = (byte)(last ? 0x02 : 0x00);

                var filled = 0;
                while (filled < chunk)
                {
                    var part = parts[partIndex];
                    var take = Math.Min(chunk - filled, part.Count - partOffset);
                    Buffer.BlockCopy(part.Array, part.Offset + partOffset, buffer, 12 + filled, take);
                    filled += take;
                    partOffset += take;
                    if (partOffset == part.Count)
                    {
                        partIndex++;
                        partOffset = 0;
                    }
                }

                await _stream.WriteAsync(buffer, 0, 12 + chunk).ConfigureAwait(false);
                if ((uint)(PdvOverhead + chunk) > LargestPduSent) LargestPduSent = (uint)(PdvOverhead + chunk);
                remaining -= chunk;
            }
            await _stream.FlushAsync().ConfigureAwait(false);
        }

        private async Task<int> SendFragmentsAsync(byte contextId, byte[] data, bool isCommand, uint pduLength, int stopAfter)
        {
            var room = (int)Math.Min(pduLength, int.MaxValue / 2) - PdvOverhead;
            if (room < 1) throw new InvalidOperationException("The PDU length leaves no room for data.");

            var offset = 0;
            while (offset < data.Length)
            {
                if (stopAfter >= 0 && offset >= stopAfter) return offset;

                var chunk = Math.Min(room, data.Length - offset);
                var last = offset + chunk == data.Length;
                var body = BuildDataPdu(contextId, data, offset, chunk, isCommand, last);
                await WritePduAsync(PduData, body, body.Length).ConfigureAwait(false);
                offset += chunk;
            }
            return offset;
        }

        /// <summary>
        /// Starts a data PDU of <paramref name="pduLength"/> and stops after its header and half of its content, leaving the
        /// other side in the middle of a PDU. Returns how many bytes of the dataset went out.
        /// </summary>
        public async Task<int> SendHalfPduAsync(byte contextId, byte[] dataset, int offset, uint pduLength)
        {
            var room = (int)Math.Min(pduLength, int.MaxValue / 2) - PdvOverhead;
            var chunk = Math.Min(room, dataset.Length - offset);
            if (chunk < 2) return 0;

            var body = BuildDataPdu(contextId, dataset, offset, chunk, false, offset + chunk == dataset.Length);
            var pdu = new byte[6 + body.Length];
            pdu[0] = PduData;
            WriteUInt32(pdu, 2, (uint)body.Length);
            Buffer.BlockCopy(body, 0, pdu, 6, body.Length);

            var half = 6 + PdvOverhead + chunk / 2;
            await _stream.WriteAsync(pdu, 0, half).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
            return chunk / 2;
        }

        private static byte[] BuildDataPdu(byte contextId, byte[] data, int offset, int count, bool isCommand, bool last)
        {
            var body = new byte[PdvOverhead + count];
            WriteUInt32(body, 0, (uint)(count + 2));
            body[4] = contextId;
            body[5] = (byte)((isCommand ? 0x01 : 0x00) | (last ? 0x02 : 0x00));
            Buffer.BlockCopy(data, offset, body, PdvOverhead, count);
            return body;
        }

        /// <summary>Waits for a DIMSE response and reads its status. Data PDVs are skipped.</summary>
        public async Task<RawDimseReply> ReadResponseAsync(TimeSpan timeout, CancellationToken token, int dimseIndex = 0, string command = null)
        {
            var reply = new RawDimseReply();
            var commandBytes = new MemoryStream();
            var deadline = _clock.Elapsed + timeout;

            while (true)
            {
                var left = deadline - _clock.Elapsed;
                var next = left > TimeSpan.Zero ? await NextAsync(left, token).ConfigureAwait(false) : null;
                if (next == null)
                {
                    reply.TimedOut = true;
                    return reply;
                }
                if (next.Kind != RawEventKind.Pdu || next.PduType != PduData)
                {
                    reply.Ending = next;
                    RecordEnding(next);
                    return reply;
                }

                var position = 0;
                var complete = false;
                while (position + PdvOverhead <= next.Body.Length)
                {
                    var length = (int)ReadUInt32(next.Body, position);
                    if (length < 2 || position + 4 + length > next.Body.Length) break;
                    var control = next.Body[position + 5];
                    if ((control & 0x01) != 0)
                    {
                        commandBytes.Write(next.Body, position + PdvOverhead, length - 2);
                        if ((control & 0x02) != 0) complete = true;
                    }
                    position += 4 + length;
                }
                if (!complete) continue;

                reply.ArrivedTicks = next.AtTicks;
                ParseCommand(commandBytes.ToArray(), reply);
                if (command != null && reply.Status.HasValue) Timeline?.RecordResponse(dimseIndex, command, reply.Status.Value, StoreStatus.Describe(reply.Status.Value));
                return reply;
            }
        }

        // ---------------------------------------------------------------- endings

        /// <summary>A-RELEASE-RQ, then waits for the A-RELEASE-RP and closes. Returns whether the RP came.</summary>
        public async Task<bool> ReleaseAsync(TimeSpan timeout, CancellationToken token)
        {
            var released = false;
            try
            {
                await WritePduAsync(PduReleaseRq, new byte[4], 4).ConfigureAwait(false);
                Timeline?.RecordStep(ProtocolEvent.Sent, "A-RELEASE-RQ", null);

                var deadline = _clock.Elapsed + timeout;
                while (true)
                {
                    var left = deadline - _clock.Elapsed;
                    var next = left > TimeSpan.Zero ? await NextAsync(left, token).ConfigureAwait(false) : null;
                    if (next == null) break;
                    if (next.Kind == RawEventKind.Pdu && next.PduType == PduReleaseRp)
                    {
                        released = true;
                        Timeline?.RecordStep(ProtocolEvent.Received, "A-RELEASE-RP", null, next.AtMs);
                        break;
                    }
                    if (next.IsEnding || next.PduType == PduAbort)
                    {
                        RecordEnding(next);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                // The other side was already gone. There is nothing left to release.
            }
            Dispose();
            return released;
        }

        /// <summary>A-ABORT from the service user, delivered before the connection closes.</summary>
        public async Task AbortAsync(string reason)
        {
            try
            {
                await WritePduAsync(PduAbort, new byte[4], 4).ConfigureAwait(false);
                Timeline?.RecordStep(ProtocolEvent.Sent, "A-ABORT", reason);
                await CloseAfterDeliveryAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                Dispose();
            }
        }

        /// <summary>Resets the TCP connection at once: no A-ABORT, no TLS close and no orderly end. What is not yet delivered is lost.</summary>
        public void Drop(string reason)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Timeline?.RecordStep(ProtocolEvent.Sent, "CONNECTION-RESET", reason);
            try
            {
                var socket = _tcp.Client;
                if (socket != null) socket.LingerState = new LingerOption(true, 0);
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
            {
            }
            SafeClose(_tcp);
        }

        /// <summary>
        /// Closes our side so that everything sent is delivered first, then waits up to <paramref name="drain"/> for the other
        /// side to close. What it still sends is taken off the socket and stays in the queue, unread by the caller.
        /// </summary>
        public async Task CloseAfterDeliveryAsync(TimeSpan drain)
        {
            if (IsClosed) return;
            try
            {
                if (_stream is SslStream ssl) await ssl.ShutdownAsync().ConfigureAwait(false);
                // Null if a cancel closed the connection in the meantime.
                _tcp.Client?.Shutdown(SocketShutdown.Send);

                var deadline = _clock.Elapsed + drain;
                while (_ending == null && _clock.Elapsed < deadline) await Task.Delay(20).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                // Already gone.
            }
            Dispose();
        }

        /// <summary>Whether a PDU of this type arrived and has not been taken with <see cref="NextAsync"/>.</summary>
        public bool HasUnread(byte pduType) => _events.Any(e => e.Kind == RawEventKind.Pdu && e.PduType == pduType);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _stream.Dispose(); } catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException) { }
            SafeClose(_tcp);
        }

        private static void SafeClose(TcpClient tcp)
        {
            try { tcp.Close(); } catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is IOException) { }
        }

        private static void Observe(Task task) => task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

        // ---------------------------------------------------------------- encoding

        private byte[] BuildAssociateRq()
        {
            var body = new MemoryStream();
            body.Write(new byte[] { 0x00, 0x01, 0x00, 0x00 }, 0, 4);
            WriteAe(body, _endpoint.CalledAe);
            WriteAe(body, _endpoint.CallingAe);
            body.Write(new byte[32], 0, 32);

            WriteItem(body, 0x10, Encoding.ASCII.GetBytes(ApplicationContext));

            foreach (var context in Contexts)
            {
                var item = new MemoryStream();
                item.Write(new byte[] { context.Id, 0, 0, 0 }, 0, 4);
                WriteItem(item, 0x30, Encoding.ASCII.GetBytes(context.AbstractSyntaxUid));
                foreach (var syntax in context.TransferSyntaxUids) WriteItem(item, 0x40, Encoding.ASCII.GetBytes(syntax));
                WriteItem(body, 0x20, item.ToArray());
            }

            var user = new MemoryStream();
            var maxLength = new byte[4];
            WriteUInt32(maxLength, 0, OurMaxPdu);
            WriteItem(user, 0x51, maxLength);
            WriteItem(user, 0x52, Encoding.ASCII.GetBytes(DicomImplementation.ClassUID.UID));
            WriteItem(user, 0x55, Encoding.ASCII.GetBytes(DicomImplementation.Version));
            WriteItem(body, 0x50, user.ToArray());

            return body.ToArray();
        }

        private void ParseAssociateAc(byte[] body)
        {
            var position = 68;
            while (position + 4 <= body.Length)
            {
                var type = body[position];
                var length = ReadUInt16(body, position + 2);
                var start = position + 4;
                if (start + length > body.Length) break;

                if (type == 0x21 && length >= 4)
                {
                    var context = Contexts.FirstOrDefault(c => c.Id == body[start]);
                    if (context != null)
                    {
                        context.Result = body[start + 2];
                        var sub = start + 4;
                        while (sub + 4 <= start + length)
                        {
                            var subLength = ReadUInt16(body, sub + 2);
                            if (body[sub] == 0x40) context.AcceptedTransferSyntaxUid = Text(body, sub + 4, subLength);
                            sub += 4 + subLength;
                        }
                    }
                }
                else if (type == 0x50)
                {
                    var sub = start;
                    while (sub + 4 <= start + length)
                    {
                        var subLength = ReadUInt16(body, sub + 2);
                        if (body[sub] == 0x51 && subLength >= 4) PeerMaxPdu = ReadUInt32(body, sub + 4);
                        else if (body[sub] == 0x52) PeerImplementationClassUid = Text(body, sub + 4, subLength);
                        else if (body[sub] == 0x55) PeerImplementationVersion = Text(body, sub + 4, subLength);
                        sub += 4 + subLength;
                    }
                }
                position = start + length;
            }
        }

        /// <summary>A command set: Implicit VR Little Endian, with its group length first.</summary>
        private static byte[] BuildCommand(SortedDictionary<ushort, byte[]> elements)
        {
            var content = new MemoryStream();
            foreach (var element in elements)
            {
                content.Write(new byte[] { 0x00, 0x00, (byte)(element.Key & 0xFF), (byte)(element.Key >> 8) }, 0, 4);
                content.Write(BitConverter.GetBytes((uint)element.Value.Length), 0, 4);
                content.Write(element.Value, 0, element.Value.Length);
            }

            var command = new MemoryStream();
            command.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 }, 0, 8);
            command.Write(BitConverter.GetBytes((uint)content.Length), 0, 4);
            content.WriteTo(command);
            return command.ToArray();
        }

        private static void ParseCommand(byte[] command, RawDimseReply reply)
        {
            var position = 0;
            while (position + 8 <= command.Length)
            {
                var group = BitConverter.ToUInt16(command, position);
                var element = BitConverter.ToUInt16(command, position + 2);
                var length = (int)BitConverter.ToUInt32(command, position + 4);
                var start = position + 8;
                if (length < 0 || start + length > command.Length) break;

                if (group == 0x0000)
                {
                    if (element == 0x0100 && length >= 2) reply.CommandField = BitConverter.ToUInt16(command, start);
                    else if (element == 0x0900 && length >= 2) reply.Status = BitConverter.ToUInt16(command, start);
                    else if (element == 0x0902) reply.ErrorComment = Text(command, start, length);
                }
                position = start + length;
            }
        }

        private static byte[] Uid(string uid)
        {
            var bytes = Encoding.ASCII.GetBytes(uid);
            if (bytes.Length % 2 == 0) return bytes;
            var padded = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
            return padded;
        }

        private static byte[] UInt16(ushort value) => BitConverter.GetBytes(value);

        private static void WriteAe(Stream target, string ae)
        {
            var bytes = Encoding.ASCII.GetBytes((ae ?? string.Empty).PadRight(DicomEndpoint.MaxAeTitleLength).Substring(0, DicomEndpoint.MaxAeTitleLength));
            target.Write(bytes, 0, bytes.Length);
        }

        private static void WriteItem(Stream target, byte type, byte[] value)
        {
            target.WriteByte(type);
            target.WriteByte(0);
            target.WriteByte((byte)(value.Length >> 8));
            target.WriteByte((byte)(value.Length & 0xFF));
            target.Write(value, 0, value.Length);
        }

        private static string Text(byte[] buffer, int offset, int count) => Encoding.ASCII.GetString(buffer, offset, count).TrimEnd('\0', ' ');

        private static ushort ReadUInt16(byte[] buffer, int offset) => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

        private static uint ReadUInt32(byte[] buffer, int offset) =>
            ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        /// <summary>The dataset of a file as it goes on the wire: without the preamble and the File Meta Information.</summary>
        public static byte[] DatasetBytes(DicomFile file)
        {
            using (var stream = new MemoryStream())
            {
                file.Save(stream);
                var bytes = stream.ToArray();
                // 128 bytes of preamble, "DICM", then (0002,0000) UL with the length of the rest of the group.
                const int groupLengthValue = 128 + 4 + 8;
                var metaLength = BitConverter.ToUInt32(bytes, groupLengthValue);
                var start = groupLengthValue + 4 + (int)metaLength;
                var dataset = new byte[bytes.Length - start];
                Buffer.BlockCopy(bytes, start, dataset, 0, dataset.Length);
                return dataset;
            }
        }
    }
}
