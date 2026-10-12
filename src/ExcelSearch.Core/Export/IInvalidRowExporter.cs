namespace ExcelSearch.Core.Export;

/// <summary>Where the exported cell values came from.</summary>
public enum ExportMode
{
    /// <summary>The original workbook was re-read, so values are exactly as the user typed them.</summary>
    SourceFile,

    /// <summary>The original file was missing or changed, so the values parsed at import time were written instead.</summary>
    StagedValues,
}

public sealed record ExportResult(bool Success, int RowsWritten, ExportMode Mode, string? Note, string? DestinationPath)
{
    public static ExportResult Nothing(string note) => new(true, 0, ExportMode.SourceFile, note, null);
}

/// <summary>Writes a batch's Invalid rows to a new .xlsx so the user can fix them in Excel and import the file again.</summary>
public interface IInvalidRowExporter
{
    /// <param name="sourcePath">The workbook that was imported (used when it still exists and is unchanged).</param>
    /// <param name="progress">Rows written so far.</param>
    Task<ExportResult> ExportAsync(string sourcePath, int batchId, string destinationPath, IProgress<int>? progress,
        CancellationToken ct = default);
}
