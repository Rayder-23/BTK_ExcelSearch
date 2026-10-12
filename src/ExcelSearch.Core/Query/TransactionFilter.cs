namespace ExcelSearch.Core.Query;

/// <summary>How a text filter compares: "starts with" (the default, fast: it can use an index) or the whole value.</summary>
public enum TextMatchMode { StartsWith, Exact }

/// <summary>One text filter box: what was typed plus how to match it.</summary>
public readonly record struct TextCriterion(string? Value, TextMatchMode Mode = TextMatchMode.StartsWith)
{
    public static TextCriterion StartsWith(string? value) => new(value, TextMatchMode.StartsWith);
    public static TextCriterion Exact(string? value) => new(value, TextMatchMode.Exact);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// Everything the Query screen can filter on. All parts are optional and combined with AND; an empty filter matches
/// every row. It knows nothing about SQL or EF (provider-agnostic), so any query service can translate it.
/// Always call <see cref="Normalized"/> before using it: it trims the text, strips non-digits from the CNIC (the
/// importer stores CNICs digits-only), and drops anything that is blank so "no filter" is represented one way only.
/// </summary>
public sealed record TransactionFilter
{
    // Dates are inclusive on both ends (a transaction on the "to" date is included).
    public DateOnly? ValueDateFrom { get; init; }
    public DateOnly? ValueDateTo { get; init; }

    // Reference numbers: each has its own match mode (default StartsWith).
    public TextCriterion Cnic { get; init; }
    public TextCriterion ApplicationNo { get; init; }
    public TextCriterion RegNo { get; init; }
    public TextCriterion PayOrderNo { get; init; }
    public TextCriterion TxnRefSeqNo { get; init; }
    public TextCriterion ChallanNo { get; init; }
    public TextCriterion ChequeInstNo { get; init; }
    public TextCriterion AccountNo { get; init; }

    /// <summary>Matches anywhere inside the name (Contains).</summary>
    public string? CustomerName { get; init; }

    // Multi-select lists: a row matches when its value equals ANY of the selected ones. Empty = no restriction.
    public IReadOnlyList<string> BankNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Projects { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DealerNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BranchNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DataSources { get; init; } = Array.Empty<string>();

    // Amount ranges, inclusive.
    public decimal? CreditMin { get; init; }
    public decimal? CreditMax { get; init; }
    public decimal? DebitMin { get; init; }
    public decimal? DebitMax { get; init; }
    public decimal? DownPaymentMin { get; init; }
    public decimal? DownPaymentMax { get; init; }

    /// <summary>Only rows that came from this import batch.</summary>
    public int? ImportBatchId { get; init; }

    /// <summary>True when nothing is filtered (call on a normalized filter).</summary>
    public bool IsEmpty => this == Empty;

    public static TransactionFilter Empty { get; } = new();

    /// <summary>The same filter, cleaned: trimmed, CNIC reduced to digits, blanks removed, lists de-duplicated.</summary>
    public TransactionFilter Normalized() => this with
    {
        Cnic = CleanCnic(Cnic),
        ApplicationNo = Clean(ApplicationNo),
        RegNo = Clean(RegNo),
        PayOrderNo = Clean(PayOrderNo),
        TxnRefSeqNo = Clean(TxnRefSeqNo),
        ChallanNo = Clean(ChallanNo),
        ChequeInstNo = Clean(ChequeInstNo),
        AccountNo = Clean(AccountNo),
        CustomerName = string.IsNullOrWhiteSpace(CustomerName) ? null : CustomerName.Trim(),
        BankNames = CleanList(BankNames),
        Projects = CleanList(Projects),
        DealerNames = CleanList(DealerNames),
        BranchNames = CleanList(BranchNames),
        DataSources = CleanList(DataSources),
    };

    private static TextCriterion Clean(TextCriterion c) =>
        c.IsEmpty ? new TextCriterion(null, TextMatchMode.StartsWith) : c with { Value = c.Value!.Trim() };

    /// <summary>"35202-1234567-1" and "3520212345671" find the same row: only the digits are compared.</summary>
    private static TextCriterion CleanCnic(TextCriterion c)
    {
        if (c.IsEmpty) return new TextCriterion(null, TextMatchMode.StartsWith);
        var digits = new string(c.Value!.Where(char.IsAsciiDigit).ToArray());
        // Typed text with no digits at all (e.g. "abc") cannot match any CNIC; keep it so the search returns nothing
        // instead of silently ignoring the box.
        return c with { Value = digits.Length > 0 ? digits : c.Value.Trim() };
    }

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string>? values) =>
        values is null
            ? Array.Empty<string>()
            : values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Records compare lists by reference, so IsEmpty needs its own comparison.
    public bool Equals(TransactionFilter? other) => other is not null && Key() == other.Key();
    public override int GetHashCode() => Key().GetHashCode();

    private string Key() => string.Join('\u001F', new object?[]
    {
        ValueDateFrom, ValueDateTo, Cnic, ApplicationNo, RegNo, PayOrderNo, TxnRefSeqNo, ChallanNo, ChequeInstNo, AccountNo,
        CustomerName, string.Join('\u001E', BankNames), string.Join('\u001E', Projects), string.Join('\u001E', DealerNames),
        string.Join('\u001E', BranchNames), string.Join('\u001E', DataSources),
        CreditMin, CreditMax, DebitMin, DebitMax, DownPaymentMin, DownPaymentMax, ImportBatchId,
    });
}
