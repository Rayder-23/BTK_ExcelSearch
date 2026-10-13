using ExcelSearch.Data.SqlServer;

namespace ExcelSearch.Tests;

public class SqlServerConnectionCheckTests
{
    // A missing or malformed connection string must come back as a message for the startup dialog, not as an exception.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("this is not a connection string")]
    public async Task A_missing_or_malformed_connection_string_is_reported_not_thrown(string? connectionString)
    {
        var problem = await SqlServerConnectionCheck.CheckAsync(connectionString);

        Assert.False(string.IsNullOrWhiteSpace(problem));
    }
}
