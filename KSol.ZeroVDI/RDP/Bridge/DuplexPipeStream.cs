using System.Threading.Channels;

namespace KSol.ZeroVDI.RDP.Bridge;

/// <summary>
/// An in-memory bidirectional pipe: two <see cref="Stream"/> endpoints where each one's writes appear on
/// the other's reads. Used by the VNC bridge to give the browser console relay a plain RDP byte stream
/// (<see cref="BrowserSide"/>) while the minimal RDP server front-end drives the other end
/// (<see cref="ServerSide"/>). No TLS: the gateway already terminated the browser's transport, so the
/// browser client speaks plaintext RDP starting at MCS Connect-Initial.
///
/// Modelled on <c>MitmRdpStream.ClientFacingStream</c>, generalized to a symmetric pair.
/// </summary>
public sealed class DuplexPipeStream
{
    private readonly Channel<byte[]> _a2b = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Channel<byte[]> _b2a = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public DuplexPipeStream()
    {
        BrowserSide = new Endpoint(_b2a.Reader, _a2b.Writer);
        ServerSide = new Endpoint(_a2b.Reader, _b2a.Writer);
    }

    /// <summary>The end handed to <c>RdpRelaySession</c> as the decrypted RDP stream.</summary>
    public Stream BrowserSide { get; }

    /// <summary>The end the RDP server front-end reads client PDUs from and writes server PDUs to.</summary>
    public Stream ServerSide { get; }

    /// <summary>Tears down both directions so blocked reads on either endpoint return EOF.</summary>
    public void Complete()
    {
        _a2b.Writer.TryComplete();
        _b2a.Writer.TryComplete();
    }

    private sealed class Endpoint : Stream
    {
        private readonly ChannelReader<byte[]> _in;
        private readonly ChannelWriter<byte[]> _out;
        private byte[] _residual = Array.Empty<byte>();
        private int _residualOffset;

        public Endpoint(ChannelReader<byte[]> @in, ChannelWriter<byte[]> @out) { _in = @in; _out = @out; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_residualOffset >= _residual.Length)
            {
                try { _residual = await _in.ReadAsync(ct); _residualOffset = 0; }
                catch (ChannelClosedException) { return 0; }
                catch (OperationCanceledException) { return 0; }
            }
            int n = Math.Min(buffer.Length, _residual.Length - _residualOffset);
            _residual.AsSpan(_residualOffset, n).CopyTo(buffer.Span);
            _residualOffset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            try { await _out.WriteAsync(buffer.ToArray(), ct); }
            catch (ChannelClosedException) { /* peer gone; drop */ }
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _out.TryComplete();
            base.Dispose(disposing);
        }
    }
}
