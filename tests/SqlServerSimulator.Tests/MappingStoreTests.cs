using System.Text.RegularExpressions;
using SqlServerSimulator.Mapping;

namespace SqlServerSimulator.Tests;

public sealed class MappingStoreTests
{
    private static MappingStore StoreWithPatterns(params (string Pattern, string Label)[] patterns)
    {
        var exact = new Dictionary<string, ResultSet>(StringComparer.OrdinalIgnoreCase);
        var regexes = patterns.Select(p =>
        {
            var text = p.Pattern.Trim();
            if (!text.StartsWith('^')) text = "^" + text;
            if (!text.EndsWith('$')) text += "$";
            return (new Regex(text, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled),
                new ResultSet(
                    new[] { ColumnDefinition.Parse("Label", "nvarchar(50)") },
                    new[] { new object?[] { p.Label } }));
        }).ToList();
        return new MappingStore(exact, regexes);
    }

    [Fact]
    public void Exact_Statement_Does_Not_Match_Longer_Prefix_Sharing_Query()
    {
        var empty = new ResultSet(Array.Empty<ColumnDefinition>(), Array.Empty<object?[]>());
        var store = new MappingStore(
            new Dictionary<string, ResultSet>(StringComparer.OrdinalIgnoreCase)
            {
                [MappingStore.Normalize("SELECT Id, Name, Price FROM Products")] = empty,
                [MappingStore.Normalize("SELECT Id, Name, Price FROM Products WHERE Name = N'Keyboard'")] = empty,
            },
            Array.Empty<(Regex, ResultSet)>().ToList());

        Assert.NotNull(store.Lookup("SELECT Id, Name, Price FROM Products"));
        Assert.NotNull(store.Lookup("SELECT Id, Name, Price FROM Products WHERE Name = N'Keyboard'"));
        Assert.Null(store.Lookup("SELECT Id, Name, Price FROM Products WHERE Name = N'Mouse'"));
    }

    [Fact]
    public void Pattern_Mapping_Requires_Full_Statement_Match()
    {
        var store = StoreWithPatterns(
            ("SELECT Id, Name, Price FROM Products", "base"),
            ("SELECT Id, Name, Price FROM Products WHERE Name = N'Keyboard'", "filtered"));

        Assert.Equal("base", store.Lookup("SELECT Id, Name, Price FROM Products")!.Rows[0][0]);
        Assert.Equal("filtered", store.Lookup("SELECT Id, Name, Price FROM Products WHERE Name = N'Keyboard'")!.Rows[0][0]);
        Assert.Null(store.Lookup("SELECT Id, Name, Price FROM Products WHERE Name = N'Mouse'"));
    }

    [Fact]
    public void Pattern_Mapping_Does_Not_Match_Substring_Only()
    {
        var store = StoreWithPatterns(("from\\s+Products", "hit"));

        Assert.Null(store.Lookup("SELECT Id, Name, Price FROM Products"));
    }
}
