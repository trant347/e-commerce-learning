using System.Reflection;
using System.Text.Json;
using calendar_service.Contracts;
using calendar_service.Model;
using Payment.Contracts.V1;
using Xunit;

namespace calendar_service.Tests
{
    /// <summary>
    /// <see cref="BookingResponse"/> is hand-mapped from <see cref="Booking"/>, so these tests
    /// exist to make silent contract drift impossible: a property added to the entity but not to
    /// the response (or a mapping that forgets to copy one) fails here rather than in the browser.
    /// </summary>
    public class BookingResponseTests
    {
        /// <summary>Payment projection fields live only on the response, never on the document.</summary>
        private static readonly string[] ResponseOnlyProperties =
        {
            nameof(BookingResponse.PaymentPending),
            nameof(BookingResponse.LatestPaymentSagaId),
            nameof(BookingResponse.LatestPaymentStatus),
            nameof(BookingResponse.LatestPaymentOperation),
            nameof(BookingResponse.LatestPaymentFailureReason)
        };

        [Fact]
        public void Response_ExposesEveryEntityProperty_PlusThePaymentProjection()
        {
            var entityProperties = PublicPropertyNames(typeof(Booking));
            var responseProperties = PublicPropertyNames(typeof(BookingResponse));

            var missing = entityProperties.Except(responseProperties).ToList();
            Assert.True(
                missing.Count == 0,
                $"BookingResponse is missing entity properties: {string.Join(", ", missing)}");

            var unexpected = responseProperties
                .Except(entityProperties)
                .Except(ResponseOnlyProperties)
                .ToList();
            Assert.True(
                unexpected.Count == 0,
                $"BookingResponse exposes unknown properties: {string.Join(", ", unexpected)}");
        }

        [Fact]
        public void Entity_NoLongerCarriesPaymentProjectionFields()
        {
            // The projection is a read model. Keeping it off the document stops a write path from
            // ever persisting a transient payment status onto the booking.
            var entityProperties = PublicPropertyNames(typeof(Booking));

            foreach (var property in ResponseOnlyProperties)
            {
                Assert.DoesNotContain(property, entityProperties);
            }
        }

        [Fact]
        public void From_CopiesEveryEntityValue()
        {
            var booking = FullyPopulatedBooking();

            var response = BookingResponse.From(booking);

            foreach (var entityProperty in typeof(Booking).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                var responseProperty = typeof(BookingResponse).GetProperty(entityProperty.Name);
                Assert.NotNull(responseProperty);
                Assert.Equal(
                    entityProperty.GetValue(booking),
                    responseProperty!.GetValue(response));
            }
        }

        [Fact]
        public void SerializedResponse_KeepsTheComputedFieldsClientsRelyOn()
        {
            // slotEnd is asserted by the e2e suite and offeredTotalAmount is rendered by the
            // calendar and incoming-bookings screens; both are computed, so they are easy to drop.
            var response = BookingResponse.From(FullyPopulatedBooking());

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(
                response,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            Assert.True(document.RootElement.TryGetProperty("slotEnd", out _));
            Assert.Equal(
                150m,
                document.RootElement.GetProperty("offeredTotalAmount").GetDecimal());
        }

        [Fact]
        public void WithLatestPayment_StartedSaga_MarksPaymentPending()
        {
            var sagaId = Guid.NewGuid();

            var response = BookingResponse.From(FullyPopulatedBooking())
                .WithLatestPayment(new SagaState
                {
                    SagaId = sagaId,
                    BookingId = "bk-1",
                    EscrowId = Guid.NewGuid(),
                    Operation = PaymentOperation.FundEscrow,
                    Status = SagaState.StatusStarted
                });

            Assert.True(response.PaymentPending);
            Assert.Equal(sagaId, response.LatestPaymentSagaId);
            Assert.Equal(PaymentStatusResponseV1.PendingStatus, response.LatestPaymentStatus);
            Assert.Equal(PaymentOperation.FundEscrow, response.LatestPaymentOperation);
        }

        [Fact]
        public void WithLatestPayment_ResolvedSaga_ReportsTerminalStatus()
        {
            var response = BookingResponse.From(FullyPopulatedBooking())
                .WithLatestPayment(new SagaState
                {
                    SagaId = Guid.NewGuid(),
                    BookingId = "bk-1",
                    EscrowId = Guid.NewGuid(),
                    Operation = PaymentOperation.ReleaseEscrow,
                    Status = SagaState.StatusFailed,
                    FailureReason = "Card declined"
                });

            Assert.False(response.PaymentPending);
            Assert.Equal(SagaState.StatusFailed, response.LatestPaymentStatus);
            Assert.Equal("Card declined", response.LatestPaymentFailureReason);
        }

        [Fact]
        public void WithLatestPayment_LegacySagaWithoutEscrow_OnlyReportsPending()
        {
            var response = BookingResponse.From(FullyPopulatedBooking())
                .WithLatestPayment(new SagaState
                {
                    SagaId = Guid.NewGuid(),
                    BookingId = "bk-1",
                    Status = SagaState.StatusStarted
                });

            Assert.True(response.PaymentPending);
            Assert.Null(response.LatestPaymentSagaId);
            Assert.Null(response.LatestPaymentOperation);
        }

        [Fact]
        public void WithLatestPayment_NoSaga_LeavesProjectionEmpty()
        {
            var response = BookingResponse.From(FullyPopulatedBooking())
                .WithLatestPayment(null);

            Assert.False(response.PaymentPending);
            Assert.Null(response.LatestPaymentSagaId);
            Assert.Null(response.LatestPaymentStatus);
        }

        private static Booking FullyPopulatedBooking() => new()
        {
            Id = "bk-1",
            TaskMasterId = "tm-1",
            TaskMasterUsername = "bob",
            RequesterUsername = "alice",
            SlotStart = new DateTime(2026, 8, 22, 16, 0, 0, DateTimeKind.Utc),
            DurationHours = 3,
            OfferedRatePerHour = 50m,
            AgreedAmount = 150m,
            AgreedCurrency = "USD",
            EscrowId = Guid.NewGuid(),
            EscrowStatus = EscrowStatus.Funded,
            Status = Booking.StatusImplemented,
            RequestMessage = "please come",
            ResponseMessage = "on my way",
            ProofFileUrl = "proof.jpg",
            InvoiceAmount = 150m,
            PaymentTransactionId = "txn-1",
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            RespondedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            ImplementedAt = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
            WorkStartedAt = new DateTime(2026, 8, 22, 16, 5, 0, DateTimeKind.Utc),
            ReleaseRequestedAt = new DateTime(2026, 8, 23, 1, 0, 0, DateTimeKind.Utc),
            RefundRequestedAt = new DateTime(2026, 8, 23, 2, 0, 0, DateTimeKind.Utc),
            CancelledAt = new DateTime(2026, 8, 23, 3, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc)
        };

        private static List<string> PublicPropertyNames(Type type) => type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();
    }
}
