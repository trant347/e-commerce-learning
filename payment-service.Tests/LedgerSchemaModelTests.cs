using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using payment_service.Data;
using payment_service.Models;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerSchemaModelTests
    {
        [Fact]
        public void Model_DefinesLedgerTablesAndRequiredUniqueness()
        {
            using var dbContext = NewContext();
            var account = dbContext.Model.FindEntityType(typeof(LedgerAccount))!;
            var entry = dbContext.Model.FindEntityType(typeof(JournalEntry))!;
            var line = dbContext.Model.FindEntityType(typeof(JournalLine))!;

            Assert.Equal("ledger_accounts", account.GetTableName());
            Assert.Equal("journal_entries", entry.GetTableName());
            Assert.Equal("journal_lines", line.GetTableName());
            AssertUniqueIndex(account, "IX_ledger_accounts_owner_type_currency");
            AssertUniqueIndex(account, "IX_ledger_accounts_system_issuance_currency");
            AssertUniqueIndex(entry, "IX_journal_entries_idempotency_key");
            AssertUniqueIndex(entry, "IX_journal_entries_payment_transaction_id");
            AssertUniqueIndex(entry, "IX_journal_entries_reverses_journal_entry_id");
            AssertUniqueIndex(line, "IX_journal_lines_entry_line_number");
        }

        [Fact]
        public void Model_RestrictsLedgerDeletesAndUsesExactMoneyType()
        {
            using var dbContext = NewContext();
            var line = dbContext.Model.FindEntityType(typeof(JournalLine))!;
            var entry = dbContext.Model.FindEntityType(typeof(JournalEntry))!;

            Assert.Equal(
                "numeric(18,2)",
                line.FindProperty(nameof(JournalLine.Amount))!
                    .FindAnnotation(RelationalAnnotationNames.ColumnType)!
                    .Value);
            Assert.All(
                line.GetForeignKeys(),
                foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
            Assert.Equal(
                DeleteBehavior.Restrict,
                entry.GetForeignKeys().Single(foreignKey =>
                    foreignKey.Properties.Single().Name ==
                    nameof(JournalEntry.PaymentTransactionId)).DeleteBehavior);
        }

        private static PaymentDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<PaymentDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new PaymentDbContext(options);
        }

        private static void AssertUniqueIndex(
            IEntityType entityType,
            string databaseName)
        {
            var index = entityType.GetIndexes().Single(candidate =>
                candidate.GetDatabaseName() == databaseName);
            Assert.True(index.IsUnique);
        }
    }
}
