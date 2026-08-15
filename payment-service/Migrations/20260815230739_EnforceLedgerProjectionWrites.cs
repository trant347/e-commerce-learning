using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class EnforceLedgerProjectionWrites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION protect_wallet_projection_updates()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF current_setting(
                        'payment_service.allow_projection_update',
                        true) IS DISTINCT FROM 'on' THEN
                        RAISE EXCEPTION
                            'Wallet financial projections may only be updated by the ledger service'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_user_wallets_protect_projection"
                BEFORE UPDATE OF "Balance", "ProjectionVersion", "LastJournalEntryId"
                ON user_wallets
                FOR EACH ROW
                WHEN (
                    OLD."Balance" IS DISTINCT FROM NEW."Balance"
                    OR OLD."ProjectionVersion" IS DISTINCT FROM NEW."ProjectionVersion"
                    OR OLD."LastJournalEntryId" IS DISTINCT FROM NEW."LastJournalEntryId")
                EXECUTE FUNCTION protect_wallet_projection_updates();

                REVOKE EXECUTE
                ON FUNCTION protect_wallet_projection_updates()
                FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_user_wallets_protect_projection"
                    ON user_wallets;
                DROP FUNCTION IF EXISTS protect_wallet_projection_updates();
                """);
        }
    }
}
