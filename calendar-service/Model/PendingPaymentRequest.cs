using Payment.Contracts.V1;

namespace calendar_service.Model
{
    /// <summary>
    /// MongoDB-safe persisted copy of a payment command. It is embedded in the SagaState
    /// document so creating the saga and its outbox payload is one atomic insert.
    /// </summary>
    public class PendingPaymentRequest
    {
        public int SchemaVersion { get; set; }
        public Guid SagaId { get; set; }
        public Guid EscrowId { get; set; }
        public string BookingId { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string PayerUserId { get; set; } = string.Empty;
        public string PayeeUserId { get; set; } = string.Empty;
        public string? PaymentMethodToken { get; set; }

        public static PendingPaymentRequest FromContract(PaymentRequestedV1 request) => new()
        {
            SchemaVersion = request.SchemaVersion,
            SagaId = request.SagaId,
            EscrowId = request.EscrowId,
            BookingId = request.BookingId,
            Operation = request.Operation,
            Amount = request.Amount,
            Currency = request.Currency,
            PayerUserId = request.PayerUserId,
            PayeeUserId = request.PayeeUserId,
            PaymentMethodToken = request.PaymentMethodToken
        };

        public PaymentRequestedV1 ToContract() => new()
        {
            SchemaVersion = SchemaVersion,
            SagaId = SagaId,
            EscrowId = EscrowId,
            BookingId = BookingId,
            Operation = Operation,
            Amount = Amount,
            Currency = Currency,
            PayerUserId = PayerUserId,
            PayeeUserId = PayeeUserId,
            PaymentMethodToken = PaymentMethodToken
        };
    }
}
