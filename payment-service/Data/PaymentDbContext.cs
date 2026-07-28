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
        }
    }
}
