using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using payment_service.Migrations;
using Xunit;

namespace payment_service.Tests
{
    public class LedgerDatabaseProtectionMigrationTests
    {
        [Fact]
        public void Up_AddsImmutableAndDeferredValidationTriggers()
        {
            var migration = new TestableMigration();
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");

            migration.Apply(builder);

            var sql = string.Join(
                Environment.NewLine,
                builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql));
            Assert.Contains("TR_journal_entries_append_only", sql);
            Assert.Contains("TR_journal_lines_append_only", sql);
            Assert.Contains("TR_ledger_accounts_protect_identity", sql);
            Assert.Contains("DEFERRABLE INITIALLY DEFERRED", sql);
            Assert.Contains("must contain at least two lines", sql);
            Assert.Contains("is unbalanced", sql);
            Assert.Contains("different currency", sql);
            Assert.Contains("contains a closed account", sql);
            Assert.Contains("is not an exact reversal", sql);
        }

        private sealed class TestableMigration : AddLedgerDatabaseProtections
        {
            public void Apply(MigrationBuilder migrationBuilder)
            {
                base.Up(migrationBuilder);
            }
        }
    }
}
