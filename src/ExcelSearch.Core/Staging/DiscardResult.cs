namespace ExcelSearch.Core.Staging;

/// <summary>
/// Outcome of DiscardAsync. A refusal (the batch is already committed or discarded, or does not exist) is an
/// expected answer, not an error: nothing was deleted and <see cref="Reason"/> says why in plain words.
/// </summary>
public sealed record DiscardResult(bool Discarded, string? Reason = null);
