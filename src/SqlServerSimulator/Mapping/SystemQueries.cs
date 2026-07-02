using System.Text.RegularExpressions;

namespace SqlServerSimulator.Mapping;

/// <summary>
/// Built-in answers for the housekeeping queries that real clients (Power BI / Power Query,
/// SSMS, ODBC drivers) send on connect, so they don't need to be listed in mappings.json.
/// Consulted only when no user mapping matches.
/// </summary>
public static class SystemQueries
{
    private const string VersionString =
        "Microsoft SQL Server 2014 (RTM) - 12.0.2000.8 (X64) \n\tSQL Server Simulator";

    // Power BI connection check:
    //   SELECT @@version _VERSION, CAST(SERVERPROPERTY('EngineEdition') as VARCHAR(4)) _EDITION,
    //   CASE WHEN EXISTS (... isSaaSMetadata ...) THEN 1 ELSE 0 END _IS_SAAS,
    //   CASE WHEN EXISTS (... UTF8 collation ...) THEN 1 ELSE 0 END _UTF8_COLLATION
    private static readonly Regex PowerBiVersionCheck = new(
        @"@@version\s+(as\s+)?_VERSION\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Any other probe that just selects @@version (optionally concatenated with SERVERPROPERTY).
    private static readonly Regex GenericVersionQuery = new(
        @"^\s*select\s+@@version\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Batches consisting solely of session-option statements are acknowledged with an empty DONE.
    private static readonly Regex SessionOptionsOnly = new(
        @"^\s*((SET|USE)\s+[^;]*;?\s*)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static ColumnDefinition NVarChar(string name, int len = 512) =>
        new(name, SqlTypeKind.NVarChar, len, 0, 0);

    private static ColumnDefinition Int(string name) => new(name, SqlTypeKind.Int, 4, 0, 0);

    /// <summary>Removes a leading "USE [database]" (with optional semicolon) that Power Query prepends to batches.</summary>
    public static string StripLeadingUse(string sql)
    {
        var m = Regex.Match(sql, @"^\s*USE\s+(\[[^\]]+\]|\w+)\s*;?\s*", RegexOptions.IgnoreCase);
        return m.Success && m.Length < sql.Length ? sql[m.Length..].Trim() : sql;
    }

    public static ResultSet? TryHandle(string sql)
    {
        if (PowerBiVersionCheck.IsMatch(sql))
        {
            return new ResultSet(
                new[] { NVarChar("_VERSION"), NVarChar("_EDITION", 4), Int("_IS_SAAS"), Int("_UTF8_COLLATION") },
                new[] { new object?[] { VersionString, "3", 0, 0 } });
        }

        if (GenericVersionQuery.IsMatch(sql))
        {
            return new ResultSet(
                new[] { NVarChar("version") },
                new[] { new object?[] { VersionString } });
        }

        if (SessionOptionsOnly.IsMatch(sql))
            return ResultSet.Empty;

        return null;
    }
}
