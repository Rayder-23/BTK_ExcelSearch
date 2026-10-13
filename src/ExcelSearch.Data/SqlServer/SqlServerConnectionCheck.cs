using Microsoft.Data.SqlClient;

namespace ExcelSearch.Data.SqlServer;

/// <summary>
/// Opens one connection to find out, at startup, whether the configured SQL Server string is usable. Returns a short reason (never the
/// connection string itself, which holds the password) instead of throwing, so the app can show a dialog instead of crashing.
/// Encrypt and TrustServerCertificate are whatever the connection string says; nothing is overridden here.
/// </summary>
public static class SqlServerConnectionCheck
{
    /// <summary>The configuration key the dialog tells the user to fix.</summary>
    public const string ConfigKey = "ConnectionStrings:DefaultConnection";

    /// <summary>Null when a connection could be opened; otherwise why not.</summary>
    public static async Task<string?> CheckAsync(string? connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "No SQL Server connection string is set.";

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return null;
        }
        catch (OperationCanceledException)
        {
            return "The connection attempt timed out.";
        }
        catch (Exception ex)
        {
            // SqlException text names the server and the login ("Login failed for user 'x'"), never the password.
            return ex.Message;
        }
    }
}
