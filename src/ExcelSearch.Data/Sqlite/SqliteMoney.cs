using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ExcelSearch.Data.Sqlite;

/// <summary>
/// How values are stored on SQLite where the engine has no matching type. Used by the EF value converters and by the
/// staging loader (which writes with plain ADO.NET), so there is exactly one definition of each rule.
/// </summary>
internal static class SqliteValues
{
    /// <summary>Money -> whole minor units (cents), rounding half away from zero: 0.005 -> 1, -0.005 -> -1, 12.50 -> 1250.</summary>
    public static long ToMinorUnits(decimal amount) =>
        checked((long)Math.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));

    /// <summary>Cents -> decimal with scale 2 (so 1250 is 12.50 and 100 is 1.00, like SQL Server's decimal(18,2)).</summary>
    public static decimal FromMinorUnits(long cents)
    {
        var negative = cents < 0;
        var magnitude = negative ? unchecked((ulong)(-(cents + 1)) + 1UL) : (ulong)cents;
        return new decimal((int)(magnitude & 0xFFFFFFFF), (int)(magnitude >> 32), 0, negative, 2);
    }

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>A timestamp as ISO 8601 UTC text. Local kinds are converted; an unspecified kind is taken to be UTC already.</summary>
    public static string ToIsoUtc(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Reads ISO UTC text back as a DateTime of kind Utc.</summary>
    public static DateTime FromIsoUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>The ISO date text of a date ('yyyy-MM-dd').</summary>
    public static string ToIsoDate(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static readonly ValueConverter<decimal, long> MoneyConverter =
        new(v => ToMinorUnits(v), v => FromMinorUnits(v));

    public static readonly ValueConverter<DateTime, string> TimestampConverter =
        new(v => ToIsoUtc(v), v => FromIsoUtc(v));
}
