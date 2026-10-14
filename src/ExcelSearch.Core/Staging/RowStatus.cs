namespace ExcelSearch.Core.Staging;

/// <summary>Classification of a staged row. The numbers are stored in ImportStaging.Status and must not change.</summary>
public enum RowStatus : byte
{
    /// <summary>Loaded, not yet classified.</summary>
    Pending = 0,

    /// <summary>Has validation errors (or conflicts with another row in the same file); see ErrorText.</summary>
    Invalid = 1,

    /// <summary>Key not in Transactions.</summary>
    New = 2,

    /// <summary>
    /// Same RowHash already in Transactions. A row WITH a full key (PayOrderNo and TxnRefSeqNo) is always skipped;
    /// a key-less row is flagged and imported unless the user chooses to skip flagged duplicates.
    /// </summary>
    ExactDuplicate = 3,

    /// <summary>
    /// Repeat of an identical row earlier in the same file. Keyed rows: skipped. Key-less rows: flagged, imported
    /// unless the user chooses to skip flagged duplicates (the first copy is New).
    /// </summary>
    FileDuplicate = 4,

    /// <summary>Key in Transactions but RowHash differs: the user decides. Only rows with a full key can be Conflict.</summary>
    Conflict = 5,

    /// <summary>
    /// Key-less row whose date, account, slip number and amounts match a row in Transactions although other values
    /// differ. A hint only: flagged, imported unless the user chooses to skip flagged duplicates.
    /// </summary>
    LikelyDuplicate = 6,
}
