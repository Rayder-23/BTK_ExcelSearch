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

    /// <summary>Key in Transactions with the same RowHash: skipped.</summary>
    ExactDuplicate = 3,

    /// <summary>Repeat of an identical row earlier in the same file: skipped.</summary>
    FileDuplicate = 4,

    /// <summary>Key in Transactions but RowHash differs: the user decides.</summary>
    Conflict = 5,
}
