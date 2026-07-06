using System.Net.WebSockets;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Adapts a binary-framed <see cref="WebSocket"/> into a duplex <see cref="Stream"/> so a connector data
/// channel can stand in for a raw TCP <see cref="System.Net.Sockets.NetworkStream"/> — the transport that
/// <see cref="RdpHostConnection"/> layers X.224/TLS/CredSSP over, and that an <see cref="System.Net.Http.HttpClient"/>
/// tunnels through for a connector-proxied Proxmox API.
///
/// Writes send one binary frame each (EndOfMessage=true); reads return whatever bytes are available from
/// the current frame, spanning frames transparently. A close/abort surfaces as end-of-stream on read,
/// which tears the RDP relay / HTTP request down cleanly.
/// </summary>
public sealed class WebSocketStream : Stream
{
    private readonly WebSocket _ws;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ArraySegment<byte> _pending; // leftover bytes from a partially-consumed frame

    public WebSocketStream(WebSocket ws) => _ws = ws;

    /// <summary>Completes when the stream is disposed, so the owning request can stay alive until then.</summary>
    public Task Completion => _completion.Task;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pending.Count > 0)
            return DrainPending(buffer.Span);

        // Rent a scratch buffer to receive the next frame; carry any overflow into _pending.
        var scratch = new byte[Math.Max(buffer.Length, 16 * 1024)];
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(scratch), ct);
            }
            catch (WebSocketException)
            {
                return 0; // connection dropped → EOF
            }
            catch (OperationCanceledException) { throw; }

            if (result.MessageType == WebSocketMessageType.Close)
                return 0;
            if (result.Count == 0)
                continue; // empty frame; keep waiting

            _pending = new ArraySegment<byte>(scratch, 0, result.Count);
            return DrainPending(buffer.Span);
        }
    }

    private int DrainPending(Span<byte> dest)
    {
        int n = Math.Min(dest.Length, _pending.Count);
        _pending.AsSpan(0, n).CopyTo(dest);
        _pending = n == _pending.Count
            ? default
            : new ArraySegment<byte>(_pending.Array!, _pending.Offset + n, _pending.Count - n);
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return;
        // WebSocket forbids concurrent sends; serialize.
        await _writeLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
        finally { _writeLock.Release(); }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .GetAwaiter().GetResult();
            }
            catch { }
            _ws.Dispose();
            _writeLock.Dispose();
            _completion.TrySetResult();
        }
        base.Dispose(disposing);
    }
}
