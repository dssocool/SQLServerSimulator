using System.Text;

namespace SqlServerSimulator.Tds;

public sealed record RpcParameter(string Name, bool IsOutput, object? Value);

public sealed record RpcRequest(string ProcName, IReadOnlyList<RpcParameter> Parameters);

/// <summary>Parses an RPC request (TDS packet type 0x03) into a procedure name and typed parameters.</summary>
public static class RpcParser
{
    public static RpcRequest Parse(byte[] payload)
    {
        var pos = 0;
        // Skip ALL_HEADERS (TDS 7.2+): first DWORD is total header length.
        if (payload.Length >= 4)
        {
            var totalHeaderLength = BitConverter.ToUInt32(payload, 0);
            if (totalHeaderLength <= payload.Length) pos = (int)totalHeaderLength;
        }

        var nameLength = BitConverter.ToUInt16(payload, pos);
        pos += 2;
        string procName;
        if (nameLength == 0xFFFF)
        {
            var procId = BitConverter.ToUInt16(payload, pos);
            pos += 2;
            procName = procId switch
            {
                10 => "sp_executesql",
                11 => "sp_prepare",
                12 => "sp_execute",
                13 => "sp_prepexec",
                15 => "sp_unprepare",
                _ => $"#proc{procId}",
            };
        }
        else
        {
            procName = Encoding.Unicode.GetString(payload, pos, nameLength * 2);
            pos += nameLength * 2;
        }
        pos += 2; // option flags

        var parameters = new List<RpcParameter>();
        while (pos < payload.Length)
        {
            // 0x80 (TDS 7.2+) / 0xFF (7.1) marks the start of another RPC in the same batch;
            // a real parameter name is never that long, so treat it as end of this request.
            if (payload[pos] is 0x80 or 0xFF) break;

            var nameLen = payload[pos];
            pos += 1;
            var name = Encoding.Unicode.GetString(payload, pos, nameLen * 2);
            pos += nameLen * 2;
            var status = payload[pos];
            pos += 1;

            object? value;
            try
            {
                value = ReadTypedValue(payload, ref pos);
            }
            catch (NotSupportedException ex)
            {
                // Can't determine the wire length of an unknown type, so stop parsing here
                // and keep whatever parameters were already decoded.
                Console.WriteLine($"[rpc] warning: {ex.Message} Remaining parameters ignored.");
                break;
            }
            parameters.Add(new RpcParameter(name, (status & 0x01) != 0, value));
        }

        return new RpcRequest(procName, parameters);
    }

    private static object? ReadTypedValue(byte[] p, ref int pos)
    {
        var type = p[pos++];
        switch (type)
        {
            case 0xE7: // NVARCHARTYPE
            case 0xEF: // NCHARTYPE
            case 0xA7: // BIGVARCHARTYPE
            case 0xAF: // BIGCHARTYPE
            {
                var maxLen = BitConverter.ToUInt16(p, pos);
                pos += 2 + 5; // max length + collation
                var bytes = ReadVarBytes(p, ref pos, maxLen);
                if (bytes is null) return null;
                return type is 0xE7 or 0xEF ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            }
            case 0xA5: // BIGVARBINARYTYPE
            case 0xAD: // BIGBINARYTYPE
            {
                var maxLen = BitConverter.ToUInt16(p, pos);
                pos += 2;
                return ReadVarBytes(p, ref pos, maxLen);
            }
            case 0x26: // INTN
            {
                pos += 1; // declared size
                var len = p[pos++];
                object? v = len switch
                {
                    0 => null,
                    1 => p[pos],
                    2 => BitConverter.ToInt16(p, pos),
                    4 => BitConverter.ToInt32(p, pos),
                    8 => BitConverter.ToInt64(p, pos),
                    _ => throw new NotSupportedException($"INTN length {len} is not supported."),
                };
                pos += len;
                return v;
            }
            case 0x30: pos += 1; return (byte)p[pos - 1];                                    // INT1
            case 0x34: { var v = BitConverter.ToInt16(p, pos); pos += 2; return v; }         // INT2
            case 0x38: { var v = BitConverter.ToInt32(p, pos); pos += 4; return v; }         // INT4
            case 0x7F: { var v = BitConverter.ToInt64(p, pos); pos += 8; return v; }         // INT8
            case 0x32: pos += 1; return p[pos - 1] != 0;                                     // BIT
            case 0x3B: { var v = (double)BitConverter.ToSingle(p, pos); pos += 4; return v; } // FLT4
            case 0x3E: { var v = BitConverter.ToDouble(p, pos); pos += 8; return v; }        // FLT8
            case 0x68: // BITN
            {
                pos += 1;
                var len = p[pos++];
                if (len == 0) return null;
                var v = p[pos] != 0;
                pos += len;
                return v;
            }
            case 0x6D: // FLTN
            {
                pos += 1;
                var len = p[pos++];
                object? v = len switch
                {
                    0 => null,
                    4 => (double)BitConverter.ToSingle(p, pos),
                    8 => BitConverter.ToDouble(p, pos),
                    _ => throw new NotSupportedException($"FLTN length {len} is not supported."),
                };
                pos += len;
                return v;
            }
            case 0x6A: // DECIMALN
            case 0x6C: // NUMERICN
            {
                pos += 2; // declared size + precision
                var scale = p[pos++];
                var len = p[pos++];
                if (len == 0) return null;
                var sign = p[pos];
                var magnitude = new System.Numerics.BigInteger(
                    p.AsSpan(pos + 1, len - 1), isUnsigned: true, isBigEndian: false);
                pos += len;
                var value = (decimal)magnitude;
                for (var i = 0; i < scale; i++) value /= 10m;
                return sign == 1 ? value : -value;
            }
            case 0x6E: // MONEYN
            {
                pos += 1;
                var len = p[pos++];
                if (len == 0) return null;
                decimal v;
                if (len == 4)
                {
                    v = BitConverter.ToInt32(p, pos) / 10000m;
                }
                else
                {
                    var hi = BitConverter.ToInt32(p, pos);
                    var lo = BitConverter.ToUInt32(p, pos + 4);
                    v = (((long)hi << 32) | lo) / 10000m;
                }
                pos += len;
                return v;
            }
            case 0x6F: // DATETIMN
            {
                pos += 1;
                var len = p[pos++];
                if (len == 0) return null;
                DateTime v;
                if (len == 4) // smalldatetime: days since 1900 + minutes
                {
                    v = new DateTime(1900, 1, 1)
                        .AddDays(BitConverter.ToUInt16(p, pos))
                        .AddMinutes(BitConverter.ToUInt16(p, pos + 2));
                }
                else // datetime: days since 1900 + 1/300s ticks
                {
                    var days = BitConverter.ToInt32(p, pos);
                    var thirds = BitConverter.ToUInt32(p, pos + 4);
                    v = new DateTime(1900, 1, 1).AddDays(days)
                        .AddTicks((long)Math.Round(thirds * (10_000_000.0 / 300.0)));
                }
                pos += len;
                return v;
            }
            case 0x2A: // DATETIME2N
            {
                var scale = p[pos++];
                var len = p[pos++];
                if (len == 0) return null;
                var timeBytes = len - 3;
                var time = ReadUIntLe(p, pos, timeBytes);
                var days = (int)ReadUIntLe(p, pos + timeBytes, 3);
                pos += len;
                return new DateTime(1, 1, 1).AddDays(days).AddTicks((long)time * Pow10(7 - scale));
            }
            case 0x28: // DATEN
            {
                var len = p[pos++];
                if (len == 0) return null;
                var days = (int)ReadUIntLe(p, pos, 3);
                pos += len;
                return new DateTime(1, 1, 1).AddDays(days);
            }
            case 0x29: // TIMEN
            {
                var scale = p[pos++];
                var len = p[pos++];
                if (len == 0) return null;
                var time = ReadUIntLe(p, pos, len);
                pos += len;
                return TimeSpan.FromTicks((long)time * Pow10(7 - scale));
            }
            case 0x24: // GUIDTYPE
            {
                pos += 1;
                var len = p[pos++];
                if (len == 0) return null;
                var v = new Guid(p.AsSpan(pos, 16));
                pos += len;
                return v;
            }
            default:
                throw new NotSupportedException($"RPC parameter type 0x{type:X2} is not supported.");
        }
    }

    /// <summary>Reads a US_VARBYTE value, or a PLP-encoded value when the declared max length is 0xFFFF ("max" types).</summary>
    private static byte[]? ReadVarBytes(byte[] p, ref int pos, ushort maxLen)
    {
        if (maxLen == 0xFFFF)
        {
            var total = BitConverter.ToUInt64(p, pos);
            pos += 8;
            if (total == 0xFFFFFFFFFFFFFFFF) return null; // PLP NULL
            using var ms = new MemoryStream();
            while (true)
            {
                var chunk = BitConverter.ToUInt32(p, pos);
                pos += 4;
                if (chunk == 0) break;
                ms.Write(p, pos, (int)chunk);
                pos += (int)chunk;
            }
            return ms.ToArray();
        }

        var len = BitConverter.ToUInt16(p, pos);
        pos += 2;
        if (len == 0xFFFF) return null;
        var bytes = p.AsSpan(pos, len).ToArray();
        pos += len;
        return bytes;
    }

    private static ulong ReadUIntLe(byte[] p, int pos, int count)
    {
        ulong v = 0;
        for (var i = 0; i < count; i++) v |= (ulong)p[pos + i] << (8 * i);
        return v;
    }

    private static long Pow10(int n)
    {
        long r = 1;
        for (var i = 0; i < n; i++) r *= 10;
        return r;
    }
}
