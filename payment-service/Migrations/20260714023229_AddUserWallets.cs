using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddUserWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_wallets",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_wallets", x => x.UserId);
                    table.CheckConstraint("CK_user_wallets_balance_non_negative", "\"Balance\" >= 0");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_wallets");
        }
    }
}
