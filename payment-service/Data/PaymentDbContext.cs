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
            });
        }
    }
}
