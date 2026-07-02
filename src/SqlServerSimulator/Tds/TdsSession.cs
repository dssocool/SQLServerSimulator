using System.Text;
using SqlServerSimulator.Mapping;

namespace SqlServerSimulator.Tds;

/// <summary>Handles one client connection: PRELOGIN, LOGIN7 (always succeeds), then SQL batches.</summary>
public sealed class TdsSession
{
    private readonly Stream _stream;
    private readonly MappingStore _mappings;

    public TdsSession(Stream stream, MappingStore mappings)
    {
        _stream = stream;
        _mappings = mappings;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            var message = await TdsPacketIo.ReadMessageAsync(_stream, ct);
            if (message is null) return;

            switch (message.Type)
            {
                case TdsPacketType.PreLogin:
                    await SendPreLoginResponseAsync(ct);
                    break;
                case TdsPacketType.Login7:
                    await SendLoginSuccessAsync(ct);
                    break;
                case TdsPacketType.SqlBatch:
                    await HandleSqlBatchAsync(message.Payload, ct);
                    break;
                case TdsPacketType.Rpc:
                    await HandleRpcAsync(message.Payload, ct);
                    break;
                case TdsPacketType.Attention:
                    await SendAttentionAckAsync(ct);
                    break;
                default:
                    await SendErrorResponseAsync($"Unsupported TDS message type 0x{message.Type:X2}.", ct);
                    break;
            }
        }
    }

    private async Task SendPreLoginResponseAsync(CancellationToken ct)
    {
        // Options: VERSION, ENCRYPTION, INSTANCE, THREADID, MARS, terminator.
        var options = new (byte Token, byte[] Data)[]
        {
            (0x00, new byte[] { 12, 0, 0, 0, 0, 0 }),  // version 12.0.0.0
            (0x01, new byte[] { 0x02 }),                // ENCRYPT_NOT_SUP
            (0x02, new byte[] { 0x00 }),                // instance ack
            (0x03, new byte[] { 0, 0, 0, 0 }),          // thread id
            (0x04, new byte[] { 0x00 }),                // MARS off
        };

        var headerSize = options.Length * 5 + 1;
        var w = new TdsWriter();
        var offset = headerSize;
        foreach (var (token, data) in options)
        {
            w.WriteByte(token);
            w.WriteByte((byte)(offset >> 8)); // offsets/lengths are big-endian
            w.WriteByte((byte)offset);
            w.WriteByte((byte)(data.Length >> 8));
            w.WriteByte((byte)data.Length);
            offset += data.Length;
        }
        w.WriteByte(0xFF);
        foreach (var (_, data) in options) w.WriteBytes(data);

        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    private async Task SendLoginSuccessAsync(CancellationToken ct)
    {
        var w = new TdsWriter();

        // ENVCHANGE: database change master -> master
        WriteEnvChange(w, 1, "master", "master");
        // ENVCHANGE: packet size
        WriteEnvChange(w, 4, TdsPacketIo.PacketSize.ToString(), TdsPacketIo.PacketSize.ToString());
        // ENVCHANGE: SQL collation (type 7, binary payload). Required by clients
        // (e.g. SqlClient RPC parameter encoding) that use the server default collation.
        w.WriteByte(0xE3);
        w.WriteUInt16(1 + 1 + 5 + 1); // type + new length + collation + old length
        w.WriteByte(7);
        w.WriteByte(5);
        w.WriteBytes(new byte[] { 0x09, 0x04, 0x00, 0x00, 0x00 }); // Latin1_General (LCID 0x0409)
        w.WriteByte(0);

        // LOGINACK
        var progName = "SQL Server Simulator";
        var progBytes = Encoding.Unicode.GetByteCount(progName);
        w.WriteByte(0xAD);
        w.WriteUInt16((ushort)(1 + 4 + 1 + progBytes + 4));
        w.WriteByte(1); // interface: SQL_TSQL
        w.WriteBytes(new byte[] { 0x74, 0x00, 0x00, 0x04 }); // TDS 7.4
        w.WriteBVarChar(progName);
        w.WriteBytes(new byte[] { 12, 0, 0, 0 }); // prog version

        WriteDone(w, status: 0x00, rowCount: 0);
        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    private static void WriteEnvChange(TdsWriter w, byte type, string newValue, string oldValue)
    {
        var inner = new TdsWriter();
        inner.WriteByte(type);
        inner.WriteBVarChar(newValue);
        inner.WriteBVarChar(oldValue);
        var payload = inner.ToArray();
        w.WriteByte(0xE3);
        w.WriteUInt16((ushort)payload.Length);
        w.WriteBytes(payload);
    }

    private async Task HandleSqlBatchAsync(byte[] payload, CancellationToken ct)
    {
        var sqlStart = 0;
        // Skip ALL_HEADERS (TDS 7.2+): first DWORD is total header length.
        if (payload.Length >= 4)
        {
            var totalHeaderLength = BitConverter.ToUInt32(payload, 0);
            if (totalHeaderLength <= payload.Length) sqlStart = (int)totalHeaderLength;
        }

        var sql = Encoding.Unicode.GetString(payload, sqlStart, payload.Length - sqlStart).Trim();
        Console.WriteLine($"[batch] {sql}");
        await ExecuteSqlAsync(sql, ct);
    }

    private async Task ExecuteSqlAsync(string sql, CancellationToken ct)
    {
        // Power Query prepends "USE [db]" to its batches; ignore it for matching.
        var effectiveSql = SystemQueries.StripLeadingUse(sql);

        var resultSet = _mappings.Lookup(effectiveSql)
                        ?? SystemQueries.TryHandle(effectiveSql)
                        ?? (effectiveSql != sql ? SystemQueries.TryHandle(sql) : null);
        if (resultSet is null)
        {
            await SendErrorResponseAsync($"No mapping configured for statement: {sql}", ct);
            return;
        }

        var w = new TdsWriter();

        if (resultSet.Columns.Count > 0)
        {
            // COLMETADATA
            w.WriteByte(0x81);
            w.WriteUInt16((ushort)resultSet.Columns.Count);
            foreach (var col in resultSet.Columns)
            {
                w.WriteUInt32(0);      // UserType
                w.WriteUInt16(0x0001); // flags: nullable
                w.WriteTypeInfo(col);
                w.WriteBVarChar(col.Name);
            }
        }

        // ROW tokens
        foreach (var row in resultSet.Rows)
        {
            w.WriteByte(0xD1);
            for (var i = 0; i < resultSet.Columns.Count; i++)
                w.WriteValue(resultSet.Columns[i], row[i]);
        }

        WriteDone(w, status: 0x10 /* DONE_COUNT */, rowCount: (ulong)resultSet.Rows.Count);
        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    /// <summary>
    /// Minimal RPC support: recognizes sp_executesql (proc id 10) and executes its first
    /// parameter (the statement text) through the normal SQL path. Parameters are ignored.
    /// </summary>
    private async Task HandleRpcAsync(byte[] payload, CancellationToken ct)
    {
        try
        {
            var pos = 0;
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
                procName = procId == 10 ? "sp_executesql" : $"#proc{procId}";
            }
            else
            {
                procName = Encoding.Unicode.GetString(payload, pos, nameLength * 2);
                pos += nameLength * 2;
            }
            pos += 2; // option flags

            if (!procName.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
            {
                await SendErrorResponseAsync($"Unsupported RPC procedure: {procName}", ct);
                return;
            }

            // First parameter: B_VARCHAR name, status byte, TYPE_INFO (NVARCHAR), value.
            var paramNameLen = payload[pos];
            pos += 1 + paramNameLen * 2;
            pos += 1; // status flags
            var typeToken = payload[pos];
            if (typeToken != 0xE7) // NVARCHARTYPE expected for the statement text
            {
                await SendErrorResponseAsync("Unsupported sp_executesql parameter encoding.", ct);
                return;
            }
            pos += 1 + 2 + 5; // type token, max length, collation
            var valueLength = BitConverter.ToUInt16(payload, pos);
            pos += 2;
            if (valueLength == 0xFFFF)
            {
                await SendErrorResponseAsync("sp_executesql statement was NULL.", ct);
                return;
            }

            var sql = Encoding.Unicode.GetString(payload, pos, valueLength).Trim();
            Console.WriteLine($"[rpc] sp_executesql: {sql}");
            await ExecuteSqlAsync(sql, ct);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            await SendErrorResponseAsync("Malformed RPC request.", ct);
        }
    }

    private async Task SendErrorResponseAsync(string message, CancellationToken ct)
    {
        var w = new TdsWriter();
        var inner = new TdsWriter();
        inner.WriteInt32(50000);      // error number
        inner.WriteByte(1);           // state
        inner.WriteByte(16);          // severity
        inner.WriteUsVarChar(message);
        inner.WriteBVarChar("SqlServerSimulator"); // server name
        inner.WriteBVarChar("");      // proc name
        inner.WriteInt32(1);          // line number
        var payload = inner.ToArray();

        w.WriteByte(0xAA); // ERROR token
        w.WriteUInt16((ushort)payload.Length);
        w.WriteBytes(payload);
        WriteDone(w, status: 0x02 /* DONE_ERROR */, rowCount: 0);
        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    private async Task SendAttentionAckAsync(CancellationToken ct)
    {
        var w = new TdsWriter();
        WriteDone(w, status: 0x20 /* DONE_ATTN */, rowCount: 0);
        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    private static void WriteDone(TdsWriter w, ushort status, ulong rowCount)
    {
        w.WriteByte(0xFD);
        w.WriteUInt16(status);
        w.WriteUInt16(0); // current command
        w.WriteUInt64(rowCount);
    }
}
