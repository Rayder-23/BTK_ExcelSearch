using System.Text.RegularExpressions;

namespace ExcelSearch.Core.Import;

/// <summary>
/// Pulls identifying hints out of the free-text narrations of bank-statement sheets that carry no PayOrderNo /
/// TxnRefSeqNo. The results are hints, not facts: narrations are typed by bank staff, so a value may be missing or
/// wrong. The raw narrations are never changed, and the derived values are not part of RowHash.
/// </summary>
internal static class NarrationExtractor
{
    // A slip number is the 8-digit run in Narration 1 ("03128211", "R 02704864"). Only the digits are kept.
    private static readonly Regex Slip = new(@"(?<!\d)\d{8}(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A CNIC is 13 digits, written plain or as 12345-1234567-1, not part of a longer number.
    private static readonly Regex Cnic = new(@"(?<!\d)(\d{5})-?(\d{7})-?(\d)(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // UBL statements carry the branch inside Narration 1 ("... /Br: 1872 /Doc No:791666069") and leave the Br. Code column empty.
    private static readonly Regex Branch = new(@"/Br:\s*(\d{1,10})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static void Apply(ImportRow row)
    {
        // A row with a Txn Ref (the bank's own reference, e.g. UBL) has no slip number: its narration is bank text whose
        // digit runs would be mistaken for one. Cash rows (HBL) have no Txn Ref and carry the slip number in Narration 1.
        row.SlipNo = string.IsNullOrEmpty(row.TxnRefSeqNo) ? SlipNo(row.Narration1) : null;
        if (string.IsNullOrEmpty(row.BranchCode)) row.BranchCode = BranchCode(row.Narration1);
        row.NarrationCnic = NarrationCnic(row.Narration2, row.Narration3, row.Narration4, row.Narration5);
    }

    /// <summary>The branch code after "/Br:" in the text (kept as text, so "0949" keeps its zero), or null.</summary>
    public static string? BranchCode(string? narration1)
    {
        if (string.IsNullOrEmpty(narration1)) return null;
        var m = Branch.Match(narration1);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The first 8-digit run in the text, or null.</summary>
    public static string? SlipNo(string? narration1)
    {
        if (string.IsNullOrEmpty(narration1)) return null;
        var m = Slip.Match(narration1);
        return m.Success ? m.Value : null;
    }

    /// <summary>The first 13-digit CNIC-like number in Narrations 2-5 (in that order), digits only, or null.</summary>
    public static string? NarrationCnic(params string?[] narrations)
    {
        foreach (var text in narrations)
        {
            if (string.IsNullOrEmpty(text)) continue;
            var m = Cnic.Match(text);
            if (m.Success) return m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value;
        }
        return null;
    }
}
