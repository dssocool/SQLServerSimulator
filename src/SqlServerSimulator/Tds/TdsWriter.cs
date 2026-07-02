using System.Text;
using SqlServerSimulator.Mapping;

namespace SqlServerSimulator.Tds;

/// <summary>Little-endian byte buffer with helpers for TDS token stream encoding.</summary>
public sealed class TdsWriter
{
    private readonly MemoryStream _buffer = new();

    public byte[] ToArray() => _buffer.ToArray();

    public void WriteByte(byte b) => _buffer.WriteByte(b);
    public void WriteBytes(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

    public void WriteUInt16(ushort v)
    {
        _buffer.WriteByte((byte)v);
        _buffer.WriteByte((byte)(v >> 8));
    }

    public void WriteInt32(int v) => WriteUInt32((uint)v);

    public void WriteUInt32(uint v)
    {
        WriteUInt16((ushort)v);
        WriteUInt16((ushort)(v >> 16));
    }

    public void WriteUInt64(ulong v)
    {
        WriteUInt32((uint)v);
        WriteUInt32((uint)(v >> 32));
    }

    /// <summary>B_VARCHAR: byte length (in characters) + UTF-16 string.</summary>
    public void WriteBVarChar(string s)
    {
        WriteByte(checked((byte)s.Length));
        WriteBytes(Encoding.Unicode.GetBytes(s));
    }

    /// <summary>US_VARCHAR: ushort length (in characters) + UTF-16 string.</summary>
    public void WriteUsVarChar(string s)
    {
        WriteUInt16(checked((ushort)s.Length));
        WriteBytes(Encoding.Unicode.GetBytes(s));
    }

    // Default collation: Latin1_General, codepage 1252 (LCID 0x0409, no flags).
    private static readonly byte[] DefaultCollation = { 0x09, 0x04, 0x00, 0x00, 0x00 };

    public void WriteTypeInfo(ColumnDefinition col)
    {
        switch (col.Kind)
        {
            case SqlTypeKind.Int:
                WriteByte(0x26); // INTN
                WriteByte(4);
                break;
            case SqlTypeKind.BigInt:
                WriteByte(0x26); // INTN
                WriteByte(8);
                break;
            case SqlTypeKind.Bit:
                WriteByte(0x68); // BITN
                WriteByte(1);
                break;
            case SqlTypeKind.NVarChar:
                WriteByte(0xE7); // NVARCHARTYPE
                WriteUInt16((ushort)(col.Length * 2));
                WriteBytes(DefaultCollation);
                break;
            case SqlTypeKind.Decimal:
                WriteByte(0x6A); // DECIMALNTYPE
                WriteByte(17);
                WriteByte(col.Precision);
                WriteByte(col.Scale);
                break;
            case SqlTypeKind.DateTime2:
                WriteByte(0x2A); // DATETIME2NTYPE
                WriteByte(col.Scale);
                break;
            default:
                throw new NotSupportedException(col.Kind.ToString());
        }
    }

    public void WriteValue(ColumnDefinition col, object? value)
    {
        if (value is null)
        {
            // All supported types use a byte length prefix except NVARCHAR (ushort, 0xFFFF = NULL).
            if (col.Kind == SqlTypeKind.NVarChar) WriteUInt16(0xFFFF);
            else WriteByte(0);
            return;
        }

        switch (col.Kind)
        {
            case SqlTypeKind.Int:
                WriteByte(4);
                WriteInt32((int)value);
                break;
            case SqlTypeKind.BigInt:
                WriteByte(8);
                WriteUInt64((ulong)(long)value);
                break;
            case SqlTypeKind.Bit:
                WriteByte(1);
                WriteByte((bool)value ? (byte)1 : (byte)0);
                break;
            case SqlTypeKind.NVarChar:
            {
                var bytes = Encoding.Unicode.GetBytes((string)value);
                WriteUInt16((ushort)bytes.Length);
                WriteBytes(bytes);
                break;
            }
            case SqlTypeKind.Decimal:
                WriteDecimal((decimal)value, col.Scale);
                break;
            case SqlTypeKind.DateTime2:
                WriteDateTime2((DateTime)value, col.Scale);
                break;
            default:
                throw new NotSupportedException(col.Kind.ToString());
        }
    }

    private void WriteDecimal(decimal value, byte scale)
    {
        var scaled = decimal.Round(value, scale) * Pow10(scale);
        var positive = scaled >= 0;
        var magnitude = (System.Numerics.BigInteger)Math.Abs(scaled);
        var magBytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (magBytes.Length > 16) throw new OverflowException("Decimal magnitude too large.");

        WriteByte(17); // length: sign + 16 magnitude bytes
        WriteByte(positive ? (byte)1 : (byte)0);
        WriteBytes(magBytes);
        for (var i = magBytes.Length; i < 16; i++) WriteByte(0);
    }

    private static decimal Pow10(int n)
    {
        decimal r = 1;
        for (var i = 0; i < n; i++) r *= 10;
        return r;
    }

    private void WriteDateTime2(DateTime value, byte scale)
    {
        // Time: units of 10^-scale seconds since midnight; scale 7 => 100ns ticks.
        var ticksOfDay = value.TimeOfDay.Ticks; // 100ns units
        var divisor = (long)Math.Pow(10, 7 - scale);
        var time = (ulong)(ticksOfDay / divisor);
        var timeBytes = scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

        var days = (int)(value.Date - new DateTime(1, 1, 1)).TotalDays;

        WriteByte((byte)(timeBytes + 3));
        for (var i = 0; i < timeBytes; i++) WriteByte((byte)(time >> (8 * i)));
        WriteByte((byte)days);
        WriteByte((byte)(days >> 8));
        WriteByte((byte)(days >> 16));
    }
}
