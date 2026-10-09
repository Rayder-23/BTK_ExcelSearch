using System.Collections;
using System.Data.Common;
using ExcelSearch.Core.Import;
using ExcelSearch.Core.Staging;

namespace ExcelSearch.Data;

/// <summary>
/// A forward-only reader over a lazy <see cref="ImportRow"/> stream, shaped like dbo.ImportStaging, so SqlBulkCopy
/// can pull rows one at a time. Nothing is buffered: Read() advances the enumerator and every value is computed
/// from the current row on demand. Columns the table fills itself (Overwrite, ExistingRowHash) are left out.
/// </summary>
internal sealed class ImportRowDataReader : DbDataReader
{
    private sealed record Column(string Name, Type Type, Func<ImportRowDataReader, ImportRow, object?> Get);

    // Order here is the reader's ordinal order; SqlBulkCopy maps by NAME, so it does not have to match the table.
    private static readonly Column[] Columns =
    {
        new("ImportBatchId", typeof(int), (r, _) => r._batchId),
        new("ExcelRowNumber", typeof(int), (_, x) => x.ExcelRowNumber),
        new("Status", typeof(byte), (_, x) => (byte)(x.IsValid ? RowStatus.Pending : RowStatus.Invalid)),
        new("ErrorText", typeof(string), (r, x) => x.IsValid ? null : Cut(string.Join("; ", x.Errors.Select(e => e.Message)), ErrorTextMax)),
        new("RowHash", typeof(string), (_, x) => x.IsValid ? x.RowHash : null),
        new("PayOrderNo", typeof(string), (_, x) => x.PayOrderNo),
        new("TxnRefSeqNo", typeof(string), (_, x) => x.TxnRefSeqNo),
        new("ValueDate", typeof(DateTime), (_, x) => x.ValueDate),
        new("BranchCode", typeof(string), (_, x) => x.BranchCode),
        new("BranchName", typeof(string), (_, x) => x.BranchName),
        new("RegNo", typeof(string), (_, x) => x.RegNo),
        new("ApplicationNo", typeof(string), (_, x) => x.ApplicationNo),
        new("PlotNo", typeof(string), (_, x) => x.PlotNo),
        new("StreetNo", typeof(string), (_, x) => x.StreetNo),
        new("ChallanNo", typeof(string), (_, x) => x.ChallanNo),
        new("ChequeInstNo", typeof(string), (_, x) => x.ChequeInstNo),
        new("CustomerName", typeof(string), (_, x) => x.CustomerName),
        new("Cnic", typeof(string), (_, x) => x.Cnic),
        new("Discount", typeof(decimal), (_, x) => x.Discount),
        new("DownPaymentAmount", typeof(decimal), (_, x) => x.DownPaymentAmount),
        new("Debit", typeof(decimal), (_, x) => x.Debit),
        new("Credit", typeof(decimal), (_, x) => x.Credit),
        new("RunningBalance", typeof(decimal), (_, x) => x.RunningBalance),
        new("BankName", typeof(string), (_, x) => x.BankName),
        new("AccountNo", typeof(string), (_, x) => x.AccountNo),
        new("Project", typeof(string), (_, x) => x.Project),
        new("DealerName", typeof(string), (_, x) => x.DealerName),
        new("DataSource", typeof(string), (_, x) => x.DataSource),
        new("Narration1", typeof(string), (_, x) => x.Narration1),
        new("Narration2", typeof(string), (_, x) => x.Narration2),
        new("Narration3", typeof(string), (_, x) => x.Narration3),
        new("Narration4", typeof(string), (_, x) => x.Narration4),
        new("Narration5", typeof(string), (_, x) => x.Narration5),
    };

    private static readonly Dictionary<string, int> Ordinals =
        Columns.Select((c, i) => (c.Name, i)).ToDictionary(t => t.Name, t => t.i, StringComparer.OrdinalIgnoreCase);

    public const int ErrorTextMax = 2000;

    /// <summary>Column names for SqlBulkCopy.ColumnMappings (same name on both sides).</summary>
    public static IEnumerable<string> ColumnNames => Columns.Select(c => c.Name);

    private readonly int _batchId;
    private readonly IEnumerator<ImportRow> _rows;
    private bool _closed;

    public ImportRowDataReader(int batchId, IEnumerable<ImportRow> rows)
    {
        _batchId = batchId;
        _rows = rows.GetEnumerator();
    }

    /// <summary>Rows handed to SqlBulkCopy so far.</summary>
    public int RowsRead { get; private set; }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    public override bool Read()
    {
        if (_closed || !_rows.MoveNext()) return false;
        RowsRead++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override object GetValue(int ordinal) => Columns[ordinal].Get(this, _rows.Current) ?? DBNull.Value;
    public override bool IsDBNull(int ordinal) => Columns[ordinal].Get(this, _rows.Current) is null;
    public override int FieldCount => Columns.Length;
    public override string GetName(int ordinal) => Columns[ordinal].Name;
    public override int GetOrdinal(string name) =>
        Ordinals.TryGetValue(name, out var i) ? i : throw new IndexOutOfRangeException(name);
    public override Type GetFieldType(int ordinal) => Columns[ordinal].Type;
    public override string GetDataTypeName(int ordinal) => Columns[ordinal].Type.Name;

    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, Columns.Length);
        for (var i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
    public override string GetString(int ordinal) => (string)GetValue(ordinal);
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override bool HasRows => true;
    public override bool IsClosed => _closed;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override bool NextResult() => false;

    public override void Close()
    {
        if (_closed) return;
        _closed = true;
        _rows.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    // Types this table never uses; SqlBulkCopy does not ask for them.
    public override bool GetBoolean(int ordinal) => throw new NotSupportedException();
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override char GetChar(int ordinal) => throw new NotSupportedException();
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
    public override double GetDouble(int ordinal) => throw new NotSupportedException();
    public override float GetFloat(int ordinal) => throw new NotSupportedException();
    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();
    public override short GetInt16(int ordinal) => throw new NotSupportedException();
    public override long GetInt64(int ordinal) => throw new NotSupportedException();
}
