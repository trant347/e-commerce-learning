using Microsoft.AspNetCore.Mvc;
using Moq;
using payment_service.Contracts;
using payment_service.Controllers;
using payment_service.Models;
using payment_service.Services;
using Xunit;

namespace payment_service.Tests
{
    /// <summary>
    /// Covers the <c>GET /api/payment/transaction/{sagaId}</c> endpoint added for
    /// PAYMENT_SAGA_SPEC.md's migration step 3.
    /// </summary>
    public class PaymentControllerTransactionLookupTests
    {
        [Fact]
        public async Task GetTransactionBySagaId_Found_ReturnsOkWithTransaction()
        {
            var sagaId = Guid.NewGuid();
            var transaction = new PaymentTransaction { SagaId = sagaId, Amount = 42m };
            var serviceMock = new Mock<IPaymentService>();
            var tokenServiceMock = new Mock<IPaymentMethodTokenService>();
            serviceMock.Setup(s => s.GetTransactionBySagaIdAsync(sagaId)).ReturnsAsync(transaction);
            var controller = new PaymentController(serviceMock.Object, tokenServiceMock.Object);

            var result = await controller.GetTransactionBySagaId(sagaId);

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Same(transaction, okResult.Value);
        }

        [Fact]
        public async Task GetTransactionBySagaId_NotFound_ReturnsNotFound()
        {
            var sagaId = Guid.NewGuid();
            var serviceMock = new Mock<IPaymentService>();
            var tokenServiceMock = new Mock<IPaymentMethodTokenService>();
            serviceMock.Setup(s => s.GetTransactionBySagaIdAsync(sagaId)).ReturnsAsync((PaymentTransaction?)null);
            var controller = new PaymentController(serviceMock.Object, tokenServiceMock.Object);

            var result = await controller.GetTransactionBySagaId(sagaId);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task TokenizePaymentMethod_ValidCard_ReturnsOpaqueToken()
        {
            var serviceMock = new Mock<IPaymentService>();
            var tokenServiceMock = new Mock<IPaymentMethodTokenService>();
            var expiresAt = DateTime.UtcNow.AddMinutes(5);
            tokenServiceMock
                .Setup(service => service.TokenizeAsync(
                    It.IsAny<CreditCardInfo>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentMethodTokenResponse
                {
                    PaymentMethodToken = "pmt_opaque",
                    ExpiresAt = expiresAt
                });
            var controller = new PaymentController(serviceMock.Object, tokenServiceMock.Object);

            var result = await controller.TokenizePaymentMethod(
                new CreditCardInfo
                {
                    CardNumber = "4111111111111111",
                    ExpiryDate = "12/30",
                    CVV = "123",
                    OwnerName = "Jane Doe"
                },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var response = Assert.IsType<PaymentMethodTokenResponse>(ok.Value);
            Assert.Equal("pmt_opaque", response.PaymentMethodToken);
            Assert.Equal(expiresAt, response.ExpiresAt);
        }

        [Fact]
        public async Task TokenizePaymentMethod_InvalidCard_ReturnsBadRequest()
        {
            var serviceMock = new Mock<IPaymentService>();
            var tokenServiceMock = new Mock<IPaymentMethodTokenService>();
            tokenServiceMock
                .Setup(service => service.TokenizeAsync(
                    It.IsAny<CreditCardInfo>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException("Card number is invalid."));
            var controller = new PaymentController(serviceMock.Object, tokenServiceMock.Object);

            var result = await controller.TokenizePaymentMethod(
                new CreditCardInfo
                {
                    CardNumber = "invalid",
                    ExpiryDate = "12/30",
                    CVV = "123",
                    OwnerName = "Jane Doe"
                },
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
