# SQL Server Simulator

A minimal TDS-protocol server (.NET 10) that looks like a real SQL Server to clients such as `Microsoft.Data.SqlClient`. It does not execute SQL; it maps incoming statements (exact match, after normalization) to static result sets loaded from CSV files. Before comparison, both the configured and the incoming statement are normalized: every run of whitespace (spaces, tabs, newlines) is collapsed to a single space, leading/trailing whitespace is trimmed, and matching is case-insensitive. Note that whitespace inside string literals is normalized too.

- Any login succeeds, regardless of credentials.
- Encrypted connections are supported via a self-signed certificate that is generated on first start and persisted next to the binary (`simulator-tls.pfx` for the server, `simulator-tls.cer` public part for clients): connect with `Encrypt=True;TrustServerCertificate=True` (or `Encrypt=False` for plaintext). Clients that fully validate the certificate must have `simulator-tls.cer` installed as a trusted root (see the Power BI section).
- Unmapped statements return a SQL error (number 50000).
- Built-in handling for common client housekeeping (no mapping needed):
  - Power BI / Power Query connection probe (`SELECT @@version _VERSION, ...`) and plain `SELECT @@version` queries.
  - Batches consisting only of `SET`/`USE` statements are acknowledged as no-ops.
  - A leading `USE [db]` in front of a batch is stripped before mapping lookup.
  - Parameterized queries sent via RPC (`sp_executesql`, and the prepared-statement procedures `sp_prepare`/`sp_execute`/`sp_prepexec`/`sp_unprepare`): the statement text goes through the normal mapping lookup, and parameter values are decoded (see below).
  - Schema/field discovery: `SET FMTONLY ON` batches (used by MS Report Builder and `CommandBehavior.SchemaOnly`) return the mapped columns with zero rows, and `sp_describe_first_result_set` returns column metadata for the mapped statement.
  - Power BI / Power Query derived-table wrappers around a mapped statement, e.g. `select * from ( <statement> ) SourceQuery where 1 = 2` (schema probe, returns the columns with zero rows) and `select [cols] from ( <statement> ) as [$Table]` (returns the inner statement's full result set; the outer column projection is ignored).

## Run

```bash
dotnet run --project src/SqlServerSimulator [path/to/mappings.json] [port] [bindAddress]
```

Defaults: `mappings/mappings.json` (copied next to the binary), port `11433`, and bind address `0.0.0.0` (all network interfaces). Use `127.0.0.1` as the bind address to accept only local connections. Copy `mappings/mappings.example.json` to `mappings/mappings.json` before the first run (the local file is gitignored).

Example connection string:

```
Server=127.0.0.1,11433;User Id=anyone;Password=whatever;Encrypt=True;TrustServerCertificate=True
```

`Encrypt=False` also works (the session then runs in plaintext). `TrustServerCertificate=True` is required with encryption because the server certificate is self-signed.

## Mapping config

Copy `mappings/mappings.example.json` to `mappings/mappings.json` and edit the local file. It maps a statement to a CSV file (path relative to the config file) plus column types:

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

## Parameterized queries (MS Report Builder, SqlClient parameters)

Clients like MS Report Builder send parameterized SQL (`... WHERE x = @y`) as an RPC call to `sp_executesql` (or via `sp_prepexec`/`sp_execute` for prepared commands). The simulator decodes the statement text and the parameter values, then looks up a mapping in two steps:

1. **Raw text**: the parameterized statement itself, so a mapping keyed on the text with the `@parameter` names matches regardless of the values sent:

```json
{
  "statement": "SELECT Id, Name, Price FROM Products WHERE Id = @ProductId",
  "csvFile": "products.csv",
  "columns": [
    { "name": "Id", "type": "int" },
    { "name": "Name", "type": "nvarchar(50)" },
    { "name": "Price", "type": "decimal(10,2)" }
  ]
}
```

2. **Value substitution**: if no mapping matches the raw text, each `@parameter` is replaced by its SQL literal value (strings as `N'...'`, numbers plain, dates as `'yyyy-MM-dd...'`, `NULL`) and the lookup is retried. This allows value-dependent results via `statement` or `statementPattern` mappings, e.g. a mapping for `SELECT ... WHERE Name = N'Keyboard'` is hit when the client sends `WHERE Name = @Name` with `@Name = 'Keyboard'`.

Report Builder's field discovery (query designer / dataset refresh) uses `SET FMTONLY ON` or `sp_describe_first_result_set`; both are answered from the mapped columns automatically. Every RPC call is logged to the console as `[rpc]` lines including the decoded parameter values, so unmapped statements can be copied from there into a new mapping.

## Power BI Desktop

Connect with the built-in SQL Server connector: server `localhost,11433`, database anything.

With **Use encrypted connection** disabled, no setup is needed. With it enabled, Power BI validates the server certificate against the Windows trusted root store, so the simulator's self-signed certificate must be installed first:

1. Start the simulator once; it prints the path of `simulator-tls.cer` (next to the binary).
2. Double-click the `.cer` file → Install Certificate → Current User → "Place all certificates in the following store" → **Trusted Root Certification Authorities**.
3. In Power BI, connect using `localhost,11433` (the certificate is issued to `localhost` and the machine's IP addresses; a plain hostname that isn't in the certificate will fail name validation).

The certificate is reused across restarts, so this is a one-time step. If you delete `simulator-tls.pfx`, a new certificate is generated and must be re-installed. The connection probe is answered by the built-in handler; the navigator's table-listing queries can be served with `statementPattern` mappings (a sample matching `INFORMATION_SCHEMA.TABLES` is included). The simulator logs every incoming statement to the console (`[batch]`/`[rpc]` lines), so if Power BI reports an unmapped statement, copy it from the console into a new mapping.

## Test

```bash
dotnet test
```

The tests start the simulator in-process and verify with `Microsoft.Data.SqlClient` that connecting, authentication with arbitrary credentials, reading the mapped result set (values and column types), and error handling for unmapped statements all behave correctly.
