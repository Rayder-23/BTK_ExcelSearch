using System;
using System.Collections.Generic;

namespace ExcelSearch.Data.Entities;

public partial class ImportBatch
{
    public int Id { get; set; }

    public string FileName { get; set; } = null!;

    public string? SheetName { get; set; }

    public string FileHash { get; set; } = null!;

    public DateTime ImportedAt { get; set; }

    public int TotalRows { get; set; }

    public int NewRows { get; set; }

    public int SkippedRows { get; set; }

    public int UpdatedRows { get; set; }

    public int RejectedRows { get; set; }

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
