using Microsoft.Data.SqlClient;
using SqlServerSimulator;
using SqlServerSimulator.Mapping;

namespace SqlServerSimulator.Tests;

public sealed class SimulatorTests : IAsyncLifetime
{
    private SimulatorServer _server = null!;

    public Task InitializeAsync()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "mappings", "mappings.json");
        _server = new SimulatorServer(MappingConfig.Load(configPath), port: 0);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private string ConnectionString =>
        $"Server=127.0.0.1,{_server.Port};User Id=anyone;Password=whatever;Encrypt=False;Connect Timeout=15";

    [Fact]
    public async Task Connects_And_Returns_Mapped_ResultSet()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT Id, Name, Price FROM Products", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Equal(3, reader.FieldCount);
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.Equal(typeof(decimal), reader.GetFieldType(2));

        var rows = new List<(int Id, string Name, decimal? Price)>();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2)));
        }

        Assert.Equal(4, rows.Count);
        Assert.Equal((1, "Keyboard", 49.99m), rows[0]);
        Assert.Equal((2, "Mouse", 19.50m), rows[1]);
        Assert.Equal((3, "Monitor, 27 inch", 249.00m), rows[2]);
        Assert.Equal(4, rows[3].Id);
        Assert.Null(rows[3].Price);
    }

    [Fact]
    public async Task SqlFile_Mapping_Returns_Mapped_ResultSet()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Statement must match the contents of mappings/products.sql (after trimming).
        await using var command = new SqlCommand("SELECT p.Id, p.Name, p.Price\nFROM Products p\nORDER BY p.Id", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Equal(3, reader.FieldCount);
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.Equal(typeof(decimal), reader.GetFieldType(2));

        var rows = 0;
        while (await reader.ReadAsync()) rows++;
        Assert.Equal(4, rows);
    }

    [Fact]
    public async Task Statement_Matching_Ignores_Whitespace_And_Case()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Mapping is "SELECT Id, Name, Price FROM Products"; send it reformatted.
        await using var command = new SqlCommand("select\t Id,\n   name,  Price\r\n  FROM  products  ", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Equal(3, reader.FieldCount);
        var rows = 0;
        while (await reader.ReadAsync()) rows++;
        Assert.Equal(4, rows);
    }

    [Fact]
    public async Task Auth_Succeeds_With_Any_Credentials()
    {
        var cs = $"Server=127.0.0.1,{_server.Port};User Id=x;Password=y;Encrypt=False;Connect Timeout=15";
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Encrypted_Connection_Returns_Mapped_ResultSet()
    {
        var cs = $"Server=127.0.0.1,{_server.Port};User Id=anyone;Password=whatever;" +
                 "Encrypt=True;TrustServerCertificate=True;Connect Timeout=15";
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT Id, Name, Price FROM Products", connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Equal(3, reader.FieldCount);
        var rows = 0;
        while (await reader.ReadAsync()) rows++;
        Assert.Equal(4, rows);
    }

    [Fact]
    public async Task PowerBi_Version_Probe_Returns_Builtin_Result()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        const string sql = """
            SELECT
            @@version _VERSION,
            CAST(SERVERPROPERTY('EngineEdition') as VARCHAR(4)) _EDITION,
            CASE WHEN EXISTS (SELECT * FROM sys.extended_properties WHERE [name] = N'isSaaSMetadata' AND [value] = '1') THEN 1 ELSE 0 END _IS_SAAS,
            CASE WHEN EXISTS (SELECT * FROM sys.types WHERE name = 'char' AND collation_name LIKE '%UTF8%') THEN 1 ELSE 0 END _UTF8_COLLATION
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.FieldCount);
        Assert.Contains("Microsoft SQL Server", reader.GetString(0));
        Assert.Equal("3", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
    }

    [Fact]
    public async Task Set_Statements_Are_Acknowledged()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SET NOCOUNT ON; SET ANSI_NULLS ON", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Leading_Use_Statement_Is_Ignored_For_Matching()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("USE [master]\nSELECT Id, Name, Price FROM Products", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.FieldCount);
    }

    [Fact]
    public async Task Parameterized_Command_Uses_SpExecuteSql_Rpc()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Adding a parameter makes SqlClient send the command via RPC (sp_executesql).
        await using var command = new SqlCommand("SELECT Id, Name, Price FROM Products", connection);
        command.Parameters.AddWithValue("@unused", 1);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.FieldCount);
    }

    [Fact]
    public async Task Pattern_Mapping_Matches_Information_Schema_Query()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "select TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE from [INFORMATION_SCHEMA].[TABLES] where TABLE_TYPE in ('BASE TABLE', 'VIEW')",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Products", reader.GetString(2));
    }

    [Fact]
    public async Task PowerBi_Schema_Probe_Wrapper_Returns_Columns_With_No_Rows()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "select * from ( SELECT Id, Name, Price FROM Products ) SourceQuery where 1 = 2",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(3, reader.FieldCount);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task PowerBi_Data_Fetch_Wrapper_Returns_Inner_ResultSet()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "select [Id], [Name], [Price] from ( SELECT Id, Name, Price FROM Products ) as [$Table]",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync()) rows++;
        Assert.Equal(4, rows);
    }

    [Fact]
    public async Task Unmapped_Statement_Returns_SqlError()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("SELECT 1", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync());
        Assert.Contains("No mapping configured", ex.Message);
    }
}
