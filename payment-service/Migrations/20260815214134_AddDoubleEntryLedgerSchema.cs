using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddDoubleEntryLedgerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PaymentTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SagaId = table.Column<Guid>(type: "uuid", nullable: true),
                    EscrowId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Operation = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ReversesJournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_entries", x => x.Id);
                    table.CheckConstraint("CK_journal_entries_currency_valid", "length(\"Currency\") = 3 AND \"Currency\" = upper(\"Currency\")");
                    table.CheckConstraint("CK_journal_entries_operation_valid", "\"Operation\" IN ('OPENING_BALANCE', 'USER_REGISTRATION_CREDIT', 'LEGACY_PAYMENT', 'FUND_ESCROW', 'RELEASE_ESCROW', 'REFUND_ESCROW', 'REVERSAL', 'ADMIN_ADJUSTMENT')");
                    table.CheckConstraint("CK_journal_entries_reversal_valid", "(\"Operation\" = 'REVERSAL' AND \"ReversesJournalEntryId\" IS NOT NULL) OR (\"Operation\" <> 'REVERSAL' AND \"ReversesJournalEntryId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_journal_entries_journal_entries_ReversesJournalEntryId",
                        column: x => x.ReversesJournalEntryId,
                        principalTable: "journal_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_journal_entries_payment_transactions_PaymentTransactionId",
                        column: x => x.PaymentTransactionId,
                        principalTable: "payment_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ledger_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccountType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_accounts", x => x.Id);
                    table.CheckConstraint("CK_ledger_accounts_account_type_valid", "\"AccountType\" IN ('USER_WALLET', 'ESCROW_CUSTODY', 'SYSTEM_ISSUANCE')");
                    table.CheckConstraint("CK_ledger_accounts_closed_at_valid", "(\"Status\" = 'ACTIVE' AND \"ClosedAt\" IS NULL) OR (\"Status\" = 'CLOSED' AND \"ClosedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_ledger_accounts_currency_valid", "length(\"Currency\") = 3 AND \"Currency\" = upper(\"Currency\")");
                    table.CheckConstraint("CK_ledger_accounts_owner_valid", "(\"AccountType\" = 'SYSTEM_ISSUANCE' AND \"OwnerUserId\" IS NULL) OR (\"AccountType\" <> 'SYSTEM_ISSUANCE' AND \"OwnerUserId\" IS NOT NULL)");
                    table.CheckConstraint("CK_ledger_accounts_status_valid", "\"Status\" IN ('ACTIVE', 'CLOSED')");
                });

            migrationBuilder.CreateTable(
                name: "journal_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<short>(type: "smallint", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_journal_lines", x => x.Id);
                    table.CheckConstraint("CK_journal_lines_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_journal_lines_direction_valid", "\"Direction\" IN ('DEBIT', 'CREDIT')");
                    table.CheckConstraint("CK_journal_lines_line_number_positive", "\"LineNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_journal_lines_journal_entries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "journal_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_journal_lines_ledger_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "ledger_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_booking_id",
                table: "journal_entries",
                column: "BookingId",
                filter: "\"BookingId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_escrow_id",
                table: "journal_entries",
                column: "EscrowId",
                filter: "\"EscrowId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_idempotency_key",
                table: "journal_entries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_payment_transaction_id",
                table: "journal_entries",
                column: "PaymentTransactionId",
                unique: true,
                filter: "\"PaymentTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_reverses_journal_entry_id",
                table: "journal_entries",
                column: "ReversesJournalEntryId",
                unique: true,
                filter: "\"ReversesJournalEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_entries_saga_id",
                table: "journal_entries",
                column: "SagaId",
                filter: "\"SagaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_journal_lines_account_created_id",
                table: "journal_lines",
                columns: new[] { "AccountId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_journal_lines_entry_line_number",
                table: "journal_lines",
                columns: new[] { "JournalEntryId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_accounts_owner_type_currency",
                table: "ledger_accounts",
                columns: new[] { "OwnerUserId", "AccountType", "Currency" },
                unique: true,
                filter: "\"OwnerUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_accounts_system_issuance_currency",
                table: "ledger_accounts",
                column: "Currency",
                unique: true,
                filter: "\"AccountType\" = 'SYSTEM_ISSUANCE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "journal_lines");

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "ledger_accounts");
        }
    }
}
