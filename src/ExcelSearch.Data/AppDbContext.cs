using System;
using System.Collections.Generic;
using ExcelSearch.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExcelSearch.Data;

public partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<ImportBatch> ImportBatches { get; set; }

    public virtual DbSet<ImportStaging> ImportStagings { get; set; }

    public virtual DbSet<Transaction> Transactions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ImportBatch>(entity =>
        {
            entity.HasIndex(e => e.FileHash, "IX_ImportBatches_FileHash");

            entity.HasIndex(e => new { e.Status, e.ImportedAt }, "IX_ImportBatches_Status_ImportedAt");

            entity.Property(e => e.CommittedAt).HasPrecision(0);
            entity.Property(e => e.FileHash)
                .HasMaxLength(64)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.FileName).HasMaxLength(260);
            entity.Property(e => e.ImportedAt)
                .HasPrecision(0)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.ImportedBy).HasMaxLength(256);
            entity.Property(e => e.SheetName).HasMaxLength(100);
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .IsUnicode(false)
                .HasDefaultValue("Committed");
        });

        modelBuilder.Entity<ImportStaging>(entity =>
        {
            entity.HasKey(e => new { e.ImportBatchId, e.ExcelRowNumber });

            entity.ToTable("ImportStaging");

            entity.HasIndex(e => new { e.ImportBatchId, e.PayOrderNo, e.TxnRefSeqNo }, "IX_ImportStaging_Key");

            entity.HasIndex(e => new { e.ImportBatchId, e.Status, e.ExcelRowNumber }, "IX_ImportStaging_Status");

            entity.Property(e => e.AccountNo).HasMaxLength(50);
            entity.Property(e => e.ApplicationNo).HasMaxLength(50);
            entity.Property(e => e.BankName).HasMaxLength(100);
            entity.Property(e => e.BranchCode).HasMaxLength(20);
            entity.Property(e => e.BranchName).HasMaxLength(100);
            entity.Property(e => e.ChallanNo).HasMaxLength(50);
            entity.Property(e => e.ChequeInstNo).HasMaxLength(50);
            entity.Property(e => e.Cnic)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.Credit).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CustomerName).HasMaxLength(200);
            entity.Property(e => e.DataSource).HasMaxLength(100);
            entity.Property(e => e.DealerName).HasMaxLength(100);
            entity.Property(e => e.Debit).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Discount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.DownPaymentAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.ErrorText).HasMaxLength(2000);
            entity.Property(e => e.ExistingRowHash)
                .HasMaxLength(64)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.Narration1).HasMaxLength(500);
            entity.Property(e => e.Narration2).HasMaxLength(500);
            entity.Property(e => e.Narration3).HasMaxLength(500);
            entity.Property(e => e.Narration4).HasMaxLength(500);
            entity.Property(e => e.Narration5).HasMaxLength(500);
            entity.Property(e => e.PayOrderNo).HasMaxLength(50);
            entity.Property(e => e.PlotNo).HasMaxLength(50);
            entity.Property(e => e.Project).HasMaxLength(100);
            entity.Property(e => e.RegNo).HasMaxLength(50);
            entity.Property(e => e.RowHash)
                .HasMaxLength(64)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.RunningBalance).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.StreetNo).HasMaxLength(50);
            entity.Property(e => e.TxnRefSeqNo).HasMaxLength(50);

            entity.HasOne(d => d.ImportBatch).WithMany(p => p.ImportStagings)
                .HasForeignKey(d => d.ImportBatchId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ImportStaging_ImportBatches");
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(e => new { e.PayOrderNo, e.TxnRefSeqNo });

            entity.HasIndex(e => e.ApplicationNo, "IX_Transactions_ApplicationNo");

            entity.HasIndex(e => new { e.BankName, e.AccountNo }, "IX_Transactions_Bank_Account");

            entity.HasIndex(e => e.ChallanNo, "IX_Transactions_ChallanNo");

            entity.HasIndex(e => e.Cnic, "IX_Transactions_Cnic");

            entity.HasIndex(e => e.CustomerName, "IX_Transactions_CustomerName");

            entity.HasIndex(e => e.DealerName, "IX_Transactions_DealerName");

            entity.HasIndex(e => e.ImportBatchId, "IX_Transactions_ImportBatchId");

            entity.HasIndex(e => e.Project, "IX_Transactions_Project");

            entity.HasIndex(e => e.TxnRefSeqNo, "IX_Transactions_TxnRefSeqNo");

            entity.HasIndex(e => e.ValueDate, "IX_Transactions_ValueDate");

            entity.HasIndex(e => e.Id, "UQ_Transactions_Id").IsUnique();

            entity.Property(e => e.PayOrderNo).HasMaxLength(50);
            entity.Property(e => e.TxnRefSeqNo).HasMaxLength(50);
            entity.Property(e => e.AccountNo).HasMaxLength(50);
            entity.Property(e => e.ApplicationNo).HasMaxLength(50);
            entity.Property(e => e.BankName).HasMaxLength(100);
            entity.Property(e => e.BranchCode).HasMaxLength(20);
            entity.Property(e => e.BranchName).HasMaxLength(100);
            entity.Property(e => e.ChallanNo).HasMaxLength(50);
            entity.Property(e => e.ChequeInstNo).HasMaxLength(50);
            entity.Property(e => e.Cnic)
                .HasMaxLength(20)
                .IsUnicode(false);
            entity.Property(e => e.CreatedAt)
                .HasPrecision(0)
                .HasDefaultValueSql("(sysutcdatetime())");
            entity.Property(e => e.Credit).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.CustomerName).HasMaxLength(200);
            entity.Property(e => e.DataSource).HasMaxLength(100);
            entity.Property(e => e.DealerName).HasMaxLength(100);
            entity.Property(e => e.Debit).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Discount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.DownPaymentAmount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Id).HasDefaultValueSql("(newsequentialid())");
            entity.Property(e => e.Narration1).HasMaxLength(500);
            entity.Property(e => e.Narration2).HasMaxLength(500);
            entity.Property(e => e.Narration3).HasMaxLength(500);
            entity.Property(e => e.Narration4).HasMaxLength(500);
            entity.Property(e => e.Narration5).HasMaxLength(500);
            entity.Property(e => e.PlotNo).HasMaxLength(50);
            entity.Property(e => e.Project).HasMaxLength(100);
            entity.Property(e => e.RegNo).HasMaxLength(50);
            entity.Property(e => e.RowHash)
                .HasMaxLength(64)
                .IsUnicode(false)
                .IsFixedLength();
            entity.Property(e => e.RunningBalance).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.StreetNo).HasMaxLength(50);
            entity.Property(e => e.UpdatedAt).HasPrecision(0);

            entity.HasOne(d => d.ImportBatch).WithMany(p => p.Transactions)
                .HasForeignKey(d => d.ImportBatchId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Transactions_ImportBatches");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
