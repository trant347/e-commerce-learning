using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace payment_service.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentResultOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_result_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    DispatchStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DispatchAttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextDispatchAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DispatchClaimExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DispatchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastDispatchError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TraceParent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_result_outbox", x => x.Id);
                    table.CheckConstraint("CK_payment_result_outbox_dispatch_status_valid", "\"DispatchStatus\" IN ('PENDING', 'CLAIMED', 'DISPATCHED')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_result_outbox_pending",
                table: "payment_result_outbox",
                columns: new[] { "DispatchStatus", "NextDispatchAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_result_outbox_saga_id",
                table: "payment_result_outbox",
                column: "SagaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_result_outbox_transaction_id",
                table: "payment_result_outbox",
                column: "TransactionId",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO payment_result_outbox (
                    "Id",
                    "SagaId",
                    "TransactionId",
                    "Payload",
                    "DispatchStatus",
                    "DispatchAttemptCount",
                    "NextDispatchAttemptAt",
                    "CreatedAt")
                SELECT
                    "Id",
                    "SagaId",
                    "Id",
                    jsonb_strip_nulls(jsonb_build_object(
                        'schemaVersion', 1,
                        'sagaId', "SagaId",
                        'escrowId', "EscrowId",
                        'bookingId', "BookingId",
                        'operation', "Operation",
                        'transactionId', "Id",
                        'amount', "Amount",
                        'currency', "Currency",
                        'status', "Status",
                        'declineReason', "DeclineReason")),
                    'PENDING',
                    0,
                    NOW(),
                    "CreatedAt"
                FROM payment_transactions
                WHERE "SagaId" IS NOT NULL
                  AND "EscrowId" IS NOT NULL
                  AND "BookingId" IS NOT NULL
                  AND "Operation" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_result_outbox");
        }
    }
}
