using System.Buffers.Binary;

namespace SqlServerSimulator.Tds;

public static class TdsPacketType
{
    public const byte SqlBatch = 0x01;
    public const byte Rpc = 0x03;
    public const byte TabularResult = 0x04;
    public const byte Attention = 0x06;
    public const byte TransactionManager = 0x0E;
    public const byte Login7 = 0x10;
    public const byte PreLogin = 0x12;
}

public sealed record TdsMessage(byte Type, byte[] Payload);

/// <summary>Reads and writes TDS packets (8-byte header framing, multi-packet messages).</summary>
public static class TdsPacketIo
{
    private const int HeaderSize = 8;
    public const int PacketSize = 4096;

    /// <summary>Reads a complete TDS message (all packets until EOM). Returns null on clean disconnect.</summary>
    public static async Task<TdsMessage?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        using var payload = new MemoryStream();
        byte messageType = 0;
        var header = new byte[HeaderSize];
        while (true)
        {
            if (!await ReadExactOrEofAsync(stream, header, ct))
                return payload.Length == 0 ? null : throw new EndOfStreamException("Connection closed mid-message.");

            messageType = header[0];
            var status = header[1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            if (length < HeaderSize) throw new InvalidDataException($"Bad TDS packet length {length}.");

            var body = new byte[length - HeaderSize];
            await stream.ReadExactlyAsync(body, ct);
            payload.Write(body);

            if ((status & 0x01) != 0) // EOM
                return new TdsMessage(messageType, payload.ToArray());
        }
    }

    private static async Task<bool> ReadExactOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }

    /// <summary>Writes a message, splitting it into packets as needed.</summary>
    public static async Task WriteMessageAsync(Stream stream, byte type, byte[] payload, CancellationToken ct)
    {
        var maxBody = PacketSize - HeaderSize;
        var offset = 0;
        byte packetId = 1;
        do
        {
            var chunk = Math.Min(maxBody, payload.Length - offset);
            var last = offset + chunk >= payload.Length;
            var packet = new byte[HeaderSize + chunk];
            packet[0] = type;
            packet[1] = (byte)(last ? 0x01 : 0x00);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)(HeaderSize + chunk));
            // spid (4,5) and window (7) stay zero
            packet[6] = packetId++;
            payload.AsSpan(offset, chunk).CopyTo(packet.AsSpan(HeaderSize));
            await stream.WriteAsync(packet, ct);
            offset += chunk;
        } while (offset < payload.Length);
        await stream.FlushAsync(ct);
    }
}
