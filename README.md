# SQL Server Simulator

A minimal TDS-protocol server (.NET 10) that looks like a real SQL Server to clients such as `Microsoft.Data.SqlClient`. It does not execute SQL; it maps incoming statements (exact match, after normalization) to static result sets loaded from CSV files. Before comparison, both the configured and the incoming statement are normalized: every run of whitespace (spaces, tabs, newlines) is collapsed to a single space, leading/trailing whitespace is trimmed, and matching is case-insensitive. Note that whitespace inside string literals is normalized too.

- Any login succeeds, regardless of credentials.
- Encryption is not supported: clients must connect with `Encrypt=False`.
- Unmapped statements return a SQL error (number 50000).
- Built-in handling for common client housekeeping (no mapping needed):
  - Power BI / Power Query connection probe (`SELECT @@version _VERSION, ...`) and plain `SELECT @@version` queries.
  - Batches consisting only of `SET`/`USE` statements are acknowledged as no-ops.
  - A leading `USE [db]` in front of a batch is stripped before mapping lookup.
  - `sp_executesql` sent via RPC: the statement text is executed through the normal mapping lookup (parameters are ignored).
  - Power BI / Power Query derived-table wrappers around a mapped statement, e.g. `select * from ( <statement> ) SourceQuery where 1 = 2` (schema probe, returns the columns with zero rows) and `select [cols] from ( <statement> ) as [$Table]` (returns the inner statement's full result set; the outer column projection is ignored).

## Run

```bash
dotnet run --project src/SqlServerSimulator [path/to/mappings.json] [port]
```

Defaults: `mappings/mappings.json` (copied next to the binary) and port `11433`.

Example connection string:

```
Server=127.0.0.1,11433;User Id=anyone;Password=whatever;Encrypt=False
```

## Mapping config

`mappings/mappings.json` maps a statement to a CSV file (path relative to the config file) plus column types:

```json
{
  "mappings": [
    {
      "statement": "SELECT Id, Name, Price FROM Products",
      "csvFile": "products.csv",
      "columns": [
        { "name": "Id", "type": "int" },
        { "name": "Name", "type": "nvarchar(50)" },
        { "name": "Price", "type": "decimal(10,2)" }
      ]
    }
  ]
}
```

The CSV's first line is a header and is skipped. Empty fields or `NULL` are returned as SQL NULL.

Supported column types: `int`, `bigint`, `bit`, `nvarchar(n)`, `decimal(p,s)`, `datetime2`.

Instead of writing the statement inline, a mapping can use `sqlFile`: a path (relative to the config file) to a `.sql` file whose contents are the statement to match (exact match after the same normalization). Column types are still defined in the mapping:

```json
{
  "sqlFile": "products.sql",
  "csvFile": "products.csv",
  "columns": [
    { "name": "Id", "type": "int" },
    { "name": "Name", "type": "nvarchar(50)" },
    { "name": "Price", "type": "decimal(10,2)" }
  ]
}
```

Instead of `statement` (exact match), a mapping can use `statementPattern`: a .NET regex (case-insensitive, `.` matches newlines) tried when no exact match is found. Patterns are tried in config order. `csvFile` may be omitted to return an empty result set.

```json
{
  "statementPattern": "from\\s+\\[?INFORMATION_SCHEMA\\]?\\.\\[?TABLES\\]?",
  "csvFile": "tables.csv",
  "columns": [ { "name": "TABLE_NAME", "type": "nvarchar(128)" } ]
}
```

## Power BI Desktop

Connect with the built-in SQL Server connector: server `127.0.0.1,11433`, database anything, and under connection settings disable encryption (or ignore the encryption warning). The connection probe is answered by the built-in handler; the navigator's table-listing queries can be served with `statementPattern` mappings (a sample matching `INFORMATION_SCHEMA.TABLES` is included). The simulator logs every incoming statement to the console (`[batch]`/`[rpc]` lines), so if Power BI reports an unmapped statement, copy it from the console into a new mapping.

## Test

```bash
dotnet test
```

The tests start the simulator in-process and verify with `Microsoft.Data.SqlClient` that connecting, authentication with arbitrary credentials, reading the mapped result set (values and column types), and error handling for unmapped statements all behave correctly.
