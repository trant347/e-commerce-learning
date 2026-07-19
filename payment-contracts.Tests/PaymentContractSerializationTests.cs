using System.Text.Json;
using Payment.Contracts.V1;
using Xunit;

namespace Payment.Contracts.Tests;

public class PaymentContractSerializationTests
{
    [Fact]
    public void PaymentRequestedV1_SerializesWithStableCamelCaseShapeAndSagaKey()
    {
        var sagaId = Guid.Parse("9fd5cb72-3a4b-42f6-aa5d-b0987df46c02");
        var escrowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var message = new PaymentRequestedV1
        {
            SagaId = sagaId,
            EscrowId = escrowId,
            BookingId = "booking-123",
            Operation = PaymentOperation.FundEscrow,
            Amount = 125.50m,
            Currency = "USD",
            PayerUserId = "alice",
            PayeeUserId = "admin-escrow",
            PaymentMethodToken = "pmt_opaque"
        };

        var json = JsonSerializer.Serialize(message, PaymentContractJson.SerializerOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(PaymentRequestedV1.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(sagaId, root.GetProperty("sagaId").GetGuid());
        Assert.Equal(escrowId, root.GetProperty("escrowId").GetGuid());
        Assert.Equal("booking-123", root.GetProperty("bookingId").GetString());
        Assert.Equal("FUND_ESCROW", root.GetProperty("operation").GetString());
        Assert.Equal(125.50m, root.GetProperty("amount").GetDecimal());
        Assert.Equal("USD", root.GetProperty("currency").GetString());
        Assert.Equal("alice", root.GetProperty("payerUserId").GetString());
        Assert.Equal("admin-escrow", root.GetProperty("payeeUserId").GetString());
        Assert.Equal("pmt_opaque", root.GetProperty("paymentMethodToken").GetString());
        Assert.False(root.TryGetProperty("kafkaMessageKey", out _));
        Assert.Equal(sagaId.ToString("D"), message.KafkaMessageKey);
    }

    [Fact]
    public void PaymentRequestedV1_DeserializesWhenACompatibleProducerAddsAField()
    {
        var json = """
            {
              "schemaVersion": 1,
              "sagaId": "9fd5cb72-3a4b-42f6-aa5d-b0987df46c02",
              "escrowId": "11111111-1111-1111-1111-111111111111",
              "bookingId": "booking-123",
              "operation": "FUND_ESCROW",
              "amount": 125.50,
              "currency": "USD",
              "payerUserId": "alice",
              "payeeUserId": "admin-escrow",
              "paymentMethodToken": "pmt_opaque",
              "futureOptionalField": "ignored"
            }
            """;

        var message = JsonSerializer.Deserialize<PaymentRequestedV1>(
            json,
            PaymentContractJson.SerializerOptions);

        Assert.NotNull(message);
        Assert.Equal(PaymentRequestedV1.CurrentSchemaVersion, message.SchemaVersion);
        Assert.Equal("booking-123", message.BookingId);
        Assert.Equal(PaymentOperation.FundEscrow, message.Operation);
        Assert.Equal("pmt_opaque", message.PaymentMethodToken);
    }

    [Fact]
    public void PaymentRequestedV1_InternalEscrowTransferOmitsPaymentMethodToken()
    {
        var message = new PaymentRequestedV1
        {
            SagaId = Guid.NewGuid(),
            EscrowId = Guid.NewGuid(),
            BookingId = "booking-123",
            Operation = PaymentOperation.ReleaseEscrow,
            Amount = 125.50m,
            Currency = "USD",
            PayerUserId = "admin-escrow",
            PayeeUserId = "bob"
        };

        var json = JsonSerializer.Serialize(message, PaymentContractJson.SerializerOptions);

        Assert.DoesNotContain("paymentMethodToken", json);
    }

    [Fact]
    public void PaymentResultV1_RoundTripsApprovedAndDeclinedEscrowOperations()
    {
        var sagaId = Guid.Parse("9fd5cb72-3a4b-42f6-aa5d-b0987df46c02");
        var escrowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var approved = new PaymentResultV1
        {
            SagaId = sagaId,
            EscrowId = escrowId,
            BookingId = "booking-123",
            Operation = PaymentOperation.FundEscrow,
            TransactionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Amount = 125.50m,
            Currency = "USD",
            Status = PaymentResultV1.StatusApproved
        };
        var declined = approved with
        {
            TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Status = PaymentResultV1.StatusDeclined,
            DeclineReason = "Insufficient balance"
        };

        var approvedJson = JsonSerializer.Serialize(approved, PaymentContractJson.SerializerOptions);
        var declinedJson = JsonSerializer.Serialize(declined, PaymentContractJson.SerializerOptions);
        var approvedCopy = JsonSerializer.Deserialize<PaymentResultV1>(
            approvedJson,
            PaymentContractJson.SerializerOptions);
        var declinedCopy = JsonSerializer.Deserialize<PaymentResultV1>(
            declinedJson,
            PaymentContractJson.SerializerOptions);

        Assert.Equal(approved, approvedCopy);
        Assert.Equal(declined, declinedCopy);
        Assert.DoesNotContain("declineReason", approvedJson);
        Assert.Contains("\"declineReason\":\"Insufficient balance\"", declinedJson);
        Assert.Equal(sagaId.ToString("D"), declined.KafkaMessageKey);
    }

    [Fact]
    public void PaymentResultV1_DeserializesWhenACompatibleProducerAddsAField()
    {
        var json = """
            {
              "schemaVersion": 1,
              "sagaId": "9fd5cb72-3a4b-42f6-aa5d-b0987df46c02",
              "escrowId": "11111111-1111-1111-1111-111111111111",
              "bookingId": "booking-123",
              "operation": "RELEASE_ESCROW",
              "transactionId": "22222222-2222-2222-2222-222222222222",
              "amount": 125.50,
              "currency": "USD",
              "status": "APPROVED",
              "futureOptionalField": "ignored"
            }
            """;

        var message = JsonSerializer.Deserialize<PaymentResultV1>(
            json,
            PaymentContractJson.SerializerOptions);

        Assert.NotNull(message);
        Assert.Equal(PaymentResultV1.CurrentSchemaVersion, message.SchemaVersion);
        Assert.Equal(PaymentOperation.ReleaseEscrow, message.Operation);
        Assert.Equal(PaymentResultV1.StatusApproved, message.Status);
        Assert.Null(message.DeclineReason);
    }

    [Fact]
    public void PaymentAcceptedResponseV1_SerializesAsPendingWithStatusUrl()
    {
        var sagaId = Guid.Parse("9fd5cb72-3a4b-42f6-aa5d-b0987df46c02");
        var escrowId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var response = new PaymentAcceptedResponseV1
        {
            SagaId = sagaId,
            EscrowId = escrowId,
            StatusUrl = $"/api/booking/payment-status/{sagaId:D}"
        };

        var json = JsonSerializer.Serialize(response, PaymentContractJson.SerializerOptions);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(sagaId, root.GetProperty("sagaId").GetGuid());
        Assert.Equal(escrowId, root.GetProperty("escrowId").GetGuid());
        Assert.Equal("PENDING", root.GetProperty("status").GetString());
        Assert.Equal($"/api/booking/payment-status/{sagaId:D}", root.GetProperty("statusUrl").GetString());
    }
}
