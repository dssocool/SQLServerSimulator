using System.Buffers.Binary;

namespace SqlServerSimulator.Tds;

/// <summary>
/// Stream wrapper for the TLS handshake phase: per the TDS spec, the handshake records
/// are exchanged wrapped inside TDS PRELOGIN (0x12) packets. Writes are framed as
/// PRELOGIN packets; reads strip the packet headers. Once the handshake completes, set
/// <see cref="HandshakeComplete"/> so subsequent TLS traffic passes through unframed.
/// </summary>
public sealed class PreLoginFramedStream : Stream
{
    private const int HeaderSize = 8;

    private readonly Stream _inner;
    private byte[] _readBuffer = Array.Empty<byte>();
    private int _readPos;

    public PreLoginFramedStream(Stream inner) => _inner = inner;

    public bool HandshakeComplete { get; set; }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (HandshakeComplete && _readPos >= _readBuffer.Length)
            return await _inner.ReadAsync(buffer, ct);

        if (_readPos >= _readBuffer.Length)
        {
            var header = new byte[HeaderSize];
            await _inner.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            if (header[0] != TdsPacketType.PreLogin || length < HeaderSize)
                throw new InvalidDataException("Expected a PRELOGIN-framed TLS handshake packet.");
            _readBuffer = new byte[length - HeaderSize];
            await _inner.ReadExactlyAsync(_readBuffer, ct);
            _readPos = 0;
        }

        var n = Math.Min(buffer.Length, _readBuffer.Length - _readPos);
        _readBuffer.AsSpan(_readPos, n).CopyTo(buffer.Span);
        _readPos += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (HandshakeComplete)
        {
            await _inner.WriteAsync(buffer, ct);
            await _inner.FlushAsync(ct);
            return;
        }

        var maxBody = TdsPacketIo.PacketSize - HeaderSize;
        var offset = 0;
        do
        {
            var chunk = Math.Min(maxBody, buffer.Length - offset);
            var packet = new byte[HeaderSize + chunk];
            packet[0] = TdsPacketType.PreLogin;
            packet[1] = 0x01; // EOM
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)(HeaderSize + chunk));
            buffer.Span.Slice(offset, chunk).CopyTo(packet.AsSpan(HeaderSize));
            await _inner.WriteAsync(packet, ct);
            offset += chunk;
        } while (offset < buffer.Length);
        await _inner.FlushAsync(ct);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // Do not dispose the inner stream: the session keeps using it after the handshake.
}
