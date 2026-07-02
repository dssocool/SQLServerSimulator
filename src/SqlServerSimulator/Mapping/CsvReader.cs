namespace SqlServerSimulator.Mapping;

/// <summary>Minimal CSV parser supporting quoted fields. First line is a header and is skipped.</summary>
public static class CsvReader
{
    public static IReadOnlyList<object?[]> ReadRows(string path, IReadOnlyList<ColumnDefinition> columns)
    {
        var rows = new List<object?[]>();
        var lines = File.ReadAllLines(path);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseLine(line);
            if (fields.Count != columns.Count)
                throw new InvalidOperationException(
                    $"CSV row has {fields.Count} fields but {columns.Count} columns are defined ({path}): {line}");
            var row = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
                row[i] = columns[i].ParseValue(fields[i]);
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
