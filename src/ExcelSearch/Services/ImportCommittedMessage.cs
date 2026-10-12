namespace ExcelSearch.Services;

/// <summary>
/// Broadcast when an import has been committed to the database. The Import screen sends it, the Query screen listens
/// (to drop its cached dropdown lists) and neither knows about the other. This is the MVVM "messenger" pattern, a bit
/// like a domain event in an ASP.NET app.
/// </summary>
public sealed record ImportCommittedMessage(int BatchId);
