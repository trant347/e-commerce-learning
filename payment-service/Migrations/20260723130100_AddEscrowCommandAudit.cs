using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddEscrowCommandAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BookingId",
                table: "payment_transactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EscrowId",
                table: "payment_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "payment_transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayeeUserId",
                table: "payment_transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerUserId",
                table: "payment_transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaskMasterUserId",
                table: "payment_transactions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_escrow_id",
                table: "payment_transactions",
                column: "EscrowId",
                filter: "\"EscrowId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_transactions_escrow_id",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "BookingId",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "EscrowId",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "Operation",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "PayeeUserId",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "PayerUserId",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "TaskMasterUserId",
                table: "payment_transactions");
        }
    }
}
