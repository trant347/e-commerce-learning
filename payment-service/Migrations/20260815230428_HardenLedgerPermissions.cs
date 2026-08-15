using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class HardenLedgerPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE UPDATE, DELETE, TRUNCATE
                ON TABLE journal_entries, journal_lines
                FROM PUBLIC;

                REVOKE UPDATE ("OwnerUserId", "AccountType", "Currency", "CreatedAt")
                ON TABLE ledger_accounts
                FROM PUBLIC;

                REVOKE EXECUTE
                ON FUNCTION reject_ledger_history_mutation(),
                            protect_ledger_account_identity(),
                            validate_journal_entry_posting()
                FROM PUBLIC;

                COMMENT ON TABLE journal_entries IS
                    'Immutable financial journal. Application roles require SELECT and INSERT only.';
                COMMENT ON TABLE journal_lines IS
                    'Immutable financial journal lines. Application roles require SELECT and INSERT only.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                COMMENT ON TABLE journal_entries IS NULL;
                COMMENT ON TABLE journal_lines IS NULL;
                """);
        }
    }
}
