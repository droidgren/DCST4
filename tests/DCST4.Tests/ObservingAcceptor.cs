using FellowOakDicom.Network;
using FellowOakDicom.Network.Tls;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>
    /// Puts <see cref="ObservingStream"/> between the socket and the mock SCP. fo-dicom lets a TLS acceptor replace the
    /// stream of a connection, and nothing says the replacement must be TLS. With <see cref="Inner"/> it is TLS as well.
    /// </summary>
    public sealed class ObservingAcceptor : ITlsAcceptor
    {
        public ITlsAcceptor Inner;

        public Stream AcceptTls(Stream encryptedStream, string remoteAddress, int localPort) =>
            new ObservingStream(Inner != null ? Inner.AcceptTls(encryptedStream, remoteAddress, localPort) : encryptedStream);
    }

    /// <summary>
    /// Reads along with what a client sends, PDU by PDU, to do what fo-dicom's server has no hook for: an ARTIM timer, an
    /// idle timeout, a maximum PDU length that is enforced, and a record of a C-STORE that was broken off.
    /// </summary>
    public sealed class ObservingStream : Stream
    {
        private static readonly uint DefaultMaxPdu = new DicomServiceOptions().MaxPDULength;

        private readonly Stream _inner;
        private readonly int _generation = Scp.Generation;
        private readonly object _sync = new object();
        private readonly byte[] _header = new byte[6];
        private readonly Timer _timer;
        private int _headerBytes;
        private MemoryStream _body;
        private uint _bodyLength;
        private bool _anything;
        private bool _associated;
        private int _ended;
        private MemoryStream _command = new MemoryStream();
        private string _storeInProgress;
        private long _datasetBytes;

        public ObservingStream(Stream inner)
        {
            _inner = inner;
            Interlocked.Increment(ref Scp.OpenConnections);
            _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
            if (Scp.ArtimTimeoutMs > 0) _timer.Change(Scp.ArtimTimeoutMs, Timeout.Infinite);
        }

        private void OnTimer()
        {
            bool abort;
            lock (_sync)
            {
                // The ARTIM timer only counts while nothing has arrived, the idle timer only on an association.
                abort = _associated && Scp.IdleEndsWithAbort;
            }
            try
            {
                if (abort)
                {
                    var pdu = new byte[] { 0x07, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x02, 0x00 };
                    _inner.Write(pdu, 0, pdu.Length);
                    _inner.Flush();
                }
                _inner.Dispose();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
        }

        private void RestartIdleTimer()
        {
            try
            {
                if (_associated && Scp.IdleTimeoutMs > 0) _timer.Change(Scp.IdleTimeoutMs, Timeout.Infinite);
                else if (_anything) _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Follows the PDUs in what was just read. Throws to make fo-dicom drop a connection the mock refuses.</summary>
        private void Observe(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                _anything = true;
                var end = offset + count;
                while (offset < end)
                {
                    if (_body == null)
                    {
                        var take = Math.Min(6 - _headerBytes, end - offset);
                        Buffer.BlockCopy(buffer, offset, _header, _headerBytes, take);
                        _headerBytes += take;
                        offset += take;
                        if (_headerBytes < 6) break;

                        _bodyLength = ((uint)_header[2] << 24) | ((uint)_header[3] << 16) | ((uint)_header[4] << 8) | _header[5];
                        if (_header[0] == 0x04)
                        {
                            Scp.DataPdus.Enqueue(_bodyLength);
                            var limit = Scp.MaxPduLength > 0 ? Scp.MaxPduLength : DefaultMaxPdu;
                            if (Scp.EnforceMaxPdu && _bodyLength > limit)
                            {
                                var pdu = new byte[] { 0x07, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x02, 0x06 };
                                _inner.Write(pdu, 0, pdu.Length);
                                _inner.Flush();
                                throw new IOException("The mock refuses a PDU longer than it announced.");
                            }
                        }
                        _body = new MemoryStream();
                    }

                    var need = (int)Math.Min(_bodyLength - _body.Length, end - offset);
                    _body.Write(buffer, offset, need);
                    offset += need;
                    if (_body.Length < _bodyLength) break;

                    Complete(_header[0], _body.ToArray());
                    _body = null;
                    _headerBytes = 0;
                }
                RestartIdleTimer();
            }
        }

        private void Complete(byte type, byte[] body)
        {
            if (type == 0x01)
            {
                _associated = true;
            }
            else if (type == 0x07)
            {
                Ended("abort");
            }
            else if (type == 0x04)
            {
                var position = 0;
                while (position + 6 <= body.Length)
                {
                    var length = (int)(((uint)body[position] << 24) | ((uint)body[position + 1] << 16) | ((uint)body[position + 2] << 8) | body[position + 3]);
                    if (length < 2 || position + 4 + length > body.Length) break;
                    var control = body[position + 5];
                    var last = (control & 0x02) != 0;

                    if ((control & 0x01) != 0)
                    {
                        _command.Write(body, position + 6, length - 2);
                        if (last)
                        {
                            ReadCommand(_command.ToArray());
                            _command = new MemoryStream();
                        }
                    }
                    else if (_storeInProgress != null)
                    {
                        _datasetBytes += length - 2;
                        if (last) _storeInProgress = null;
                    }
                    position += 4 + length;
                }
            }
        }

        private void ReadCommand(byte[] command)
        {
            ushort field = 0;
            ushort dataSetType = 0x0101;
            string instance = null;

            var position = 0;
            while (position + 8 <= command.Length)
            {
                var element = BitConverter.ToUInt16(command, position + 2);
                var length = (int)BitConverter.ToUInt32(command, position + 4);
                if (position + 8 + length > command.Length) break;
                if (element == 0x0100) field = BitConverter.ToUInt16(command, position + 8);
                else if (element == 0x0800) dataSetType = BitConverter.ToUInt16(command, position + 8);
                else if (element == 0x1000) instance = Encoding.ASCII.GetString(command, position + 8, length).TrimEnd('\0', ' ');
                position += 8 + length;
            }

            if (field == 0x0001 && dataSetType != 0x0101)
            {
                _storeInProgress = instance ?? string.Empty;
                _datasetBytes = 0;
            }
        }

        /// <summary>The connection is over. A C-STORE that was still arriving is recorded as partial.</summary>
        private void Ended(string how)
        {
            PartialStore partial = null;
            lock (_sync)
            {
                if (_storeInProgress != null)
                {
                    partial = new PartialStore
                    {
                        SopInstanceUid = _storeInProgress,
                        DatasetBytes = _datasetBytes,
                        BytesOfUnfinishedPdu = how == "abort" ? 0 : _body != null ? 6 + _body.Length : _headerBytes,
                        Ending = how,
                    };
                    _storeInProgress = null;
                }
            }

            if (partial != null)
            {
                Scp.PartialStores.Enqueue(partial);
                Scp.OnPartialStore?.Invoke(partial);
            }

            if (how != "abort" && Interlocked.Exchange(ref _ended, 1) == 0)
            {
                try { _timer.Dispose(); } catch (ObjectDisposedException) { }
                if (_generation == Scp.Generation) Interlocked.Decrement(ref Scp.OpenConnections);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read;
            try
            {
                read = _inner.Read(buffer, offset, count);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                Ended("reset");
                throw;
            }
            if (read == 0) Ended("closed");
            else Observe(buffer, offset, read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read;
            try
            {
                read = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
            {
                Ended("reset");
                throw;
            }
            if (read == 0) Ended("closed");
            else Observe(buffer, offset, read);
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanTimeout => _inner.CanTimeout;
        public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
        public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Ended("closed");
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
