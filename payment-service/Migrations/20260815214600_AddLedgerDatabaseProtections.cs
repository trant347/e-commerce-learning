using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerDatabaseProtections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION reject_ledger_history_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION '% is not allowed on immutable table %', TG_OP, TG_TABLE_NAME
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER "TR_journal_entries_append_only"
                BEFORE UPDATE OR DELETE ON journal_entries
                FOR EACH ROW
                EXECUTE FUNCTION reject_ledger_history_mutation();

                CREATE TRIGGER "TR_journal_lines_append_only"
                BEFORE UPDATE OR DELETE ON journal_lines
                FOR EACH ROW
                EXECUTE FUNCTION reject_ledger_history_mutation();
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION protect_ledger_account_identity()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW."OwnerUserId" IS DISTINCT FROM OLD."OwnerUserId"
                       OR NEW."AccountType" IS DISTINCT FROM OLD."AccountType"
                       OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
                       OR NEW."CreatedAt" IS DISTINCT FROM OLD."CreatedAt" THEN
                        RAISE EXCEPTION 'Ledger account identity fields cannot be changed'
                            USING ERRCODE = '55000';
                    END IF;

                    IF OLD."Status" = 'CLOSED'
                       AND (NEW."Status" IS DISTINCT FROM OLD."Status"
                            OR NEW."ClosedAt" IS DISTINCT FROM OLD."ClosedAt") THEN
                        RAISE EXCEPTION 'Closed ledger accounts cannot be reopened or changed'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_ledger_accounts_protect_identity"
                BEFORE UPDATE ON ledger_accounts
                FOR EACH ROW
                EXECUTE FUNCTION protect_ledger_account_identity();
                """);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION validate_journal_entry_posting()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    target_entry_id uuid;
                    entry_currency varchar(3);
                    entry_operation varchar(30);
                    reversed_entry_id uuid;
                    line_count integer;
                    debit_total numeric(18,2);
                    credit_total numeric(18,2);
                BEGIN
                    IF TG_TABLE_NAME = 'journal_entries' THEN
                        target_entry_id := NEW."Id";
                    ELSE
                        target_entry_id := NEW."JournalEntryId";
                    END IF;

                    SELECT
                        "Currency",
                        "Operation",
                        "ReversesJournalEntryId"
                    INTO STRICT
                        entry_currency,
                        entry_operation,
                        reversed_entry_id
                    FROM journal_entries
                    WHERE "Id" = target_entry_id;

                    SELECT
                        COUNT(*),
                        COALESCE(SUM("Amount") FILTER (WHERE "Direction" = 'DEBIT'), 0),
                        COALESCE(SUM("Amount") FILTER (WHERE "Direction" = 'CREDIT'), 0)
                    INTO
                        line_count,
                        debit_total,
                        credit_total
                    FROM journal_lines
                    WHERE "JournalEntryId" = target_entry_id;

                    IF line_count < 2 THEN
                        RAISE EXCEPTION 'Journal entry % must contain at least two lines',
                            target_entry_id
                            USING ERRCODE = '23514';
                    END IF;

                    IF debit_total <> credit_total THEN
                        RAISE EXCEPTION 'Journal entry % is unbalanced: debits %, credits %',
                            target_entry_id,
                            debit_total,
                            credit_total
                            USING ERRCODE = '23514';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM journal_lines line
                        JOIN ledger_accounts account ON account."Id" = line."AccountId"
                        WHERE line."JournalEntryId" = target_entry_id
                          AND account."Currency" <> entry_currency
                    ) THEN
                        RAISE EXCEPTION 'Journal entry % contains an account with a different currency',
                            target_entry_id
                            USING ERRCODE = '23514';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM journal_lines line
                        JOIN ledger_accounts account ON account."Id" = line."AccountId"
                        WHERE line."JournalEntryId" = target_entry_id
                          AND account."Status" <> 'ACTIVE'
                    ) THEN
                        RAISE EXCEPTION 'Journal entry % contains a closed account',
                            target_entry_id
                            USING ERRCODE = '23514';
                    END IF;

                    IF entry_operation = 'REVERSAL'
                       AND (
                            EXISTS (
                                SELECT 1
                                FROM (
                                    SELECT
                                        original."AccountId",
                                        CASE original."Direction"
                                            WHEN 'DEBIT' THEN 'CREDIT'
                                            ELSE 'DEBIT'
                                        END AS "Direction",
                                        original."Amount"
                                    FROM journal_lines original
                                    WHERE original."JournalEntryId" = reversed_entry_id
                                    EXCEPT ALL
                                    SELECT
                                        reversal."AccountId",
                                        reversal."Direction",
                                        reversal."Amount"
                                    FROM journal_lines reversal
                                    WHERE reversal."JournalEntryId" = target_entry_id
                                ) missing_reversal_line
                            )
                            OR EXISTS (
                                SELECT 1
                                FROM (
                                    SELECT
                                        reversal."AccountId",
                                        reversal."Direction",
                                        reversal."Amount"
                                    FROM journal_lines reversal
                                    WHERE reversal."JournalEntryId" = target_entry_id
                                    EXCEPT ALL
                                    SELECT
                                        original."AccountId",
                                        CASE original."Direction"
                                            WHEN 'DEBIT' THEN 'CREDIT'
                                            ELSE 'DEBIT'
                                        END AS "Direction",
                                        original."Amount"
                                    FROM journal_lines original
                                    WHERE original."JournalEntryId" = reversed_entry_id
                                ) unexpected_reversal_line
                            )
                       ) THEN
                        RAISE EXCEPTION 'Journal entry % is not an exact reversal of %',
                            target_entry_id,
                            reversed_entry_id
                            USING ERRCODE = '23514';
                    END IF;

                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER "TR_journal_entries_validate_posting"
                AFTER INSERT ON journal_entries
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION validate_journal_entry_posting();

                CREATE CONSTRAINT TRIGGER "TR_journal_lines_validate_posting"
                AFTER INSERT ON journal_lines
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION validate_journal_entry_posting();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_journal_lines_validate_posting" ON journal_lines;
                DROP TRIGGER IF EXISTS "TR_journal_entries_validate_posting" ON journal_entries;
                DROP FUNCTION IF EXISTS validate_journal_entry_posting();

                DROP TRIGGER IF EXISTS "TR_ledger_accounts_protect_identity" ON ledger_accounts;
                DROP FUNCTION IF EXISTS protect_ledger_account_identity();

                DROP TRIGGER IF EXISTS "TR_journal_lines_append_only" ON journal_lines;
                DROP TRIGGER IF EXISTS "TR_journal_entries_append_only" ON journal_entries;
                DROP FUNCTION IF EXISTS reject_ledger_history_mutation();
                """);
        }
    }
}
