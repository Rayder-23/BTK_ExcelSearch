using System.Globalization;
using System.Text;
using ExcelSearch.Core.Export;

namespace ExcelSearch.Core.Query;

/// <summary>
/// Turns a stream of row chunks into an .xlsx (the existing forward-only <see cref="StreamingXlsxWriter"/>) or a CSV.
/// Only one chunk is alive at a time, so memory does not depend on the result size. The file is built as
/// "&lt;destination&gt;.part" and moved into place at the end; on cancel or error the .part file is deleted.
/// </summary>
public static class TransactionExportWriter
{
    /// <summary>One exportable field: the TransactionRecord property name, the header text in the file, and how to read it.</summary>
    private sealed record Field(string Name, string Header, Func<TransactionRecord, object?> Get);

    // The 28 fields in the order a full export writes them. A caller picks a subset by property name (see WriteAsync).
    private static readonly Field[] Fields =
    {
        new(nameof(TransactionRecord.PayOrderNo), "Pay Order No", r => r.PayOrderNo),
        new(nameof(TransactionRecord.TxnRefSeqNo), "Txn Ref/Seq No", r => r.TxnRefSeqNo),
        new(nameof(TransactionRecord.ValueDate), "Value Date", r => r.ValueDate.ToDateTime(TimeOnly.MinValue)),
        new(nameof(TransactionRecord.BranchCode), "Branch Code", r => r.BranchCode),
        new(nameof(TransactionRecord.BranchName), "Branch Name", r => r.BranchName),
        new(nameof(TransactionRecord.RegNo), "Reg No", r => r.RegNo),
        new(nameof(TransactionRecord.ApplicationNo), "Application No", r => r.ApplicationNo),
        new(nameof(TransactionRecord.PlotNo), "Plot No", r => r.PlotNo),
        new(nameof(TransactionRecord.StreetNo), "Street No", r => r.StreetNo),
        new(nameof(TransactionRecord.ChallanNo), "Challan No", r => r.ChallanNo),
        new(nameof(TransactionRecord.ChequeInstNo), "Cheque/Inst No", r => r.ChequeInstNo),
        new(nameof(TransactionRecord.CustomerName), "Customer Name", r => r.CustomerName),
        new(nameof(TransactionRecord.Cnic), "CNIC", r => r.Cnic),
        new(nameof(TransactionRecord.Discount), "Discount", r => r.Discount),
        new(nameof(TransactionRecord.DownPaymentAmount), "Down Payment Amount", r => r.DownPaymentAmount),
        new(nameof(TransactionRecord.Debit), "Debit", r => r.Debit),
        new(nameof(TransactionRecord.Credit), "Credit", r => r.Credit),
        new(nameof(TransactionRecord.RunningBalance), "Running Balance", r => r.RunningBalance),
        new(nameof(TransactionRecord.BankName), "Bank Name", r => r.BankName),
        new(nameof(TransactionRecord.AccountNo), "Account No", r => r.AccountNo),
        new(nameof(TransactionRecord.Project), "Project", r => r.Project),
        new(nameof(TransactionRecord.DealerName), "Dealer Name", r => r.DealerName),
        new(nameof(TransactionRecord.DataSource), "Data Source", r => r.DataSource),
        new(nameof(TransactionRecord.Narration1), "Narration 1", r => r.Narration1),
        new(nameof(TransactionRecord.Narration2), "Narration 2", r => r.Narration2),
        new(nameof(TransactionRecord.Narration3), "Narration 3", r => r.Narration3),
        new(nameof(TransactionRecord.Narration4), "Narration 4", r => r.Narration4),
        new(nameof(TransactionRecord.Narration5), "Narration 5", r => r.Narration5),
    };

    /// <summary>The property names that can be exported, in full-export order.</summary>
    public static IReadOnlyList<string> AllColumns { get; } = Fields.Select(f => f.Name).ToArray();

    /// <summary>
    /// Looks the requested names up in the fixed table (so a caller can never name anything else), keeping the caller's order.
    /// Null means all 28 fields. Throws before any file is created if a name is unknown, listed twice, or the list is empty.
    /// </summary>
    private static Field[] Pick(IReadOnlyList<string>? names)
    {
        if (names is null) return Fields;
        if (names.Count == 0) throw new ArgumentException("Choose at least one column to export.", nameof(names));
        var byName = Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var picked = new List<Field>();
        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var field)) throw new ArgumentException($"\"{name}\" is not an exportable column.", nameof(names));
            if (picked.Contains(field)) throw new ArgumentException($"\"{name}\" is listed twice.", nameof(names));
            picked.Add(field);
        }
        return picked.ToArray();
    }

    /// <param name="columns">The property names to write, in this order; null writes all 28.</param>
    /// <returns>The number of data rows written.</returns>
    public static async Task<long> WriteAsync(IAsyncEnumerable<IReadOnlyList<TransactionRecord>> chunks, ExportFormat format,
        string destinationPath, IProgress<long>? progress, CancellationToken ct, IReadOnlyList<string>? columns = null)
    {
        var fields = Pick(columns);
        var part = destinationPath + ".part";
        long written = 0;
        try
        {
            await using (var stream = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65_536))
            {
                if (format == ExportFormat.Xlsx)
                    written = await WriteXlsxAsync(stream, chunks, fields, progress, ct).ConfigureAwait(false);
                else
                    written = await WriteCsvAsync(stream, chunks, fields, progress, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(part, destinationPath, overwrite: true);
            return written;
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task<long> WriteXlsxAsync(Stream stream, IAsyncEnumerable<IReadOnlyList<TransactionRecord>> chunks,
        Field[] fields, IProgress<long>? progress, CancellationToken ct)
    {
        long n = 0;
        using var writer = new StreamingXlsxWriter(stream, "Transactions");
        writer.WriteHeader(fields.Select(f => f.Header).ToArray());
        var cells = new object?[fields.Length];

        await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (var r in chunk)
            {
                ct.ThrowIfCancellationRequested();
                Fill(cells, fields, r);
                writer.WriteRow(cells);
                n++;
            }
            progress?.Report(n);
        }
        writer.Complete();
        return n;
    }

    private static async Task<long> WriteCsvAsync(Stream stream, IAsyncEnumerable<IReadOnlyList<TransactionRecord>> chunks,
        Field[] fields, IProgress<long>? progress, CancellationToken ct)
    {
        long n = 0;
        // UTF-8 with a byte-order mark so Excel opens names with accents/Urdu transliterations correctly.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true), 65_536, leaveOpen: true);
        writer.NewLine = "\r\n";
        await writer.WriteLineAsync(string.Join(',', fields.Select(f => Csv(f.Header)))).ConfigureAwait(false);
        var cells = new object?[fields.Length];
        var line = new StringBuilder(512);

        await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (var r in chunk)
            {
                ct.ThrowIfCancellationRequested();
                Fill(cells, fields, r);
                line.Clear();
                for (var i = 0; i < cells.Length; i++)
                {
                    if (i > 0) line.Append(',');
                    line.Append(Csv(cells[i] switch
                    {
                        null => "",
                        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        string s => s,
                        var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? "",
                    }));
                }
                await writer.WriteLineAsync(line.ToString()).ConfigureAwait(false);
                n++;
            }
            progress?.Report(n);
        }
        await writer.FlushAsync(ct).ConfigureAwait(false);
        return n;
    }

    /// <summary>Quotes a CSV field when it holds a comma, quote or line break (quotes are doubled).</summary>
    internal static string Csv(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void Fill(object?[] cells, Field[] fields, TransactionRecord r)
    {
        for (var i = 0; i < fields.Length; i++) cells[i] = fields[i].Get(r);
    }
}
