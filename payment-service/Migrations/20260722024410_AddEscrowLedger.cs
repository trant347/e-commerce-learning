using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddEscrowLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "escrows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RequesterUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TaskMasterUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CustodyUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FundingTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleaseTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RefundTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escrows", x => x.Id);
                    table.CheckConstraint("CK_escrows_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_escrows_status_valid", "\"Status\" IN ('PENDING', 'FUNDED', 'RELEASED', 'REFUNDED')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_escrows_booking_id",
                table: "escrows",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_escrows_funding_transaction_id",
                table: "escrows",
                column: "FundingTransactionId",
                unique: true,
                filter: "\"FundingTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_escrows_refund_transaction_id",
                table: "escrows",
                column: "RefundTransactionId",
                unique: true,
                filter: "\"RefundTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_escrows_release_transaction_id",
                table: "escrows",
                column: "ReleaseTransactionId",
                unique: true,
                filter: "\"ReleaseTransactionId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "escrows");
        }
    }
}
