using Microsoft.EntityFrameworkCore;
using payment_service.Models;

namespace payment_service.Data
{
    public class PaymentDbContext : DbContext
    {
        public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
        {
        }

        public DbSet<PaymentTransaction> Transactions => Set<PaymentTransaction>();
        public DbSet<UserWallet> Wallets => Set<UserWallet>();
        public DbSet<PaymentMethodTokenRecord> PaymentMethodTokens => Set<PaymentMethodTokenRecord>();
        public DbSet<EscrowRecord> Escrows => Set<EscrowRecord>();
        public DbSet<PaymentResultOutbox> PaymentResultOutbox => Set<PaymentResultOutbox>();
        public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<JournalLine> JournalLines => Set<JournalLine>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PaymentTransaction>(entity =>
            {
                entity.ToTable("payment_transactions");
                entity.HasKey(t => t.Id);
                entity.Property(t => t.Amount).HasColumnType("numeric(18,2)");
                // Enforce non-negative amounts at the database level as well as in app code.
                entity.ToTable(t => t.HasCheckConstraint("CK_payment_transactions_amount_positive", "\"Amount\" > 0"));
                // Unique only when present, so retried requests with the same SagaId can be
                // detected (dedupe/idempotency) while callers that omit SagaId aren't constrained.
                entity.HasIndex(t => t.SagaId)
                    .IsUnique()
                    .HasFilter("\"SagaId\" IS NOT NULL")
                    .HasDatabaseName("IX_payment_transactions_saga_id");
                entity.HasIndex(t => t.EscrowId)
                    .HasFilter("\"EscrowId\" IS NOT NULL")
                    .HasDatabaseName("IX_payment_transactions_escrow_id");
            });

            modelBuilder.Entity<UserWallet>(entity =>
            {
                entity.ToTable("user_wallets");
                entity.HasKey(w => w.UserId);
                entity.Property(w => w.Balance).HasColumnType("numeric(18,2)");
                entity.HasIndex(w => w.LedgerAccountId)
                    .IsUnique()
                    .HasFilter("\"LedgerAccountId\" IS NOT NULL")
                    .HasDatabaseName("IX_user_wallets_ledger_account_id");
                entity.HasOne(w => w.LedgerAccount)
                    .WithOne()
                    .HasForeignKey<UserWallet>(w => w.LedgerAccountId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(w => w.LastJournalEntry)
                    .WithMany()
                    .HasForeignKey(w => w.LastJournalEntryId)
                    .OnDelete(DeleteBehavior.Restrict);
                // Enforce non-negative balances at the database level as well as in app code —
                // a wallet must never be driven below zero by a race between concurrent charges.
                entity.ToTable(t => t.HasCheckConstraint("CK_user_wallets_balance_non_negative", "\"Balance\" >= 0"));
            });

            modelBuilder.Entity<PaymentMethodTokenRecord>(entity =>
            {
                entity.ToTable("payment_method_tokens");
                entity.HasKey(token => token.Id);
                entity.HasIndex(token => token.TokenHash)
                    .IsUnique()
                    .HasDatabaseName("IX_payment_method_tokens_token_hash");
                entity.HasIndex(token => token.ExpiresAt)
                    .HasDatabaseName("IX_payment_method_tokens_expires_at");
                entity.HasIndex(token => token.RedeemedAt)
                    .HasDatabaseName("IX_payment_method_tokens_redeemed_at");
            });

            modelBuilder.Entity<EscrowRecord>(entity =>
            {
                entity.ToTable("escrows");
                entity.HasKey(escrow => escrow.Id);
                entity.Property(escrow => escrow.Amount).HasColumnType("numeric(18,2)");
                entity.HasIndex(escrow => escrow.BookingId)
                    .IsUnique()
                    .HasDatabaseName("IX_escrows_booking_id");
                entity.HasIndex(escrow => escrow.FundingTransactionId)
                    .IsUnique()
                    .HasFilter("\"FundingTransactionId\" IS NOT NULL")
                    .HasDatabaseName("IX_escrows_funding_transaction_id");
                entity.HasIndex(escrow => escrow.ReleaseTransactionId)
                    .IsUnique()
                    .HasFilter("\"ReleaseTransactionId\" IS NOT NULL")
                    .HasDatabaseName("IX_escrows_release_transaction_id");
                entity.HasIndex(escrow => escrow.RefundTransactionId)
                    .IsUnique()
                    .HasFilter("\"RefundTransactionId\" IS NOT NULL")
                    .HasDatabaseName("IX_escrows_refund_transaction_id");
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint("CK_escrows_amount_positive", "\"Amount\" > 0");
                    table.HasCheckConstraint(
                        "CK_escrows_status_valid",
                        "\"Status\" IN ('PENDING', 'FUNDED', 'RELEASED', 'REFUNDED')");
                });
            });

            modelBuilder.Entity<PaymentResultOutbox>(entity =>
            {
                entity.ToTable("payment_result_outbox");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Payload).HasColumnType("jsonb");
                entity.HasIndex(row => row.SagaId)
                    .IsUnique()
                    .HasDatabaseName("IX_payment_result_outbox_saga_id");
                entity.HasIndex(row => row.TransactionId)
                    .IsUnique()
                    .HasDatabaseName("IX_payment_result_outbox_transaction_id");
                entity.HasIndex(row => new
                    {
                        row.DispatchStatus,
                        row.NextDispatchAttemptAt
                    })
                    .HasDatabaseName("IX_payment_result_outbox_pending");
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_payment_result_outbox_dispatch_status_valid",
                    "\"DispatchStatus\" IN ('PENDING', 'CLAIMED', 'DISPATCHED')"));
            });

            modelBuilder.Entity<LedgerAccount>(entity =>
            {
                entity.ToTable("ledger_accounts");
                entity.HasKey(account => account.Id);
                entity.HasIndex(account => new
                    {
                        account.OwnerUserId,
                        account.AccountType,
                        account.Currency
                    })
                    .IsUnique()
                    .HasFilter("\"OwnerUserId\" IS NOT NULL")
                    .HasDatabaseName("IX_ledger_accounts_owner_type_currency");
                entity.HasIndex(account => account.Currency)
                    .IsUnique()
                    .HasFilter("\"AccountType\" = 'SYSTEM_ISSUANCE'")
                    .HasDatabaseName("IX_ledger_accounts_system_issuance_currency");
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_ledger_accounts_account_type_valid",
                        "\"AccountType\" IN ('USER_WALLET', 'ESCROW_CUSTODY', 'SYSTEM_ISSUANCE')");
                    table.HasCheckConstraint(
                        "CK_ledger_accounts_status_valid",
                        "\"Status\" IN ('ACTIVE', 'CLOSED')");
                    table.HasCheckConstraint(
                        "CK_ledger_accounts_currency_valid",
                        "length(\"Currency\") = 3 AND \"Currency\" = upper(\"Currency\")");
                    table.HasCheckConstraint(
                        "CK_ledger_accounts_closed_at_valid",
                        "(\"Status\" = 'ACTIVE' AND \"ClosedAt\" IS NULL) OR " +
                        "(\"Status\" = 'CLOSED' AND \"ClosedAt\" IS NOT NULL)");
                    table.HasCheckConstraint(
                        "CK_ledger_accounts_owner_valid",
                        "(\"AccountType\" = 'SYSTEM_ISSUANCE' AND \"OwnerUserId\" IS NULL) OR " +
                        "(\"AccountType\" <> 'SYSTEM_ISSUANCE' AND \"OwnerUserId\" IS NOT NULL)");
                });
            });

            modelBuilder.Entity<JournalEntry>(entity =>
            {
                entity.ToTable("journal_entries");
                entity.HasKey(entry => entry.Id);
                entity.HasIndex(entry => entry.IdempotencyKey)
                    .IsUnique()
                    .HasDatabaseName("IX_journal_entries_idempotency_key");
                entity.HasIndex(entry => entry.PaymentTransactionId)
                    .IsUnique()
                    .HasFilter("\"PaymentTransactionId\" IS NOT NULL")
                    .HasDatabaseName("IX_journal_entries_payment_transaction_id");
                entity.HasIndex(entry => entry.SagaId)
                    .HasFilter("\"SagaId\" IS NOT NULL")
                    .HasDatabaseName("IX_journal_entries_saga_id");
                entity.HasIndex(entry => entry.EscrowId)
                    .HasFilter("\"EscrowId\" IS NOT NULL")
                    .HasDatabaseName("IX_journal_entries_escrow_id");
                entity.HasIndex(entry => entry.BookingId)
                    .HasFilter("\"BookingId\" IS NOT NULL")
                    .HasDatabaseName("IX_journal_entries_booking_id");
                entity.HasIndex(entry => entry.ReversesJournalEntryId)
                    .IsUnique()
                    .HasFilter("\"ReversesJournalEntryId\" IS NOT NULL")
                    .HasDatabaseName("IX_journal_entries_reverses_journal_entry_id");
                entity.HasOne(entry => entry.PaymentTransaction)
                    .WithOne()
                    .HasForeignKey<JournalEntry>(entry => entry.PaymentTransactionId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(entry => entry.ReversesJournalEntry)
                    .WithOne(entry => entry.ReversalJournalEntry)
                    .HasForeignKey<JournalEntry>(entry => entry.ReversesJournalEntryId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_journal_entries_operation_valid",
                        "\"Operation\" IN ('OPENING_BALANCE', 'USER_REGISTRATION_CREDIT', " +
                        "'LEGACY_PAYMENT', 'FUND_ESCROW', 'RELEASE_ESCROW', 'REFUND_ESCROW', " +
                        "'REVERSAL', 'ADMIN_ADJUSTMENT')");
                    table.HasCheckConstraint(
                        "CK_journal_entries_currency_valid",
                        "length(\"Currency\") = 3 AND \"Currency\" = upper(\"Currency\")");
                    table.HasCheckConstraint(
                        "CK_journal_entries_reversal_valid",
                        "(\"Operation\" = 'REVERSAL' AND \"ReversesJournalEntryId\" IS NOT NULL) OR " +
                        "(\"Operation\" <> 'REVERSAL' AND \"ReversesJournalEntryId\" IS NULL)");
                });
            });

            modelBuilder.Entity<JournalLine>(entity =>
            {
                entity.ToTable("journal_lines");
                entity.HasKey(line => line.Id);
                entity.Property(line => line.Amount).HasColumnType("numeric(18,2)");
                entity.HasIndex(line => new
                    {
                        line.JournalEntryId,
                        line.LineNumber
                    })
                    .IsUnique()
                    .HasDatabaseName("IX_journal_lines_entry_line_number");
                entity.HasIndex(line => new
                    {
                        line.AccountId,
                        line.CreatedAt,
                        line.Id
                    })
                    .HasDatabaseName("IX_journal_lines_account_created_id");
                entity.HasOne(line => line.JournalEntry)
                    .WithMany(entry => entry.Lines)
                    .HasForeignKey(line => line.JournalEntryId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.HasOne(line => line.Account)
                    .WithMany(account => account.JournalLines)
                    .HasForeignKey(line => line.AccountId)
                    .OnDelete(DeleteBehavior.Restrict);
                entity.ToTable(table =>
                {
                    table.HasCheckConstraint(
                        "CK_journal_lines_line_number_positive",
                        "\"LineNumber\" > 0");
                    table.HasCheckConstraint(
                        "CK_journal_lines_direction_valid",
                        "\"Direction\" IN ('DEBIT', 'CREDIT')");
                    table.HasCheckConstraint(
                        "CK_journal_lines_amount_positive",
                        "\"Amount\" > 0");
                });
            });
        }
    }
}
