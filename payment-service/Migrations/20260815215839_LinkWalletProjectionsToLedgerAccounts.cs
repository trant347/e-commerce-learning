using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class LinkWalletProjectionsToLedgerAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastJournalEntryId",
                table: "user_wallets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LedgerAccountId",
                table: "user_wallets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProjectionVersion",
                table: "user_wallets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_user_wallets_LastJournalEntryId",
                table: "user_wallets",
                column: "LastJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_user_wallets_ledger_account_id",
                table: "user_wallets",
                column: "LedgerAccountId",
                unique: true,
                filter: "\"LedgerAccountId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_user_wallets_journal_entries_LastJournalEntryId",
                table: "user_wallets",
                column: "LastJournalEntryId",
                principalTable: "journal_entries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_user_wallets_ledger_accounts_LedgerAccountId",
                table: "user_wallets",
                column: "LedgerAccountId",
                principalTable: "ledger_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_wallets_journal_entries_LastJournalEntryId",
                table: "user_wallets");

            migrationBuilder.DropForeignKey(
                name: "FK_user_wallets_ledger_accounts_LedgerAccountId",
                table: "user_wallets");

            migrationBuilder.DropIndex(
                name: "IX_user_wallets_LastJournalEntryId",
                table: "user_wallets");

            migrationBuilder.DropIndex(
                name: "IX_user_wallets_ledger_account_id",
                table: "user_wallets");

            migrationBuilder.DropColumn(
                name: "LastJournalEntryId",
                table: "user_wallets");

            migrationBuilder.DropColumn(
                name: "LedgerAccountId",
                table: "user_wallets");

            migrationBuilder.DropColumn(
                name: "ProjectionVersion",
                table: "user_wallets");
        }
    }
}
