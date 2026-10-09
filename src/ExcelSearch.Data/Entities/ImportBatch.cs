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

    public string Status { get; set; } = null!;

    public DateTime? CommittedAt { get; set; }

    public string? ImportedBy { get; set; }

    public virtual ICollection<ImportStaging> ImportStagings { get; set; } = new List<ImportStaging>();

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
