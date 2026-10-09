using System.Globalization;
using System.Text;

namespace ExcelSearch.Core.Import;

/// <summary>
/// Turns raw cell values (null, string, double, DateTime, bool) into the normalized values stored in the database.
/// Pure functions with no Excel types, so they can be tested directly.
/// </summary>
internal static class FieldNormalizer
{
    private static readonly string[] DateFormats = { "d/M/yyyy", "d-M-yyyy", "yyyy-MM-dd", "d-MMM-yyyy" };

    /// <summary>Trimmed text; blank becomes null. Numbers render as plain digits (no 1E+12, no ".0").</summary>
    public static string? Text(object? raw)
    {
        var s = raw switch
        {
            null => null,
            string str => str,
            double d => NumberToPlain(d),
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Digits only (dashes, spaces and any other characters dropped); blank becomes null.</summary>
    public static string? Cnic(object? raw)
    {
        var text = Text(raw);
        if (text is null) return null;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c is >= '0' and <= '9') sb.Append(c);
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>Decimal rounded to 2 places. Text may contain thousands commas. Returns false with an error for junk.</summary>
    public static bool TryAmount(object? raw, out decimal? value, out string? error)
    {
        value = null;
        error = null;
        switch (raw)
        {
            case null:
                return true;
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e15)
                {
                    error = $"'{d}' is not a valid amount";
                    return false;
                }
                value = Math.Round((decimal)d, 2, MidpointRounding.AwayFromZero);
                return true;
            case string s:
                var t = s.Trim();
                if (t.Length == 0) return true;
                if (decimal.TryParse(t.Replace(",", ""), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var parsed))
                {
                    value = Math.Round(parsed, 2, MidpointRounding.AwayFromZero);
                    return true;
                }
                error = $"'{t}' is not a valid amount";
                return false;
            default:
                error = $"'{raw}' is not a valid amount";
                return false;
        }
    }

    /// <summary>
    /// DateTime, Excel serial number, or text in an explicit day-first format. Never guesses
    /// (so 03/04/2024 is 3 April, and anything else is an error).
    /// </summary>
    public static bool TryDate(object? raw, out DateTime? value, out string? error)
    {
        value = null;
        error = null;
        switch (raw)
        {
            case null:
                return true;
            case DateTime dt:
                value = dt.Date;
                return true;
            case double d:
                // Valid OLE range: 1 (1900-01-01) .. 2958465 (9999-12-31).
                if (d >= 1 && d < 2958466)
                {
                    value = DateTime.FromOADate(Math.Floor(d)).Date;
                    return true;
                }
                error = $"'{NumberToPlain(d)}' is not a valid date serial number";
                return false;
            case string s:
                var t = s.Trim();
                if (t.Length == 0) return true;
                if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    value = parsed.Date;
                    return true;
                }
                error = $"'{t}' is not a recognised date (expected dd/MM/yyyy, d-M-yyyy, yyyy-MM-dd or dd-MMM-yyyy)";
                return false;
            default:
                error = $"'{raw}' is not a valid date";
                return false;
        }
    }

    private static string NumberToPlain(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        // This format never uses scientific notation.
        return ((decimal)d).ToString("0.############################", CultureInfo.InvariantCulture);
    }
}
