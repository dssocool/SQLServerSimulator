using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SqlServerSimulator.Mapping;

namespace SqlServerSimulator.Tds;

/// <summary>Handles one client connection: PRELOGIN, LOGIN7 (always succeeds), then SQL batches.</summary>
public sealed class TdsSession
{
    private Stream _stream;
    private readonly MappingStore _mappings;
    private readonly X509Certificate2? _certificate;
    private readonly Dictionary<int, string> _preparedStatements = new();
    private int _nextPreparedHandle = 1;

    public TdsSession(Stream stream, MappingStore mappings, X509Certificate2? certificate = null)
    {
        _stream = stream;
        _mappings = mappings;
        _certificate = certificate;
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
                    await HandlePreLoginAsync(message.Payload, ct);
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

    private const byte EncryptOff = 0x00;
    private const byte EncryptOn = 0x01;
    private const byte EncryptNotSup = 0x02;
    private const byte EncryptReq = 0x03;

    private async Task HandlePreLoginAsync(byte[] payload, CancellationToken ct)
    {
        var clientEncryption = ParseEncryptionOption(payload);

        // Clients that don't want encryption (Encrypt=False) get ENCRYPT_NOT_SUP and a
        // fully plaintext session; clients asking for encryption get ENCRYPT_ON and a
        // TLS handshake (if we have a certificate).
        var wantsTls = _certificate is not null && clientEncryption is EncryptOn or EncryptReq;
        await SendPreLoginResponseAsync(wantsTls ? EncryptOn : EncryptNotSup, ct);

        if (wantsTls)
        {
            // The TLS handshake records are wrapped in PRELOGIN packets (TDS spec);
            // after the handshake, TDS flows directly over the TLS stream.
            var framed = new PreLoginFramedStream(_stream);
            var ssl = new SslStream(framed, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                // TDS 7.x PRELOGIN-negotiated encryption only supports up to TLS 1.2
                // (TLS 1.3 is reserved for TDS 8.0 strict mode).
                EnabledSslProtocols = SslProtocols.Tls12,
                ClientCertificateRequired = false,
            }, ct);
            framed.HandshakeComplete = true;
            _stream = ssl;
        }
    }

    /// <summary>Reads the ENCRYPTION option (token 0x01) from the client's PRELOGIN payload.</summary>
    private static byte ParseEncryptionOption(byte[] payload)
    {
        var pos = 0;
        while (pos + 5 <= payload.Length && payload[pos] != 0xFF)
        {
            var token = payload[pos];
            var offset = (payload[pos + 1] << 8) | payload[pos + 2];
            var length = (payload[pos + 3] << 8) | payload[pos + 4];
            if (token == 0x01 && length >= 1 && offset < payload.Length)
                return payload[offset];
            pos += 5;
        }
        return EncryptNotSup;
    }

    private async Task SendPreLoginResponseAsync(byte encryption, CancellationToken ct)
    {
        // Options: VERSION, ENCRYPTION, INSTANCE, THREADID, MARS, terminator.
        var options = new (byte Token, byte[] Data)[]
        {
            (0x00, new byte[] { 12, 0, 0, 0, 0, 0 }),  // version 12.0.0.0
            (0x01, new byte[] { encryption }),
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

    private async Task ExecuteSqlAsync(
        string sql,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? parameters = null,
        Action<TdsWriter>? writeExtraTokens = null)
    {
        // Report Builder wraps its field-discovery query in SET FMTONLY ON/OFF: return the
        // columns of the inner statement with zero rows.
        sql = SystemQueries.StripFmtOnly(sql, out var schemaOnly);
        if (sql.Length == 0)
        {
            var done = new TdsWriter();
            writeExtraTokens?.Invoke(done);
            WriteDone(done, status: 0x00, rowCount: 0);
            await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, done.ToArray(), ct);
            return;
        }

        // Power Query prepends "USE [db]" to its batches; ignore it for matching.
        var effectiveSql = SystemQueries.StripLeadingUse(sql);

        var resultSet = _mappings.Lookup(effectiveSql, parameters)
                        ?? SystemQueries.TryHandle(effectiveSql)
                        ?? (effectiveSql != sql ? SystemQueries.TryHandle(sql) : null);
        if (resultSet is null)
        {
            await SendErrorResponseAsync($"No mapping configured for statement: {sql}", ct);
            return;
        }

        if (schemaOnly)
            resultSet = resultSet with { Rows = Array.Empty<object?[]>() };

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

        writeExtraTokens?.Invoke(w);
        WriteDone(w, status: 0x10 /* DONE_COUNT */, rowCount: (ulong)resultSet.Rows.Count);
        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    /// <summary>
    /// RPC support (TDS packet type 0x03): sp_executesql with typed parameter values, the
    /// prepared-statement procedures (sp_prepare / sp_execute / sp_prepexec / sp_unprepare),
    /// and sp_describe_first_result_set for schema discovery.
    /// </summary>
    private async Task HandleRpcAsync(byte[] payload, CancellationToken ct)
    {
        RpcRequest request;
        try
        {
            request = RpcParser.Parse(payload);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            await SendErrorResponseAsync("Malformed RPC request.", ct);
            return;
        }

        var ps = request.Parameters;
        switch (request.ProcName.ToLowerInvariant())
        {
            case "sp_executesql":
            {
                // ps[0] = statement, ps[1] = parameter declaration string, ps[2..] = values.
                if (ps.Count == 0 || ps[0].Value is not string sql)
                {
                    await SendErrorResponseAsync("sp_executesql statement was missing or NULL.", ct);
                    return;
                }
                var values = NamedValues(ps.Skip(2));
                Console.WriteLine($"[rpc] sp_executesql: {sql.Trim()}{FormatParams(values)}");
                await ExecuteSqlAsync(sql.Trim(), ct, values);
                return;
            }
            case "sp_prepare":
            {
                // ps[0] = @handle OUT, ps[1] = parameter declaration, ps[2] = statement.
                if (ps.Count < 3 || ps[2].Value is not string stmt)
                {
                    await SendErrorResponseAsync("sp_prepare statement was missing or NULL.", ct);
                    return;
                }
                var handle = _nextPreparedHandle++;
                _preparedStatements[handle] = stmt.Trim();
                Console.WriteLine($"[rpc] sp_prepare handle={handle}: {stmt.Trim()}");

                var w = new TdsWriter();
                WriteReturnValueInt(w, ps[0].Name, handle);
                WriteDone(w, status: 0x00, rowCount: 0);
                await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
                return;
            }
            case "sp_prepexec":
            {
                // ps[0] = @handle OUT, ps[1] = parameter declaration, ps[2] = statement, ps[3..] = values.
                if (ps.Count < 3 || ps[2].Value is not string stmt)
                {
                    await SendErrorResponseAsync("sp_prepexec statement was missing or NULL.", ct);
                    return;
                }
                var handle = _nextPreparedHandle++;
                _preparedStatements[handle] = stmt.Trim();
                var values = NamedValues(ps.Skip(3));
                Console.WriteLine($"[rpc] sp_prepexec handle={handle}: {stmt.Trim()}{FormatParams(values)}");
                await ExecuteSqlAsync(stmt.Trim(), ct, values, w => WriteReturnValueInt(w, ps[0].Name, handle));
                return;
            }
            case "sp_execute":
            {
                // ps[0] = @handle, ps[1..] = values.
                if (ps.Count == 0 || ToInt(ps[0].Value) is not int handle ||
                    !_preparedStatements.TryGetValue(handle, out var stmt))
                {
                    await SendErrorResponseAsync("sp_execute: unknown prepared statement handle.", ct);
                    return;
                }
                var values = NamedValues(ps.Skip(1));
                Console.WriteLine($"[rpc] sp_execute handle={handle}: {stmt}{FormatParams(values)}");
                await ExecuteSqlAsync(stmt, ct, values);
                return;
            }
            case "sp_unprepare":
            {
                if (ps.Count > 0 && ToInt(ps[0].Value) is int handle)
                    _preparedStatements.Remove(handle);
                var w = new TdsWriter();
                WriteDone(w, status: 0x00, rowCount: 0);
                await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
                return;
            }
            case "sp_describe_first_result_set":
            {
                if (ps.Count == 0 || ps[0].Value is not string tsql)
                {
                    await SendErrorResponseAsync("sp_describe_first_result_set: @tsql was missing or NULL.", ct);
                    return;
                }
                Console.WriteLine($"[rpc] sp_describe_first_result_set: {tsql.Trim()}");
                await HandleDescribeFirstResultSetAsync(tsql.Trim(), ct);
                return;
            }
            default:
                await SendErrorResponseAsync($"Unsupported RPC procedure: {request.ProcName}", ct);
                return;
        }
    }

    private static Dictionary<string, object?> NamedValues(IEnumerable<RpcParameter> parameters)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters)
            if (p.Name.Length > 0)
                values[p.Name] = p.Value;
        return values;
    }

    private static string FormatParams(Dictionary<string, object?> values) =>
        values.Count == 0
            ? ""
            : " | " + string.Join(", ", values.Select(kv => $"{kv.Key}={MappingStore.ToSqlLiteral(kv.Value)}"));

    private static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        _ => null,
    };

    /// <summary>Answers sp_describe_first_result_set by describing the columns of the mapped result set.</summary>
    private async Task HandleDescribeFirstResultSetAsync(string tsql, CancellationToken ct)
    {
        var resultSet = _mappings.Lookup(SystemQueries.StripLeadingUse(tsql));
        if (resultSet is null)
        {
            await SendErrorResponseAsync($"No mapping configured for statement: {tsql}", ct);
            return;
        }

        ColumnDefinition NVarChar(string name, int len) => new(name, SqlTypeKind.NVarChar, len, 0, 0);
        ColumnDefinition Int(string name) => new(name, SqlTypeKind.Int, 4, 0, 0);
        ColumnDefinition Bit(string name) => new(name, SqlTypeKind.Bit, 1, 0, 0);

        var columns = new[]
        {
            Bit("is_hidden"), Int("column_ordinal"), NVarChar("name", 128), Bit("is_nullable"),
            Int("system_type_id"), NVarChar("system_type_name", 256),
            Int("max_length"), Int("precision"), Int("scale"),
        };

        var rows = new List<object?[]>();
        for (var i = 0; i < resultSet.Columns.Count; i++)
        {
            var col = resultSet.Columns[i];
            var (typeId, typeName, maxLength) = col.Kind switch
            {
                SqlTypeKind.Int => (56, "int", 4),
                SqlTypeKind.BigInt => (127, "bigint", 8),
                SqlTypeKind.Bit => (104, "bit", 1),
                SqlTypeKind.NVarChar => (231, $"nvarchar({col.Length})", col.Length * 2),
                SqlTypeKind.Decimal => (106, $"decimal({col.Precision},{col.Scale})", 17),
                SqlTypeKind.DateTime2 => (42, "datetime2", 8),
                _ => (231, "nvarchar(max)", -1),
            };
            rows.Add(new object?[] { false, i + 1, col.Name, true, typeId, typeName, maxLength, (int)col.Precision, (int)col.Scale });
        }

        var describeResult = new ResultSet(columns, rows);
        var w = new TdsWriter();
        w.WriteByte(0x81);
        w.WriteUInt16((ushort)describeResult.Columns.Count);
        foreach (var col in describeResult.Columns)
        {
            w.WriteUInt32(0);
            w.WriteUInt16(0x0001);
            w.WriteTypeInfo(col);
            w.WriteBVarChar(col.Name);
        }
        foreach (var row in describeResult.Rows)
        {
            w.WriteByte(0xD1);
            for (var i = 0; i < describeResult.Columns.Count; i++)
                w.WriteValue(describeResult.Columns[i], row[i]);
        }
        WriteDone(w, status: 0x10, rowCount: (ulong)describeResult.Rows.Count);
        await TdsPacketIo.WriteMessageAsync(_stream, TdsPacketType.TabularResult, w.ToArray(), ct);
    }

    /// <summary>RETURNVALUE token (0xAC) carrying an INT output parameter (used for prepared statement handles).</summary>
    private static void WriteReturnValueInt(TdsWriter w, string name, int value)
    {
        w.WriteByte(0xAC);
        w.WriteUInt16(0);      // param ordinal
        w.WriteBVarChar(name);
        w.WriteByte(0x01);     // status: output parameter
        w.WriteUInt32(0);      // user type
        w.WriteUInt16(0x0001); // flags: nullable
        w.WriteByte(0x26);     // INTN
        w.WriteByte(4);
        w.WriteByte(4);
        w.WriteInt32(value);
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
