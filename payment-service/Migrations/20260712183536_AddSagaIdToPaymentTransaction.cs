using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaIdToPaymentTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SagaId",
                table: "payment_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_saga_id",
                table: "payment_transactions",
                column: "SagaId",
                unique: true,
                filter: "\"SagaId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_transactions_saga_id",
                table: "payment_transactions");

            migrationBuilder.DropColumn(
                name: "SagaId",
                table: "payment_transactions");
        }
    }
}
