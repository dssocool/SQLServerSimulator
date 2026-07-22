using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SqlServerSimulator.Mapping;

public sealed class MappingConfig
{
    [JsonPropertyName("mappings")]
    public List<StatementMapping> Mappings { get; set; } = new();

    public static MappingStore Load(string configPath)
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<MappingConfig>(json)
                     ?? throw new InvalidOperationException($"Invalid mapping config: {configPath}");
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

        var exact = new Dictionary<string, ResultSet>(StringComparer.OrdinalIgnoreCase);
        var patterns = new List<(Regex Pattern, ResultSet ResultSet)>();
        foreach (var m in config.Mappings)
        {
            var columns = m.Columns.Select(c => ColumnDefinition.Parse(c.Name, c.Type)).ToList();
            IReadOnlyList<object?[]> rows = string.IsNullOrEmpty(m.CsvFile)
                ? Array.Empty<object?[]>()
                : CsvReader.ReadRows(Path.Combine(baseDir, m.CsvFile), columns);
            var resultSet = new ResultSet(columns, rows);

            if (!string.IsNullOrEmpty(m.Statement))
                exact[MappingStore.Normalize(m.Statement)] = resultSet;
            if (!string.IsNullOrEmpty(m.SqlFile))
            {
                var sqlPath = Path.Combine(baseDir, m.SqlFile);
                if (!File.Exists(sqlPath))
                    throw new FileNotFoundException($"SQL file not found for mapping: {sqlPath}");
                exact[MappingStore.Normalize(File.ReadAllText(sqlPath))] = resultSet;
            }
            if (!string.IsNullOrEmpty(m.StatementPattern))
                patterns.Add((CompileFullStringPattern(m.StatementPattern), resultSet));
        }
        return new MappingStore(exact, patterns);
    }

    // Patterns must describe the whole normalized statement, not just a substring.
    private static Regex CompileFullStringPattern(string pattern)
    {
        var p = pattern.Trim();
        if (!p.StartsWith('^'))
            p = "^" + p;
        if (!p.EndsWith('$'))
            p += "$";
        return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    }
}

public sealed class StatementMapping
{
    [JsonPropertyName("statement")]
    public string Statement { get; set; } = "";

    /// <summary>Optional path (relative to the config file) to a .sql file whose contents are the statement to match exactly (after whitespace normalization).</summary>
    [JsonPropertyName("sqlFile")]
    public string SqlFile { get; set; } = "";

    /// <summary>Optional regex (case-insensitive, dot matches newline) tried when no exact match is found.</summary>
    [JsonPropertyName("statementPattern")]
    public string StatementPattern { get; set; } = "";

    [JsonPropertyName("csvFile")]
    public string CsvFile { get; set; } = "";

    [JsonPropertyName("columns")]
    public List<ColumnSpec> Columns { get; set; } = new();
}

public sealed class ColumnSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed class MappingStore
{
    private readonly Dictionary<string, ResultSet> _exact;
    private readonly List<(Regex Pattern, ResultSet ResultSet)> _patterns;

    public MappingStore(Dictionary<string, ResultSet> exact, List<(Regex, ResultSet)> patterns)
    {
        _exact = exact;
        _patterns = patterns;
    }

    // Collapse every run of whitespace (spaces, tabs, newlines) to a single space and trim,
    // so client statements match configured ones regardless of formatting. Comparison is
    // case-insensitive (dictionary uses OrdinalIgnoreCase). Note: whitespace inside string
    // literals is also collapsed, which is acceptable for exact-match simulation purposes.
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string sql) => WhitespaceRun.Replace(sql, " ").Trim();

    /// <summary>
    /// Parameter-aware lookup: tries the raw statement text first (so mappings can be keyed on
    /// the parameterized form, e.g. "... where x = @y"), then retries with each @parameter
    /// replaced by its SQL literal value.
    /// </summary>
    public ResultSet? Lookup(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        var rs = Lookup(statement);
        if (rs is not null || parameters is null || parameters.Count == 0) return rs;
        var substituted = SubstituteParameters(statement, parameters);
        return substituted == statement ? null : Lookup(substituted);
    }

    /// <summary>Replaces @name references with SQL literal values (longest names first, so @p10 wins over @p1).</summary>
    public static string SubstituteParameters(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters.OrderByDescending(p => p.Key.Length))
        {
            var paramName = name.StartsWith('@') ? name : "@" + name;
            sql = Regex.Replace(
                sql,
                Regex.Escape(paramName) + @"(?![\w@$#])",
                ToSqlLiteral(value).Replace("$", "$$"),
                RegexOptions.IgnoreCase);
        }
        return sql;
    }

    public static string ToSqlLiteral(object? value) => value switch
    {
        null => "NULL",
        string s => "N'" + s.Replace("'", "''") + "'",
        bool b => b ? "1" : "0",
        DateTime d => "'" + d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture) + "'",
        TimeSpan t => "'" + t.ToString(null, System.Globalization.CultureInfo.InvariantCulture) + "'",
        Guid g => "'" + g + "'",
        byte[] b => "0x" + Convert.ToHexString(b),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "NULL",
    };

    public ResultSet? Lookup(string statement)
    {
        var sql = Normalize(statement);
        if (_exact.TryGetValue(sql, out var rs)) return rs;
        foreach (var (pattern, resultSet) in _patterns)
            if (pattern.IsMatch(sql)) return resultSet;
        return TryUnwrapSubquery(sql);
    }

    // Power BI / Power Query never sends the configured statement verbatim; it wraps it in a
    // derived table, e.g.:
    //   select * from ( <statement> ) SourceQuery where 1 = 2          (schema probe)
    //   select [Id], [Name] from ( <statement> ) as [$Table]           (data fetch)
    // Unwrap such wrappers and look up the inner statement. Column projections in the outer
    // select are ignored: the inner statement's full result set is returned. A "where 1 = 2"
    // wrapper returns the columns with zero rows.
    private static readonly Regex SubqueryWrapper = new(
        @"^select\s+(?<cols>.+?)\s+from\s*\(\s*(?<inner>.+)\s*\)\s+(as\s+)?(\[[^\]]+\]|\w+)\s*(?<where>where\s+1\s*=\s*2)?\s*;?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private ResultSet? TryUnwrapSubquery(string sql)
    {
        var m = SubqueryWrapper.Match(sql);
        if (!m.Success) return null;

        var inner = m.Groups["inner"].Value.Trim();
        var resultSet = Lookup(inner);
        if (resultSet is null) return null;

        return m.Groups["where"].Success
            ? resultSet with { Rows = Array.Empty<object?[]>() }
            : resultSet;
    }
}

public sealed record ResultSet(IReadOnlyList<ColumnDefinition> Columns, IReadOnlyList<object?[]> Rows)
{
    /// <summary>A result with no columns: the response contains only a DONE token.</summary>
    public static readonly ResultSet Empty = new(Array.Empty<ColumnDefinition>(), Array.Empty<object?[]>());
}

public enum SqlTypeKind
{
    Int,
    BigInt,
    Bit,
    NVarChar,
    Decimal,
    DateTime2,
}

public sealed record ColumnDefinition(string Name, SqlTypeKind Kind, int Length, byte Precision, byte Scale)
{
    public static ColumnDefinition Parse(string name, string type)
    {
        var t = type.Trim().ToLowerInvariant();
        if (t == "int") return new(name, SqlTypeKind.Int, 4, 0, 0);
        if (t == "bigint") return new(name, SqlTypeKind.BigInt, 8, 0, 0);
        if (t == "bit") return new(name, SqlTypeKind.Bit, 1, 0, 0);
        if (t == "datetime2") return new(name, SqlTypeKind.DateTime2, 8, 0, 7);

        if (t.StartsWith("nvarchar"))
        {
            var len = 50;
            var args = ExtractArgs(t);
            if (args.Length == 1) len = args[0] == "max" ? 4000 : int.Parse(args[0]);
            return new(name, SqlTypeKind.NVarChar, len, 0, 0);
        }
        if (t.StartsWith("decimal") || t.StartsWith("numeric"))
        {
            byte precision = 18, scale = 0;
            var args = ExtractArgs(t);
            if (args.Length >= 1) precision = byte.Parse(args[0]);
            if (args.Length >= 2) scale = byte.Parse(args[1]);
            return new(name, SqlTypeKind.Decimal, 17, precision, scale);
        }
        throw new NotSupportedException($"Unsupported column type: {type}");
    }

    private static string[] ExtractArgs(string type)
    {
        var open = type.IndexOf('(');
        if (open < 0) return Array.Empty<string>();
        var close = type.IndexOf(')', open);
        return type[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
    }

    public object? ParseValue(string raw)
    {
        if (raw.Length == 0 || raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;
        return Kind switch
        {
            SqlTypeKind.Int => int.Parse(raw),
            SqlTypeKind.BigInt => long.Parse(raw),
            SqlTypeKind.Bit => raw is "1" or "true" or "True" ? true : false,
            SqlTypeKind.NVarChar => raw,
            SqlTypeKind.Decimal => decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
            SqlTypeKind.DateTime2 => DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(),
        };
    }
}
