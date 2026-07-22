using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentMethodTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_method_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaskedCardNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SimulatesDecline = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_method_tokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_method_tokens_expires_at",
                table: "payment_method_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_payment_method_tokens_redeemed_at",
                table: "payment_method_tokens",
                column: "RedeemedAt");

            migrationBuilder.CreateIndex(
                name: "IX_payment_method_tokens_token_hash",
                table: "payment_method_tokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_method_tokens");
        }
    }
}
